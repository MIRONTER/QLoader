using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using AdvancedSharpAdbClient.Models;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Notifications;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DynamicData;
using QSideloader.Models;
using QSideloader.Properties;
using QSideloader.Services;
using QSideloader.Utilities;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Serilog;

namespace QSideloader.ViewModels;

public class InstalledAppsViewModel : ViewModelBase, IActivatableViewModel
{
    private static readonly SemaphoreSlim RefreshSemaphoreSlim = new(1, 1);
    private readonly AdbService _adbService;
    private readonly SettingsData _sideloaderSettings;
    private readonly ReadOnlyObservableCollection<InstalledApp> _installedApps;
    private readonly SourceCache<InstalledApp, string> _installedAppsSourceCache = new(x => x.Name);
    private readonly ObservableAsPropertyHelper<bool> _isBusy;

    // Dummy constructor for XAML, do not use.
    public InstalledAppsViewModel() : this(false)
    {
    }

    public InstalledAppsViewModel(bool filterForDonations)
    {
        Activator = new ViewModelActivator();
        _adbService = AdbService.Instance;
        _sideloaderSettings = Globals.SideloaderSettings;
        Refresh = ReactiveCommand.CreateFromObservable<bool, Unit>(RefreshImpl);
        Refresh.IsExecuting.ToProperty(this, x => x.IsBusy, out _isBusy, false, RxApp.MainThreadScheduler);
        Donate = ReactiveCommand.CreateFromObservable(DonateImpl);
        DonateAll = ReactiveCommand.CreateFromObservable(DonateAllImpl);
        Ignore = ReactiveCommand.CreateFromObservable(IgnoreImpl);
        Uninstall = ReactiveCommand.CreateFromObservable(UninstallImpl);
        Extract = ReactiveCommand.CreateFromTask(ExtractImpl);

        // ReSharper disable once MoveLocalFunctionAfterJumpStatement
        Func<InstalledApp, bool> DonationFilter(bool isShowHiddenFromDonation)
        {
            return app => !app.IsHiddenFromDonation || isShowHiddenFromDonation;
        }

        var donationFilterPredicate = this.WhenAnyValue(x => x.IsShowHiddenFromDonation)
            .DistinctUntilChanged()
            .Select(DonationFilter);

        var cacheListBindPart = _installedAppsSourceCache.Connect()
            .RefCount()
            .ObserveOn(RxApp.MainThreadScheduler);
        var cacheListBindPart2 = filterForDonations
            ? cacheListBindPart.Filter(donationFilterPredicate)
            : cacheListBindPart.Filter(x => !x.IsKnown);
        var cacheListBind = cacheListBindPart2
            .SortBy(x => x.Name)
            .Bind(out _installedApps)
            .DisposeMany();
        this.WhenActivated(disposables =>
        {
            cacheListBind.Subscribe().DisposeWith(disposables);
            _adbService.WhenDeviceStateChanged.Subscribe(OnDeviceStateChanged).DisposeWith(disposables);
            _adbService.WhenPackageListChanged.Subscribe(_ => Refresh.Execute().Subscribe()).DisposeWith(disposables);
            Globals.MainWindowViewModel!.WhenGameDonated.Subscribe(_ => Refresh.Execute().Subscribe())
                .DisposeWith(disposables);
            Refresh.Execute().Subscribe();
            IsShowHiddenFromDonation = false;
        });
    }

    public ReactiveCommand<bool, Unit> Refresh { get; }
    public ReactiveCommand<Unit, Unit> Donate { get; }
    public ReactiveCommand<Unit, Unit> DonateAll { get; }
    public ReactiveCommand<Unit, Unit> Ignore { get; }
    public ReactiveCommand<Unit, Unit> Uninstall { get; }
    public ReactiveCommand<Unit, Unit> Extract { get; }
    public ReadOnlyObservableCollection<InstalledApp> InstalledApps => _installedApps;
    public bool IsBusy => _isBusy.Value;
    [Reactive] public bool IsDeviceConnected { get; private set; }
    [Reactive] public bool IsShowHiddenFromDonation { get; set; }

    public ViewModelActivator Activator { get; }

    private IObservable<Unit> RefreshImpl(bool rescan = false)
    {
        return Observable.StartAsync(async () =>
        {
            // Check whether refresh is already running
            if (RefreshSemaphoreSlim.CurrentCount == 0) return;
            await RefreshSemaphoreSlim.WaitAsync();
            try
            {
                await RefreshInstalledAppsAsync(rescan);
                Globals.MainWindowViewModel!.RefreshGameDonationBadge();
            }
            finally
            {
                RefreshSemaphoreSlim.Release();
            }
        });
    }

    private IObservable<Unit> DonateImpl()
    {
        return Observable.Start(() =>
        {
            if (!_adbService.IsDeviceConnected)
            {
                Log.Warning("InstalledAppsViewModel.DonateImpl: no device connection!");
                OnDeviceOffline();
                return;
            }

            var selectedApps = _installedAppsSourceCache.Items.Where(app => app.IsSelectedDonation).ToList();
            if (selectedApps.Count == 0)
            {
                Log.Information("No apps selected for donation");
                Globals.ShowNotification(Resources.Donate, Resources.NoAppsSelected, NotificationType.Information,
                    TimeSpan.FromSeconds(2));
                return;
            }

            foreach (var app in selectedApps)
            {
                app.IsSelectedDonation = false;
                app.PullAndUpload();
                Log.Information("Queued for donation: {ReleaseName}", app.Name);
            }
        });
    }

    private IObservable<Unit> DonateAllImpl()
    {
        return Observable.Start(() =>
        {
            if (!_adbService.IsDeviceConnected)
            {
                Log.Warning("InstalledAppsViewModel.DonateAllImpl: no device connection!");
                OnDeviceOffline();
                return;
            }

            Log.Information("Donating all eligible apps");
            var eligibleApps = _installedAppsSourceCache.Items.Where(app => !app.IsHiddenFromDonation).ToList();
            if (eligibleApps.Count == 0)
            {
                Log.Information("No apps to donate");
                Globals.ShowNotification(Resources.Donate, Resources.NoAppsToDonate, NotificationType.Information,
                    TimeSpan.FromSeconds(2));
                return;
            }

            foreach (var app in eligibleApps)
            {
                app.IsSelectedDonation = false;
                app.PullAndUpload();
                Log.Information("Queued for donation: {Name}", app.Name);
            }
        });
    }

    private IObservable<Unit> IgnoreImpl()
    {
        return Observable.StartAsync(async () =>
        {
            var selectedApps = _installedAppsSourceCache.Items.Where(app => app.IsSelectedDonation).ToList();
            if (selectedApps.Count == 0)
            {
                Log.Information("No apps selected to add to ignore list");
                Globals.ShowNotification(Resources.Ignore, Resources.NoAppsSelected, NotificationType.Information,
                    TimeSpan.FromSeconds(2));
                return;
            }

            var inverse =
                selectedApps.All(app => _sideloaderSettings.IgnoredDonationPackages.Contains(app.PackageName));
            var count = 0;
            foreach (var app in selectedApps)
            {
                app.IsSelectedDonation = false;
                switch (inverse)
                {
                    case false when !_sideloaderSettings.IgnoredDonationPackages.Contains(app.PackageName) &&
                                    !app.IsHiddenFromDonation:
                        _sideloaderSettings.IgnoredDonationPackages.Add(app.PackageName);
                        count++;
                        break;
                    case true when _sideloaderSettings.IgnoredDonationPackages.Contains(app.PackageName):
                        _sideloaderSettings.IgnoredDonationPackages.Remove(app.PackageName);
                        count++;
                        break;
                }
            }

            if (!inverse)
            {
                Log.Information("Added {Count} apps to ignore list", count);
                Globals.ShowNotification(Resources.Ignore, string.Format(Resources.AppsAddedToIgnore, count),
                    NotificationType.Success, TimeSpan.FromSeconds(2));
            }
            else
            {
                Log.Information("Removed {Count} apps from ignore list", count);
                Globals.ShowNotification(Resources.Ignore, Resources.AppsRemovedFromIgnore, NotificationType.Success,
                    TimeSpan.FromSeconds(2));
            }

            if (count == 0) return;
            await RefreshInstalledAppsAsync(true);
            Globals.MainWindowViewModel!.RefreshGameDonationBadge();
        });
    }

    private IObservable<Unit> UninstallImpl()
    {
        return Observable.Start(() =>
        {
            if (!_adbService.IsDeviceConnected)
            {
                Log.Warning("InstalledAppsViewModel.UninstallImpl: no device connection!");
                OnDeviceOffline();
                return;
            }

            var selectedApps = _installedAppsSourceCache.Items.Where(game => game.IsSelected).ToList();
            if (selectedApps.Count == 0)
            {
                Log.Information("No apps selected for uninstall");
                Globals.ShowNotification(Resources.Uninstall, Resources.NoAppsSelected, NotificationType.Information,
                    TimeSpan.FromSeconds(2));
                return;
            }

            foreach (var app in selectedApps)
            {
                app.IsSelected = false;
                app.Uninstall();
            }
        });
    }

    private async Task ExtractImpl()
    {
        if (!_adbService.IsDeviceConnected)
        {
            Log.Warning("InstalledAppsViewModel.ExtractImpl: no device connection!");
            OnDeviceOffline();
            return;
        }

        var selectedApps = _installedAppsSourceCache.Items.Where(game => game.IsSelected).ToList();
        if (selectedApps.Count == 0)
        {
            Log.Information("No apps selected for extraction");
            Globals.ShowNotification(Resources.Extract, Resources.NoAppsSelected, NotificationType.Information,
                TimeSpan.FromSeconds(2));
            return;
        }

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = desktop.MainWindow!;
            var result = await mainWindow.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = Resources.SelectDestinationFolder,
                SuggestedStartLocation =
                    await mainWindow.StorageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Desktop),
                AllowMultiple = false
            });
            if (result.Count > 0)
            {
                foreach (var app in selectedApps)
                {
                    var path = result[0].TryGetLocalPath();
                    if (!Directory.Exists(path))
                    {
                        Log.Error("Selected path for app extraction does not exist: {Path}", path);
                        return;
                    }

                    app.IsSelected = false;
                    app.Extract(path);
                }
            }
        }
    }

    private void OnDeviceStateChanged(DeviceState state)
    {
        // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault
        switch (state)
        {
            case DeviceState.Online:
                OnDeviceOnline();
                break;
            case DeviceState.Offline:
                OnDeviceOffline();
                break;
        }
    }

    private void OnDeviceOnline()
    {
        IsDeviceConnected = true;
        Refresh.Execute().Subscribe();
    }

    private void OnDeviceOffline()
    {
        IsDeviceConnected = false;
        Dispatcher.UIThread.InvokeAsync(_installedAppsSourceCache.Clear);
    }

    private async Task RefreshInstalledAppsAsync(bool rescan)
    {
        if (!_adbService.IsDeviceConnected)
        {
            Log.Warning("InstalledAppsViewModel.RefreshInstalledApps: no device connection!");
            OnDeviceOffline();
            return;
        }

        IsDeviceConnected = true;
        if (rescan && _adbService.Device is not null) await _adbService.Device.RefreshInstalledAppsAsync();
        while (_adbService.Device!.IsRefreshingInstalledGames)
        {
            await Task.Delay(100);
            if (_adbService.Device is null)
                return;
        }

        _installedAppsSourceCache.Edit(innerCache =>
        {
            innerCache.Clear();
            innerCache.AddOrUpdate(_adbService.Device!.InstalledApps);
        });
    }
}