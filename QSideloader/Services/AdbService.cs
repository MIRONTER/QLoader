using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.DeviceCommands;
using AdvancedSharpAdbClient.Models;
using AdvancedSharpAdbClient.Models.DeviceCommands;
using AdvancedSharpAdbClient.Receivers;
using AsyncAwaitBestPractices;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using CliWrap;
using CliWrap.Buffered;
using CliWrap.Exceptions;
using QSideloader.Common;
using QSideloader.Exceptions;
using QSideloader.Models;
using QSideloader.Properties;
using QSideloader.Utilities;
using QSideloader.ViewModels;
using Serilog;
using Serilog.Events;
using SerilogTimings;
using Tmds.MDns;
using UnixFileType = AdvancedSharpAdbClient.Models.UnixFileType;

namespace QSideloader.Services;

/// <summary>
///     Service for all ADB operations.
/// </summary>
public partial class AdbService
{
    private static readonly SemaphoreSlim DeviceSemaphoreSlim = new(1, 1);
    private static readonly SemaphoreSlim DeviceListSemaphoreSlim = new(1, 1);
    private static readonly SemaphoreSlim BackupListSemaphoreSlim = new(1, 1);
    private static readonly SemaphoreSlim AdbServerSemaphoreSlim = new(1, 1);
    private static readonly SemaphoreSlim AdbDeviceMonitorSemaphoreSlim = new(1, 1);
    private static readonly SemaphoreSlim PackageOperationSemaphoreSlim = new(1, 1);
    private static readonly string[] MdnsServiceTypes = {"_adb-tls-connect._tcp", "_adb_secure_connect._tcp"};
    private readonly AdbServerClient _adb;
    private readonly Subject<Unit> _backupListChangeSubject = new();
    private readonly Subject<DeviceState> _deviceStateChangeSubject = new();
    private readonly Subject<List<AdbDevice>> _deviceListChangeSubject = new();
    private readonly Subject<Unit> _packageListChangeSubject = new();
    private readonly SettingsData _sideloaderSettings;
    private readonly List<AdbDevice> _deviceList = [];
    private readonly List<Backup> _backupList = [];
    private List<DeviceData> _unauthorizedDeviceList = [];
    private DeviceMonitor? _deviceMonitor;
    private bool _forcePreferWireless;
    private bool _firstDeviceSearch = true;

    static AdbService()
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="AdbService" /> class.
    /// </summary>
    private AdbService()
    {
        _sideloaderSettings = Globals.SideloaderSettings;
        _adb = new AdbServerClient();
        if (Design.IsDesignMode) return;
        // This event may fire when DeviceSemaphoreSlim is taken, so we need to use TaskPool threads
        WhenDeviceListChanged.ObserveOn(Scheduler.Default).Subscribe(_ => Task.Run(CheckConnectionPreferenceAsync));
        Task.Run(async () =>
        {
            RefreshBackupList();
            await CheckDeviceConnectionAsync();
            var lastWirelessAdbHost = _sideloaderSettings.LastWirelessAdbHost;
            if (!string.IsNullOrEmpty(lastWirelessAdbHost))
                await TryConnectWirelessAdbAsync(lastWirelessAdbHost, remember: true);
            StartMdnsBrowser();
        }).SafeFireAndForget();
    }

    public static AdbService Instance { get; } = new();

    public AdbDevice? Device { get; private set; }

    public IReadOnlyList<AdbDevice> DeviceList => _deviceList.AsReadOnly();
    public IEnumerable<Backup> BackupList => _backupList.AsReadOnly();
    
    public bool IsDeviceConnected => Device is not null && Device.State == DeviceState.Online;

    public IObservable<Unit> WhenBackupListChanged => _backupListChangeSubject.AsObservable();
    public IObservable<DeviceState> WhenDeviceStateChanged => _deviceStateChangeSubject.AsObservable();
    public IObservable<Unit> WhenPackageListChanged => _packageListChangeSubject.AsObservable();
    public IObservable<List<AdbDevice>> WhenDeviceListChanged => _deviceListChangeSubject.AsObservable();

    /// <summary>
    ///     Runs a shell command on the device.
    /// </summary>
    /// <param name="device">Device to run the command on.</param>
    /// <param name="command">Command to run.</param>
    /// <param name="logCommand">Should log the command and result.</param>
    /// <returns>Output of executed command.</returns>
    /// <exception cref="AdbServiceException">Thrown if an error occured when running the command.</exception>
    private string RunShellCommand(DeviceData device, string command, bool logCommand = false)
    {
        ConsoleOutputReceiver receiver = new();
        try
        {
            if (logCommand)
                Log.Debug("Running shell command: {Command}", command);
            _adb.AdbClient.ExecuteRemoteCommand(command, device, receiver);
        }
        catch (Exception e)
        {
            throw new AdbServiceException("Error running shell command: " + command, e);
        }

        var result = receiver.ToString().Trim();
        if (!logCommand) return result;
        if (!string.IsNullOrWhiteSpace(result))
            Log.Debug("Command returned: {Result}", result);
        return result;
    }
    
    /// <summary>
    ///     Runs a shell command on the device.
    /// </summary>
    /// <param name="device">Device to run the command on.</param>
    /// <param name="command">Command to run.</param>
    /// <param name="logCommand">Should log the command and result.</param>
    /// <returns>Output of executed command.</returns>
    /// <exception cref="AdbServiceException">Thrown if an error occured when running the command.</exception>
    private async Task<string> RunShellCommandAsync(DeviceData device, string command, bool logCommand = false)
    {
        ConsoleOutputReceiver receiver = new();
        try
        {
            if (logCommand)
                Log.Debug("Running shell command: {Command}", command);
            await _adb.AdbClient.ExecuteRemoteCommandAsync(command, device, receiver);
        }
        catch (Exception e)
        {
            throw new AdbServiceException("Error running shell command: " + command, e);
        }

        var result = receiver.ToString().Trim();
        if (!logCommand) return result;
        if (!string.IsNullOrWhiteSpace(result))
            Log.Debug("Command returned: {Result}", result);
        return result;
    }

    /// <summary>
    ///     Checks that device is connected and if not, tries to find an appropriate device and connect to it.
    /// </summary>
    /// <param name="assumeOffline">Assume that current device is offline and run full search</param>
    /// <returns>
    ///     <c>true</c> if device is connected, <c>false</c> if no device was found.
    /// </returns>
    public async Task<bool> CheckDeviceConnectionAsync(bool assumeOffline = false)
    {
        if (Design.IsDesignMode) return false;

        await EnsureADBRunningAsync();

        var connectionStatus = false;
        await DeviceSemaphoreSlim.WaitAsync();
        try
        {
            if (Device is not null && !assumeOffline) connectionStatus = await Device.PingAsync();

            if (!connectionStatus)
            {
                AdbDevice? foundDevice = null;
                await RefreshDeviceListAsync();
                foreach (var device in DeviceList)
                {
                    connectionStatus = await device.PingAsync();
                    if (!connectionStatus) continue;
                    foundDevice = device;
                    break;
                }

                if ((Device is null || Device?.Serial != foundDevice?.Serial) && foundDevice is not null &&
                    connectionStatus)
                    OnDeviceOnline(foundDevice);
                else if (Device is not null && !connectionStatus) OnDeviceOffline(Device);
                if (DeviceList.Count == 0)
                {
                    if (_unauthorizedDeviceList.Count > 0)
                        OnDeviceUnauthorized(_unauthorizedDeviceList[0]);
                    else if (_firstDeviceSearch)
                        OnDeviceOffline(null);
                }
            }
        }
        catch (Exception e)
        {
            Log.Error(e, "Error while checking device connection");
            Globals.ShowErrorNotification(e, Resources.ErrorCheckingDeviceConnection);
        }
        finally
        {
            _firstDeviceSearch = false;
            DeviceSemaphoreSlim.Release();
        }
        
        return connectionStatus;
    }

    /// <summary>
    ///     Refreshes the device list.
    /// </summary>
    public async Task RefreshDeviceListAsync()
    {
        try
        {
            var skipScan = DeviceListSemaphoreSlim.CurrentCount == 0;
            await DeviceListSemaphoreSlim.WaitAsync();
            try
            {
                await EnsureADBRunningAsync();
            }
            catch
            {
                return;
            }

            if (skipScan) return;
            var (deviceList, unauthorizedDevices) = await GetOculusDevicesAsync();
            _unauthorizedDeviceList = unauthorizedDevices;
            var addedDevices = deviceList.Where(d => _deviceList.All(x => x.Serial != d.Serial)).ToList();
            var removedDevices = _deviceList.Where(d => deviceList.All(x => x.Serial != d.Serial)).ToList();
            if (addedDevices.Count == 0 && removedDevices.Count == 0) return;

            foreach (var device in addedDevices)
            {
                device.InitializeAsync().SafeFireAndForget();
                _deviceList.Add(device);
            }

            _deviceList.RemoveAll(d => removedDevices.Any(x => x.Serial == d.Serial));
            _deviceListChangeSubject.OnNext(deviceList);
        }
        finally
        {
            DeviceListSemaphoreSlim.Release();
        }
    }

    /// <summary>
    ///     Checks the current connection type and switches to preferred type if necessary.
    /// </summary>
    private Task CheckConnectionPreferenceAsync()
    {
        if (Device is null) return Task.CompletedTask;
        var preferredConnectionType = _sideloaderSettings.PreferredConnectionType;
        switch (Device.IsWireless)
        {
            case true when !_forcePreferWireless && preferredConnectionType == "USB":
            {
                if (DeviceList.FirstOrDefault(x => x.TrueSerial == Device.TrueSerial && !x.IsWireless) is
                    { } preferredDevice)
                {
                    Log.Information("Auto-switching to preferred connection type ({ConnectionType})",
                        preferredConnectionType);
                    return TrySwitchDeviceAsync(preferredDevice);
                }

                break;
            }
            case false when _forcePreferWireless || preferredConnectionType == "Wireless":
            {
                if (DeviceList.FirstOrDefault(x => x.TrueSerial == Device.TrueSerial && x.IsWireless) is
                    { } preferredDevice)
                {
                    Log.Information("Auto-switching to preferred connection type ({ConnectionType})",
                        preferredConnectionType);
                    return TrySwitchDeviceAsync(preferredDevice);
                }

                break;
            }
        }

        return Task.CompletedTask;
    }

    private async Task<(bool running, bool outdatedVersion)> CheckADBRunningAsync()
    {
        try
        {
            var adbServerStatus = await _adb.AdbServer.GetStatusAsync();
            if (adbServerStatus.IsRunning)
            {
                var requiredAdbVersion = new Version("1.0.40");
                if (adbServerStatus.Version < requiredAdbVersion)
                    return (true, true);
                await StartDeviceMonitorAsync(false);
                return (true, false);

            }
        }
        catch
        {
            Log.Warning("Failed to check ADB server status");
        }

        return (false, false);
    }
    
    /// <summary>
    ///     Ensures that ADB server is running.
    /// </summary>
    /// <exception cref="AdbServiceException">Thrown if ADB server start failed.</exception>
    private async Task EnsureADBRunningAsync()
    {
        try
        {
            await AdbServerSemaphoreSlim.WaitAsync();
            var (running, outdatedVersion) = await CheckADBRunningAsync();
            if (running) return;

            Log.Information("Starting ADB server");

            // Workaround for issues with AdbServer.StartServer
            try
            {
                var adbPath = PathHelper.AdbPath;
                if (outdatedVersion)
                    try
                    {
                        Log.Warning("ADB server version is too old, restarting");
                        await Cli.Wrap(adbPath)
                            .WithArguments("kill-server")
                            .ExecuteBufferedAsync();
                    }
                    catch (CommandExecutionException)
                    {
                        Array.ForEach(Process.GetProcessesByName("adb"), p => p.Kill());
                    }

                await Cli.Wrap(adbPath)
                    .WithArguments("start-server")
                    .ExecuteBufferedAsync();
            }
            catch (Exception e)
            {
                Log.Error(e, "Failed to start ADB server");
                Globals.ShowErrorNotification(e, Resources.FailedToStartAdbServer);
                throw new AdbServiceException("Failed to start ADB server", e);
            }

            if (!(await _adb.AdbServer.GetStatusAsync()).IsRunning)
            {
                Log.Error("Failed to start ADB server");
                Globals.ShowNotification("ADB", Resources.FailedToStartAdbServer, NotificationType.Error,
                    TimeSpan.Zero);
                throw new AdbServiceException("Failed to start ADB server");
            }

            await _adb.AdbClient.ConnectAsync("127.0.0.1:62001");
            Log.Information("Started ADB server");
            await StartDeviceMonitorAsync(true);
        }
        finally
        {
            AdbServerSemaphoreSlim.Release();
        }
    }

    /// <summary>
    /// Restarts the ADB server.
    /// </summary>
    public async Task RestartAdbServerAsync()
    {
        Log.Warning("Restarting ADB server");
        try
        {
            var adbPath = PathHelper.AdbPath;
            var adbServerStatus = await _adb.AdbServer.GetStatusAsync();
            if (adbServerStatus.IsRunning)
                try
                {
                    await Cli.Wrap(adbPath)
                        .WithArguments("kill-server")
                        .ExecuteBufferedAsync();
                }
                catch (CommandExecutionException)
                {
                    Array.ForEach(Process.GetProcessesByName("adb"), p => p.Kill());
                }
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to kill ADB server");
        }


        OnDeviceOffline(Device);
        _deviceList.Clear();
        _deviceListChangeSubject.OnNext(_deviceList);
        await CheckDeviceConnectionAsync();
    }

    /// <summary>
    /// Resets ADB keys and restarts the ADB server.
    /// </summary>
    public void ResetAdbKeys()
    {
        Log.Warning("Resetting ADB keys");
        try
        {
            var userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var adbKeyPath = Path.Combine(userProfilePath, ".android", "adbkey");
            var adbKeyPubPath = Path.Combine(userProfilePath, ".android", "adbkey.pub");
            File.Delete(adbKeyPath);
            File.Delete(adbKeyPubPath);
        }
        catch (Exception e)
        {
            Log.Warning(e, "Failed to delete ADB keys");
        }
        finally
        {
            Task.Run(RestartAdbServerAsync);
        }
    }

    /// <summary>
    /// Reconnects to the current device.
    /// </summary>
    public void ReconnectDevice()
    {
        Log.Information("Reconnecting device");
        OnDeviceOffline(Device);
        _deviceList.Clear();
        _deviceListChangeSubject.OnNext(_deviceList);
        Task.Run(async () => await CheckDeviceConnectionAsync());
    }

    /// <summary>
    ///     (Re)starts <see cref="DeviceMonitor" />
    /// </summary>
    /// <param name="restart">Should restart device monitor.</param>
    private async Task StartDeviceMonitorAsync(bool restart)
    {
        await AdbDeviceMonitorSemaphoreSlim.WaitAsync();
        try
        {
            if (_deviceMonitor is null || restart)
            {
                _deviceMonitor = new DeviceMonitor(new AdbSocket(new IPEndPoint(IPAddress.Loopback, 5037)));
            }

            if (_deviceMonitor.IsRunning) return;
            _deviceMonitor.DeviceChanged += (_, args) => Task.Run(async () => await OnDeviceChangedAsync(args));
            await _deviceMonitor.StartAsync();
            Log.Debug("Started device monitor");
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to start device monitor");
        }
        finally
        {
            AdbDeviceMonitorSemaphoreSlim.Release();
        }
    }

    private void StartMdnsBrowser()
    {
        try
        {
            var serviceBrowser = new ServiceBrowser();
            serviceBrowser.ServiceAdded += OnServiceAdded;
            serviceBrowser.StartBrowse(MdnsServiceTypes);
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to start mDNS browser");
        }

        return;

        async void OnServiceAdded(object? _, ServiceAnnouncementEventArgs e)
        {
            var service = e.Announcement;
            if (!service.Addresses.Any()) return;
            var address = service.Addresses.First().ToString();
            Log.Information("Found Wireless ADB service: {Host}:{Port}, trying to connect", address, service.Port);
            await TryConnectWirelessAdbAsync(address, service.Port);
        }
    }

    /// <summary>
    ///     Method for handling <see cref="DeviceMonitor.DeviceChanged" /> event.
    /// </summary>
    private async Task OnDeviceChangedAsync(DeviceDataEventArgs e)
    {
        Log.Debug("OnDeviceChanged: got event. Device State = {DeviceState}", e.Device.State);
        switch (e.Device.State)
        {
            case DeviceState.Online:
                if (DeviceList.All(x => x.Serial != e.Device.Serial)) await RefreshDeviceListAsync();

                await CheckDeviceConnectionAsync();
                break;
            case DeviceState.Offline:
                if (e.Device.Serial == Device?.Serial)
                {
                    await CheckDeviceConnectionAsync(true);
                }
                else
                {
                    await RefreshDeviceListAsync();
                    await CheckDeviceConnectionAsync();
                }

                break;
            case DeviceState.Unauthorized:
                await CheckDeviceConnectionAsync();
                break;
            case DeviceState.BootLoader:
            case DeviceState.Host:
            case DeviceState.Recovery:
            case DeviceState.NoPermissions:
            case DeviceState.Sideload:
            case DeviceState.Authorizing:
            case DeviceState.Unknown:
            default:
                break;
        }
    }

    /// <summary>
    ///     Method that is called when current device is disconnected.
    /// </summary>
    /// <param name="device">Disconnected device.</param>
    private void OnDeviceOffline(AdbDevice? device)
    {
        if (device is not null)
            Log.Information("Device {Device} disconnected", device);
        Device = null;
        NotifyDeviceStateChange(DeviceState.Offline);
    }

    /// <summary>
    ///     Method that is called when a device is connected.
    /// </summary>
    /// <param name="device">Connected device.</param>
    private void OnDeviceOnline(AdbDevice device)
    {
        Log.Information("Connected to device {Device}", device);
        device.State = DeviceState.Online;
        Device = device;
        //if (!FirstDeviceSearch)
        NotifyDeviceStateChange(DeviceState.Online);
    }

    /// <summary>
    ///     Method that is called when adb is not authorized for debugging of the device.
    /// </summary>
    /// <param name="device">Device in <see cref="DeviceState.Unauthorized"/> state.</param>
    /// <remarks>This method should be called only when there are no other usable devices.</remarks>
    private void OnDeviceUnauthorized(DeviceData device)
    {
        Log.Warning("Not authorized for debugging of device {Device}", GetHashedId(device.Serial));
        device.State = DeviceState.Unauthorized;
        NotifyDeviceStateChange(DeviceState.Unauthorized);
    }

    /// <summary>
    ///     Sends new device state to <see cref="WhenDeviceStateChanged"/> subscribers.
    /// </summary>
    /// <param name="state">Device state to send.</param>
    private void NotifyDeviceStateChange(DeviceState state)
    {
        try
        {
            _deviceStateChangeSubject.OnNext(state);
        }
        catch (Exception e)
        {
            Log.Error(e, "Error while notifying device state change");
            Globals.ShowErrorNotification(e, Resources.ErrorNotifyingDeviceStateChange);
        }
    }

    /// <summary>
    ///     Gets hashed ID from device serial number.
    /// </summary>
    /// <param name="deviceSerial">Device serial to convert.</param>
    /// <returns>Hashed ID as <see cref="string" />.</returns>
    /// <remarks><see cref="Hwid.GetHwid"/> is used as salt.</remarks>
    private static string GetHashedId(string deviceSerial)
    {
        var hwid = Hwid.GetHwid(false);
        var saltedSerial = hwid + deviceSerial;
        var hashedId = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(saltedSerial)))[..16];
        return hashedId;
    }

    /// <summary>
    ///     Gets the list of Oculus devices.
    /// </summary>
    /// <returns><see cref="List{T}" /> of <see cref="AdbDevice" />.</returns>
    private async Task<(List<AdbDevice> oculusDevices, List<DeviceData> unauthorizedDevices)> GetOculusDevicesAsync()
    {
        Log.Debug("Searching for devices");
        var unauthorizedDevices = new List<DeviceData>();
        List<AdbDevice> oculusDeviceList = [];
        var deviceList = (await _adb.AdbClient.GetDevicesAsync()).ToList();
        if (deviceList.Count == 0)
        {
            Log.Debug("No ADB devices found");
            return (oculusDeviceList, unauthorizedDevices);
        }

        foreach (var device in deviceList)
        {
            var hashedDeviceId = GetHashedId(device.Serial);
            if (OculusProductsInfo.IsKnownProduct(device.Product))
            {
                try
                {
                    var adbDevice = new AdbDevice(device, this);
                    oculusDeviceList.Add(adbDevice);
                    Log.Information("Found Oculus Quest device: {Device}", adbDevice);
                }
                catch
                {
                    // ignored
                }

                continue;
            }

            // ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault
            switch (device.State)
            {
                case DeviceState.Online:
                    Log.Information("Not an Oculus Quest device! ({HashedDeviceId} - {Product})", hashedDeviceId,
                        device.Product);
                    break;
                case DeviceState.Unauthorized:
                    unauthorizedDevices.Add(device);
                    Log.Information("Found device in {State} state! ({HashedDeviceId})", device.State, hashedDeviceId);
                    break;
                default:
                    Log.Information("Found device in {State} state! ({HashedDeviceId})", device.State, hashedDeviceId);
                    break;
            }
        }

        if (oculusDeviceList.Count != 0)
            return (_sideloaderSettings.PreferredConnectionType switch
            {
                "USB" => oculusDeviceList.OrderBy(x => x.IsWireless).ToList(),
                "Wireless" => oculusDeviceList.OrderByDescending(x => x.IsWireless).ToList(),
                _ => oculusDeviceList
            }, unauthorizedDevices);
        Log.Debug("No Oculus devices found");
        return (oculusDeviceList, unauthorizedDevices);
    }

    /// <summary>
    ///     Tries to switch to another device.
    /// </summary>
    /// <param name="device">Device to switch to.</param>
    public async Task TrySwitchDeviceAsync(AdbDevice device)
    {
        if (device.Serial == Device?.Serial) return;
        if (device.State != DeviceState.Online || !await device.PingAsync())
        {
            Log.Warning("Attempted switch to offline device {Device}", device);
            await RefreshDeviceListAsync();
            return;
        }

        await DeviceSemaphoreSlim.WaitAsync();
        try
        {
            Log.Information("Switching to device {Device}", device);
            OnDeviceOnline(device);
        }
        finally
        {
            DeviceSemaphoreSlim.Release();
        }
    }

    /// <summary>
    ///     Enables Wireless ADB on the device and tries to connect to it.
    /// </summary>
    /// <param name="device">Device to enable Wireless ADB on.</param>
    public async Task EnableWirelessAdbAsync(AdbDevice device)
    {
        Log.Information("Enabling Wireless ADB");
        if (device.IsWireless)
        {
            Log.Warning("Device {Device} is already wireless!", device);
            return;
        }

        try
        {
            _forcePreferWireless = true;
            Observable.Timer(TimeSpan.FromSeconds(10)).Subscribe(_ => _forcePreferWireless = false);
            var host = await device.EnableWirelessAdbAsync();
            await Task.Delay(1000);
            await TryConnectWirelessAdbAsync(host, 5555, true, true);
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to enable Wireless ADB");
            Globals.ShowErrorNotification(e, Resources.FailedToEnableWirelessAdb);
        }
    }

    /// <summary>
    ///     Tries to connect to the wireless adb host.
    /// </summary>
    /// <param name="host">Host to connect to.</param>
    /// <param name="port">Port to connect to.</param>
    /// <param name="remember">Whether to remember the host for auto connect (port 5555 will be used).</param>
    /// <param name="silent">Don't send log messages.</param>
    private async Task TryConnectWirelessAdbAsync(string host, int port = 5555, bool remember = false,
        bool silent = false)
    {
        await EnsureADBRunningAsync();
        if (DeviceList.Any(x => x.Serial.Contains(host)))
        {
            if (!silent)
                Log.Debug("Wireless device on {Host} is already connected, skipping", host);
            return;
        }

        if (!silent)
            Log.Debug("Trying to connect to wireless device, host {Host}, port {Port}", host, port);

        try
        {
            var result = await _adb.AdbClient.ConnectAsync(host, port,
                new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);

            if (result.Contains($"connected to {host}:{port}"))
            {
                await RefreshDeviceListAsync();
                if (remember)
                    _sideloaderSettings.LastWirelessAdbHost = host;
                return;
            }
        }
        catch (OperationCanceledException)
        {
            if (remember)
                _sideloaderSettings.LastWirelessAdbHost = "";
            Log.Warning("Wireless device connection timed out");
        }
        catch
        {
            if (remember)
                _sideloaderSettings.LastWirelessAdbHost = "";
        }

        if (!silent)
            Log.Warning("Couldn't connect to wireless device");
    }

    /// <summary>
    ///     Takes the package operation lock.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public static Task TakePackageOperationLockAsync(CancellationToken ct = default)
    {
        return PackageOperationSemaphoreSlim.WaitAsync(ct);
    }

    /// <summary>
    ///     Releases the package operation lock.
    /// </summary>
    public static void ReleasePackageOperationLock()
    {
        PackageOperationSemaphoreSlim.Release();
    }

    /// <summary>
    /// Gets output of <c>adb devices</c> command.
    /// </summary>
    /// <returns>Output of the command or error message.</returns>
    public static async Task<string> GetDevicesStringAsync()
    {
        try
        {
            var commandResult = await Cli.Wrap(PathHelper.AdbPath)
                .WithArguments("devices")
                .ExecuteBufferedAsync();
            return commandResult.StandardOutput + commandResult.StandardError;
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to get adb devices output");
            return "Failed to get adb devices output";
        }
    }


    public void RefreshBackupList()
    {
        BackupListSemaphoreSlim.Wait();
        try
        {
            Log.Debug("Refreshing backup list");
            var backupDirs = Directory.GetDirectories(_sideloaderSettings.BackupsLocation);
            // tuple of (backup name, backup path)
            var backups = backupDirs.Select(x => (name: Path.GetFileName(x), path: x)).ToList();
            var toRemove = _backupList.Where(b => backups.Select(x => x.name).Contains(b.Name) == false).ToList();
            var toAdd = backups.Where(b => _backupList.Select(x => x.Name).Contains(b.name) == false).ToList();
            foreach (var backup in toRemove)
                _backupList.Remove(backup);
            foreach (var backup in toAdd)
                try
                {
                    _backupList.Add(new Backup(backup.path));
                }
                catch (Exception e)
                {
                    Log.Warning(e, "Failed to add backup {Backup}", backup);
                }

            if (toRemove.Count > 0 || toAdd.Count > 0)
                _backupListChangeSubject.OnNext(Unit.Default);
        }
        finally
        {
            BackupListSemaphoreSlim.Release();
        }
    }

    /// <summary>
    ///     Helper class to store <see cref="AdbClient" />-<see cref="AdbServer" /> pair.
    /// </summary>
    private class AdbServerClient
    {
        public AdbClient AdbClient { get; } = new();
        public AdbServer AdbServer { get; } = new();
    }


    /// <summary>
    ///     Adb device class for device-specific operations.
    /// </summary>
    public partial class AdbDevice : DeviceData
    {
        private readonly SemaphoreSlim _deviceInfoSemaphoreSlim = new(1, 1);
        private readonly SemaphoreSlim _packagesSemaphoreSlim = new(1, 1);
        private readonly AdbServerClient _adb;
        private readonly AdbService _adbService;
        private readonly SettingsData _sideloaderSettings;
        private readonly DownloaderService _downloaderService;

        /// <summary>
        ///     Initializes a new instance of the <see cref="AdbDevice" /> class.
        /// </summary>
        public AdbDevice(DeviceData deviceData, AdbService adbService)
        {
            _adbService = adbService;
            _adb = adbService._adb;
            _sideloaderSettings = Globals.SideloaderSettings;
            _downloaderService = DownloaderService.Instance;

            Serial = deviceData.Serial;
            IsWireless = deviceData.Serial.Contains(':');
            try
            {
                // corrected serial for wireless connection
                try
                {
                    TrueSerial = _adbService.RunShellCommand(deviceData, "getprop ro.boot.serialno");
                }
                catch
                {
                    TrueSerial = Serial;
                }
            }
            catch
            {
                // ignored
            }

            State = deviceData.State;
            Model = deviceData.Model;
            Product = deviceData.Product;
            Name = deviceData.Name;
            Features = deviceData.Features;
            Usb = deviceData.Usb;
            TransportId = deviceData.TransportId;
            Message = deviceData.Message;

            var modelProps = OculusProductsInfo.GetProductInfo(deviceData.Product);
            FriendlyName = modelProps.Name;
            ProductType = modelProps.Type;
            SupportedRefreshRates = modelProps.SupportedRefreshRates;


            HashedId = GetHashedId(TrueSerial ?? Serial);

            PackageManager = new PackageManager(_adb.AdbClient, this, arguments: ["-3"]);

            var bootCompleted = _adbService.RunShellCommand(deviceData, "getprop sys.boot_completed");
            if (!bootCompleted.Contains('1'))
                Log.Warning("Device {HashedId} has not finished booting yet", HashedId);

            if (IsWireless)
                Task.Run(ApplyWirelessFixAsync);
        }

        private PackageManager PackageManager { get; }
        public float SpaceUsed { get; private set; }
        public float SpaceFree { get; private set; }
        public float BatteryLevel { get; private set; }
        public List<(string packageName, VersionInfo? versionInfo)> InstalledPackages { get; } = [];
        public List<InstalledGame> InstalledGames { get; private set; } = [];
        public List<InstalledApp> InstalledApps { get; private set; } = [];
        public bool IsRefreshingInstalledGames => _packagesSemaphoreSlim.CurrentCount == 0 || InstalledPackages.Count == 0;
        public string FriendlyName { get; }
        public OculusProductType ProductType { get; }
        public IEnumerable<int> SupportedRefreshRates { get; private set; }

        /// <summary>
        /// Stores hashed id derived from serial.
        /// </summary>
        private string HashedId { get; }

        /// <summary>
        /// Stores true device serial even if it's a wireless connection.
        /// </summary>
        public string? TrueSerial { get; }

        public bool IsWireless { get; }

        /// <summary>
        ///     Overrides the default device name with hashed serial
        /// </summary>
        /// <returns>Device name as <see cref="string" /></returns>
        public override string ToString()
        {
            return IsWireless ? $"{HashedId} (wireless)" : HashedId;
        }

        public async Task InitializeAsync()
        {
            // Check if the device is running Android 12 and KeyMapper is installed
            // This combination causes serious issues with the OS, so try to uninstall KeyMapper automatically
            await CheckKeyMapperAsync();
            await CleanLeftoverApksAsync();
            await OnPackageListChangedAsync();
        }

        /// <summary>
        ///     Runs a shell command on the device.
        /// </summary>
        /// <param name="command">Command to run.</param>
        /// <param name="logCommand">Should log the command and result.</param>
        /// <returns>Output of executed command.</returns>
        /// <exception cref="AdbServiceException">Thrown if an error occured when running the command.</exception>
        public Task<string> RunShellCommandAsync(string command, bool logCommand = false)
        {
            return _adbService.RunShellCommandAsync(this, command, logCommand);
        }

        /// <summary>
        ///     Wakes up the device by sending a power button key event.
        /// </summary>
        private async Task WakeAsync()
        {
            try
            {
                await RunShellCommandAsync("input keyevent KEYCODE_WAKEUP");
            }
            catch (Exception e)
            {
                Log.Warning(e, "Failed to send wake command to device {Device}", this);
            }
        }

        /// <summary>
        ///     Pings the device to check if it is still connected and responding.
        /// </summary>
        /// <returns>
        ///     <c>true</c> if device responded, <c>false</c> otherwise.
        /// </returns>
        public async Task<bool> PingAsync()
        {
            await WakeAsync();
            if (State != DeviceState.Online) return false;
            try
            {
                return (await RunShellCommandAsync("echo 1")).Trim() == "1";
            }
            catch (Exception e)
            {
                Log.Warning(e, "Failed to ping device {Device}", this);
            }

            return false;
        }

        /// <summary>
        ///     Refreshes the <see cref="InstalledPackages" /> list
        /// </summary>
        public async Task RefreshInstalledPackagesAsync()
        {
            using var op = Operation.At(LogEventLevel.Debug).Begin("Refreshing installed packages on {Device}", this);
            var skip = _packagesSemaphoreSlim.CurrentCount == 0;
            await _packagesSemaphoreSlim.WaitAsync();
            try
            {
                if (skip)
                {
                    op.Cancel();
                    return;
                }

                Log.Debug("Refreshing list of installed packages on {Device}", this);
                await PackageManager.RefreshPackagesAsync();
                for (var i = 0; i < InstalledPackages.Count; i++)
                {
                    var package = InstalledPackages[i];
                    if (PackageManager.Packages.ContainsKey(package.packageName))
                    {
                        package.versionInfo = await PackageManager.GetVersionInfoAsync(package.packageName);
                        InstalledPackages[i] = package;
                    }
                    else
                    {
                        InstalledPackages.Remove(package);
                    }
                }

                foreach (var package in PackageManager.Packages.Keys.Where(package =>
                             InstalledPackages.All(x => x.packageName != package)).ToList())
                    InstalledPackages.Add((package, await PackageManager.GetVersionInfoAsync(package)));
                op.Complete();
            }
            finally
            {
                _packagesSemaphoreSlim.Release();
            }
        }

        /// <summary>
        ///    Gets the storage stats from the device.
        /// </summary>
        /// <returns>Storage stats as a tuple of (space total, space free) in bytes.</returns>
        private async Task<(long spaceTotal, long spaceFree)> GetSpaceStatsAsync()
        {
            string? statOutput = null;
            try
            {
                statOutput = await RunShellCommandAsync("stat -fc %S:%b:%a /data");
                var statOutputSplit = statOutput.Split(':');
                var blockSize = long.Parse(statOutputSplit[0]);
                var totalBlocks = long.Parse(statOutputSplit[1]);
                var availableBlocks = long.Parse(statOutputSplit[2]);
                return (totalBlocks * blockSize, availableBlocks * blockSize);
            }
            catch (Exception e)
            {
                Log.Error(e, "Failed to get storage stats from stat output: {StatOutput}", statOutput);
                throw;
            }
        }

        /// <summary>
        ///     Refreshes device info.
        /// </summary>
        /// <remarks>
        ///     If this method is called while another device info refresh is in progress,
        ///     the call will wait until the other refresh is finished and then return.
        /// </remarks>
        public async Task RefreshInfoAsync()
        {
            using var op = Operation.At(LogEventLevel.Debug, LogEventLevel.Error).Begin("Refreshing device info");
            // Check whether refresh is already running
            var alreadyRefreshing = _deviceInfoSemaphoreSlim.CurrentCount < 1;
            await _deviceInfoSemaphoreSlim.WaitAsync();
            var bootCompleted = await _adbService.RunShellCommandAsync(this, "getprop sys.boot_completed") == "1";
            if (!bootCompleted)
            {
                Log.Warning("Device {Device} has not finished booting yet, waiting before refreshing info", this);
            }
            while (!bootCompleted)
            {
                bootCompleted = await _adbService.RunShellCommandAsync(this, "getprop sys.boot_completed") == "1";
                await Task.Delay(200);
            }
            string? dumpsysOutput = null;
            try
            {
                // If device info has just been refreshed we can skip
                if (alreadyRefreshing)
                {
                    op.Cancel();
                    return;
                }

                var error = false;

                // Get battery level
                try
                {
                    dumpsysOutput = await RunShellCommandAsync("dumpsys battery | grep level");
                    BatteryLevel = int.Parse(BatteryLevelRegex().Match(dumpsysOutput).ToString());
                }
                catch (Exception e)
                {
                    error = true;
                    BatteryLevel = -1;
                    Log.Error(e, "Failed to get battery level from dumpsys output: {DumpsysOutput}", dumpsysOutput);
                    op.SetException(e);
                }

                // Get storage stats
                try
                {
                    var (spaceTotal, spaceFree) = await GetSpaceStatsAsync();
                    var spaceUsed = spaceTotal - spaceFree;
                    SpaceUsed = (float) Math.Round((double)spaceUsed / 1000000000, 2);
                    SpaceFree = (float) Math.Round((double)spaceFree / 1000000000, 2);
                }
                catch (Exception e)
                {
                    error = true;
                    SpaceUsed = -1;
                    SpaceFree = -1;
                    op.SetException(e);
                }

                if (error)
                {
                    op.Abandon();
                    return;
                }

                op.Complete();
            }
            finally
            {
                _deviceInfoSemaphoreSlim.Release();
            }
        }

        private async Task OnPackageListChangedAsync()
        {
            await RefreshInstalledPackagesAsync();
            await RefreshInstalledGamesAsync();
            await RefreshInstalledAppsAsync();
            if (_adbService.Device?.Equals(this) ?? false)
                _adbService._packageListChangeSubject.OnNext(Unit.Default);
        }

        /// <summary>
        ///     Refreshes the <see cref="InstalledGames" /> list
        /// </summary>
        public async Task RefreshInstalledGamesAsync()
        {
            using var op = Operation.At(LogEventLevel.Debug).Begin("Refreshing installed games on {Device}", this);
            var skip = _packagesSemaphoreSlim.CurrentCount == 0;
            await _packagesSemaphoreSlim.WaitAsync();
            try
            {
                if (skip)
                {
                    op.Cancel();
                    return;
                }

                Log.Information("Refreshing list of installed games on {Device}", this);
                await _downloaderService.EnsureMetadataAvailableAsync();
                var query = from package in InstalledPackages.ToList()
                    where package.versionInfo is not null
                    // We can't determine which particular release is installed, so we list all releases with appropriate package name
                    let games = _downloaderService.AvailableGames!.Where(g => g.PackageName == package.packageName)
                    where games.Any()
                    from game in games
                    select new InstalledGame(game, package.versionInfo!.Value.VersionCode, package.versionInfo!.Value.VersionName);
                InstalledGames = query.ToList();
                var tupleList = InstalledGames.Select(g => (g.ReleaseName, g.PackageName)).ToList();
                Log.Debug("Found {Count} installed games on {Device}: {InstalledGames}", InstalledGames.Count, this,
                    tupleList);
                op.Complete();
            }
            catch (Exception e)
            {
                Log.Error(e, "Error refreshing installed games");
                Globals.ShowErrorNotification(e, Resources.ErrorRefreshingInstalledGames);
                InstalledGames = [];
            }
            finally
            {
                _packagesSemaphoreSlim.Release();
            }
        }

        /// <summary>
        ///     Refreshes the <see cref="InstalledApps" /> list
        /// </summary>
        public async Task RefreshInstalledAppsAsync()
        {
            using var op = Operation.At(LogEventLevel.Debug).Begin("Refreshing installed apps on {Device}", this);
            var metadataAvailable = false;
            var donationsAvailable = false;

            try
            {
                await Task.Run(async () =>
                {
                    await _downloaderService.EnsureMetadataAvailableAsync();
                    donationsAvailable = await _downloaderService.GetDonationsAvailableAsync();
                });
                metadataAvailable = true;
            }
            catch
            {
                // ignored
            }

            Log.Debug("Refreshing list of installed apps on {Device}", this);
            var installedGames = InstalledGames.ToList();
            IEnumerable<InstalledApp> query;
            if (metadataAvailable)
                query = from package in InstalledPackages.ToList()
                    let packageName = package.packageName
                    let versionName = package.versionInfo?.VersionName ?? "N/A"
                    let versionCode = package.versionInfo?.VersionCode ?? -1
                    let name = installedGames.FirstOrDefault(g => g.PackageName == packageName)?.GameName ?? packageName
                    let donationsUnavailable = !donationsAvailable
                    let isBlacklisted = _downloaderService.DonationBlacklistedPackages.Contains(packageName) ||
                                        packageName.StartsWith("com.oculus.") || packageName.StartsWith("com.meta.") || packageName.Contains(".environment.")
                    let isNew = _downloaderService.AvailableGames!.All(g => g.PackageName != packageName)
                    let isIgnored = _sideloaderSettings.IgnoredDonationPackages.Any(i => i == packageName)
                    let isDonated = _sideloaderSettings.DonatedPackages.Any(i =>
                        i.packageName == packageName && i.versionCode >= versionCode)
                    let isNewVersion = _downloaderService.AvailableGames!.Where(g => g.PackageName == packageName)
                        .All(g => versionCode > g.VersionCode)
                    let isHiddenFromDonation = donationsUnavailable || isBlacklisted || isIgnored || isDonated ||
                                               !(isNew || isNewVersion)
                    let donationStatus = !isHiddenFromDonation ? isNew ? Resources.NewApp : Resources.NewVersion :
                        donationsUnavailable ? Resources.DonationsUnavailable :
                        isDonated ? Resources.Donated :
                        isIgnored ? Resources.Ignored :
                        isBlacklisted ? Resources.Blacklisted : Resources.UpToDate
                    select new InstalledApp(name, packageName, versionName, versionCode, !isNew, isHiddenFromDonation,
                        donationStatus);
            else
                query = from package in InstalledPackages.ToList()
                    let packageName = package.packageName
                    let versionName = package.versionInfo?.VersionName ?? "N/A"
                    let versionCode = package.versionInfo?.VersionCode ?? -1
                    let name = installedGames.FirstOrDefault(g => g.PackageName == packageName)?.GameName ?? packageName
                    let isHiddenFromDonation = true
                    let donationStatus = "N/A"
                    select new InstalledApp(name, packageName, versionName, versionCode, false, isHiddenFromDonation,
                        donationStatus);
            InstalledApps = query.ToList();
            var tupleList = InstalledApps.Select(g => (g.Name, g.PackageName)).ToList();
            Log.Debug("Found {Count} installed apps on {Device}: {InstalledApps}", InstalledApps.Count,
                this, tupleList);
            var donatableApps = InstalledApps.Where(a => !a.IsHiddenFromDonation)
                .Select(a => (a.Name, a.DonationStatus))
                .ToList();
            Log.Debug("Donatable apps on {Device}: {DonatableApps}", this, donatableApps);
            op.Complete();
        }

        /// <summary>
        ///     Pushes a file to the device.
        /// </summary>
        /// <param name="localPath">Path to a local file.</param>
        /// <param name="remotePath">Remote path to push the file to.</param>
        /// <param name="progress">An optional parameter which, when specified, returns progress notifications.
        /// The progress is reported as a value between 0 and 100.</param>
        /// <param name="ct">Cancellation token.</param>
        private async Task PushFileAsync(string localPath, string remotePath, IProgress<int>? progress = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            // check available space 
            var fileSize = new FileInfo(localPath).Length;
            var (_, spaceFree) = await GetSpaceStatsAsync();
            if (spaceFree < fileSize)
            {
                Log.Error("Not enough space on device {Device} to push file {LocalPath} ({SpaceFree} < {FileSize})",
                    this, localPath, spaceFree, fileSize);
                throw new NotEnoughDeviceSpaceException(this);
            }
            Log.Debug("Pushing file: \"{LocalPath}\" -> \"{RemotePath}\", size: {FileSize}", localPath, remotePath,
                fileSize);
            using var syncService = new SyncService(_adb.AdbClient, this);
            await using var file = File.OpenRead(localPath);
            var isCancelled = false;
            ct.Register(() => isCancelled = true);
            syncService.Push(file, remotePath, 777, DateTime.Now, progress, isCancelled);
        }

        /// <summary>
        ///     Pushes a directory to the device.
        /// </summary>
        /// <param name="localPath">Path to a local directory.</param>
        /// <param name="remotePath">Remote path to push the directory to.</param>
        /// <param name="overwrite">Pushed directory should fully overwrite existing one (if exists).</param>
        /// <param name="progress">An optional parameter which, when specified, returns progress notifications.</param>
        /// <param name="ct">Cancellation token.</param>
        private async Task PushDirectoryAsync(string localPath, string remotePath, bool overwrite = false,
            IProgress<(int totalFiles, int currentFile, int progress)>? progress = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!remotePath.EndsWith('/'))
                remotePath += "/";
            var localDir = new DirectoryInfo(localPath).Name;
            Log.Debug("Pushing directory: \"{LocalPath}\" -> \"{RemotePath}\", overwrite: {Overwrite}",
                localPath, remotePath + localPath.Replace("\\", "/"), overwrite);
            var dirList = Directory.GetDirectories(localPath, "*", SearchOption.AllDirectories).ToList();
            var relativeDirList = dirList.Select(dirPath => Path.GetRelativePath(localPath, dirPath));

            var fullPath = (remotePath + localDir).Replace(@"\", "/");
            if (overwrite && await RemoteDirectoryExistsAsync(fullPath))
                await RunShellCommandAsync($"rm -rf \"{fullPath}\"", true);
            await RunShellCommandAsync($"mkdir -p \"{fullPath}/\"", true);
            foreach (var dirPath in relativeDirList)
                await RunShellCommandAsync(
                    $"mkdir -p \"{fullPath + "/" + dirPath.Replace("./", "")}\"".Replace(@"\", "/"), true);

            var fileList = Directory.EnumerateFiles(localPath, "*.*", SearchOption.AllDirectories).ToList();
            var relativeFileList = fileList.Select(filePath => Path.GetRelativePath(localPath, filePath)).ToList();
            var fileCount = fileList.Count;
            for (var i = 0; i < fileCount; i++)
            {
                var file = relativeFileList[i];
                var i1 = i;
                var fileProgress = new Progress<int>(p => progress?.Report((fileCount, i1 + 1, p)));
                await PushFileAsync(localPath + Path.DirectorySeparatorChar + file,
                    fullPath + "/" + file.Replace(@"\", "/"), fileProgress, ct);
            }
        }

        /// <summary>
        ///     Pulls a file from the device.
        /// </summary>
        /// <param name="remotePath">Path to a file on the device.</param>
        /// <param name="localPath">Local path to pull to.</param>
        /// <param name="ct">Cancellation token.</param>
        private async Task PullFileAsync(string remotePath, string localPath, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(localPath))
                Directory.CreateDirectory(localPath);
            var remoteFileName = remotePath.Split('/').Last(x => !string.IsNullOrEmpty(x));
            var localFilePath = localPath + Path.DirectorySeparatorChar + remoteFileName;
            Log.Debug("Pulling file: \"{RemotePath}\" -> \"{LocalPath}\"", remotePath, localFilePath);
            using var syncService = new SyncService(_adb.AdbClient, this);
            await using var file = File.OpenWrite(localFilePath);
            await syncService.PullAsync(remotePath, file, null, ct);
        }

        public async Task PullMediaAsync(string path, CancellationToken ct = default)
        {
            Log.Information("Pulling pictures and videos from {Device} to {Path}", this, path);
            await PullDirectoryAsync("/sdcard/Oculus/VideoShots", path, null, ct);
            await PullDirectoryAsync("/sdcard/Oculus/Screenshots", path, null, ct);
        }

        /// <summary>
        ///     Recursively pulls a directory from the device.
        /// </summary>
        /// <param name="remotePath">Path to a directory on the device.</param>
        /// <param name="localPath">Local path to pull to.</param>
        /// <param name="excludeDirs">Names of directories to exclude from pulling.</param>
        /// <param name="ct">Cancellation token.</param>
        private async Task PullDirectoryAsync(string remotePath, string localPath, IEnumerable<string>? excludeDirs = default,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!remotePath.EndsWith('/'))
                remotePath += "/";
            var remoteDirName = remotePath.Split('/').Last(x => !string.IsNullOrEmpty(x));
            var localDir = Path.Combine(localPath, remoteDirName);
            var excludeDirsList = excludeDirs?.ToList() ?? [];
            Directory.CreateDirectory(localDir);
            Log.Debug("Pulling directory: \"{RemotePath}\" -> \"{LocalPath}\"",
                remotePath, localDir);
            using var syncService = new SyncService(_adb.AdbClient, this);
            // Get listing for remote directory, excluding directories in excludeDirs
            var remoteDirectoryListing = syncService.GetDirectoryListing(remotePath).Where(x =>
                !excludeDirsList.Contains(x.Path.Split('/').Last(s => !string.IsNullOrEmpty(s)))).ToList();
            foreach (var remoteFile in remoteDirectoryListing.Where(x => x.Path != "." && x.Path != ".."))
                if (remoteFile.FileType.HasFlag(UnixFileType.Directory))
                    await PullDirectoryAsync(remotePath + remoteFile.Path, localDir, excludeDirsList, ct);
                else if (remoteFile.FileType.HasFlag(UnixFileType.Regular))
                    await PullFileAsync(remotePath + remoteFile.Path, localDir, ct);
        }

        /// <summary>
        ///     Checks if specified directory exists on the device.
        /// </summary>
        /// <param name="path">Path to directory</param>
        /// <returns>
        ///     <see langword="true" /> if stat was successful and directory exists, <see langword="false" /> otherwise.
        /// </returns>
        private async Task<bool> RemoteDirectoryExistsAsync(string path)
        {
            try
            {
                using var syncService = new SyncService(_adb.AdbClient, this);
                return (await syncService.StatAsync(path)).FileType.HasFlag(UnixFileType.Directory);
            }
            catch (Exception e)
            {
                Log.Error(e, "Failed to stat {Path}", path);
                return false;
            }
        }

        /// <summary>
        ///     Checks if specified file exists on the device.
        /// </summary>
        /// <param name="path">Path to file</param>
        /// <returns>
        ///     <see langword="true" /> if stat was successful and directory exists, <see langword="false" /> otherwise.
        /// </returns>
        private async Task<bool> RemoteFileExistsAsync(string path)
        {
            try
            {
                using var syncService = new SyncService(_adb.AdbClient, this);
                return (await syncService.StatAsync(path)).FileType.HasFlag(UnixFileType.Regular);
            }
            catch (Exception e)
            {
                Log.Error(e, "Failed to stat {Path}", path);
                return false;
            }
        }

        /// <summary>
        ///     Sideloads the specified game to the device.
        /// </summary>
        /// <param name="game"><see cref="Game" /> to sideload.</param>
        /// <param name="gamePath">Path to game files.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns><see cref="IObservable{T}" /> that reports current status.</returns>
        public IObservable<(string status, string? progress)> SideloadGame(Game game, string gamePath,
            CancellationToken ct = default)
        {
            return Observable.Create<(string status, string? progress)>(async observer =>
            {
                ct.ThrowIfCancellationRequested();
                using var op = Operation.At(LogEventLevel.Information, LogEventLevel.Error)
                    .Begin("Sideloading game {GameName}", game.GameName ?? "Unknown");
                var reinstall = false;
                try
                {
                    Log.Information("Sideloading game {GameName}", game.GameName);

                    if (game.PackageName is not null)
                    {
                        EnsureValidPackageName(game.PackageName);
                        reinstall = InstalledPackages.Any(x => x.packageName == game.PackageName);
                    }

                    if (File.Exists(gamePath))
                    {
                        if (gamePath.EndsWith(".apk"))
                            await InstallPackageAsync(gamePath, reinstall, true, observer, null, ct);
                        else
                            throw new InvalidOperationException("Attempted to sideload a non-APK file");
                    }
                    else
                    {
                        var installScriptPath = "";
                        try
                        {
                            installScriptPath =
                                PathHelper.GetActualCaseForFileName(Path.Combine(gamePath, "install.txt"));
                        }
                        catch (Exception)
                        {
                            // ignored
                        }

                        if (File.Exists(installScriptPath))
                        {
                            observer.OnNext((Resources.PerformingCustomInstall, null));
                            var installScriptName = Path.GetFileName(installScriptPath);
                            Log.Information("Running commands from {InstallScriptName}", installScriptName);
                            await RunInstallScriptAsync(installScriptPath, ct);
                        }
                        else
                            // install APKs, copy OBB dir
                        {
                            foreach (var apkPath in Directory.EnumerateFiles(gamePath, "*.apk"))
                                await InstallPackageAsync(apkPath, reinstall, true, observer, null, ct);

                            if (game.PackageName is not null &&
                                Directory.Exists(Path.Combine(gamePath, game.PackageName)))
                            {
                                Log.Information("Found OBB directory for {PackageName}, pushing to device",
                                    game.PackageName);
                                observer.OnNext((Resources.PushingObbFiles, null));
                                var pushProgress = new Progress<(int totalFiles, int currentFile, int progress)>(x =>
                                {
                                    var progressString = $"{x.currentFile}/{x.totalFiles} ({x.progress}%)";
                                    observer.OnNext((Resources.PushingObbFiles, progressString));
                                });
                                await PushDirectoryAsync(Path.Combine(gamePath, game.PackageName), "/sdcard/Android/obb/", true,
                                    pushProgress, ct);
                            }
                        }
                    }

                    op.Complete();
                    await OnPackageListChangedAsync();
                    observer.OnCompleted();
                }
                catch (Exception e)
                {
                    Exception? cleanupException = null;
                    if (!reinstall && game.PackageName is not null)
                    {
                        var type = e is OperationCanceledException
                            ? "cancelled"
                            : "failed";
                        // ReSharper disable once TemplateIsNotCompileTimeConstantProblem
                        Log.Information($"Cleaning up {type} install");
                        observer.OnNext((Resources.CleaningUp, null));
                        try
                        {
                            await CleanupRemnantsAsync(game.PackageName);
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "Failed to clean up install");
                            cleanupException = ex;
                        }
                    }

                    if (e is OperationCanceledException)
                    {
                        observer.OnError(e);
                        op.Cancel();
                        return Disposable.Empty;
                    }

                    op.SetException(e);
                    op.Abandon();
                    observer.OnError(cleanupException is not null
                        ? new AggregateException(new AdbServiceException("Failed to sideload game", e),
                            cleanupException)
                        : new AdbServiceException("Failed to sideload game", e));
                    await OnPackageListChangedAsync();
                }

                return Disposable.Empty;
            });
        }

        /// <summary>
        ///     Runs custom install script.
        /// </summary>
        /// <param name="scriptPath">Path to install script.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <exception cref="FileNotFoundException">Thrown if install script not found.</exception>
        /// <exception cref="AdbServiceException">Thrown if an error occured when running the script.</exception>
        private async Task RunInstallScriptAsync(string scriptPath, CancellationToken ct = default)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                if (!File.Exists(scriptPath))
                    throw new FileNotFoundException("Install script not found", scriptPath);
                var scriptName = Path.GetFileName(scriptPath);
                var gamePath = Path.GetDirectoryName(scriptPath)!;
                var scriptCommands = await File.ReadAllLinesAsync(scriptPath, ct);
                foreach (var archivePath in Directory.GetFiles(gamePath, "*.7z", SearchOption.TopDirectoryOnly))
                    await ZipUtil.ExtractArchiveAsync(archivePath, gamePath, ct);
                foreach (var rawCommand in scriptCommands)
                {
                    ct.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(rawCommand) || rawCommand.StartsWith('#') || rawCommand.StartsWith("REM")) continue;
                    // Remove redirections
                    var command = rawCommand.Split('>').First().Trim();
                    Log.Information("{ScriptName}: Running command: \"{Command}\"", scriptName, command);
                    var args = CommandArgsRegex().Matches(command)
                        .Select(x => x.Value.Trim('"'))
                        .ToList();
                    if (args[0] == "adb")
                    {
                        var adbCommand = args[1];
                        var adbArgs = args.Skip(2).ToList();
                        switch (adbCommand)
                        {
                            case "install":
                            {
                                var reinstall = adbArgs.Contains("-r");
                                var grantRuntimePermissions = adbArgs.Contains("-g");
                                var apkPath = Path.Combine(gamePath, adbArgs.First(x => x.EndsWith(".apk")));
                                await InstallPackageAsync(apkPath, reinstall, grantRuntimePermissions, null, null, ct);
                                break;
                            }
                            case "uninstall":
                            {
                                if (adbArgs.Count > 1)
                                    throw new InvalidOperationException(
                                        $"Wrong number of arguments in adb uninstall command: expected 1, got {adbArgs.Count}");
                                var packageName = adbArgs.First();
                                await CreateBackupAsync(packageName, new BackupOptions(), ct);
                                try
                                {
                                    await UninstallPackageInternalAsync(packageName);
                                }
                                catch (PackageNotFoundException)
                                {
                                }

                                break;
                            }
                            case "push":
                            {
                                if (adbArgs.Count != 2)
                                    throw new InvalidOperationException(
                                        $"Wrong number of arguments in adb push command: expected 2, got {adbArgs.Count}");
                                var source = Path.Combine(gamePath, adbArgs[0]);
                                var destination = adbArgs[1];
                                if (Directory.Exists(source))
                                    await PushDirectoryAsync(source, destination, ct: ct);
                                else if (File.Exists(source))
                                    await PushFileAsync(source, destination, null, ct);
                                else
                                    Log.Information("Local path {Path} doesn't exist", source);
                                break;
                            }
                            case "pull":
                            {
                                if (adbArgs.Count != 2)
                                    throw new InvalidOperationException(
                                        $"Wrong number of arguments in adb pull command: expected 2, got {adbArgs.Count}");
                                var source = adbArgs[0];
                                var destination = Path.Combine(gamePath, adbArgs[1]);
                                if (await RemoteDirectoryExistsAsync(source))
                                    await PullDirectoryAsync(source, destination, ct: ct);
                                else if (await RemoteFileExistsAsync(source))
                                    await PullFileAsync(source, destination, ct);
                                else
                                    Log.Information("Remote path {Path} doesn't exist", source);
                                break;
                            }
                            case "shell":
                            {
                                adbArgs = adbArgs.Select(x => x.Contains(' ') ? $"\"{x}\"" : x).ToList();
                                if (adbArgs is ["pm", "uninstall", ..])
                                {
                                    if (adbArgs.Count != 3)
                                        throw new InvalidOperationException(
                                            $"Wrong number of arguments in adb shell pm uninstall command: expected 3, got {adbArgs.Count}");
                                    var packageName = adbArgs[2];
                                    await CreateBackupAsync(packageName, new BackupOptions(), ct);
                                    try
                                    {
                                        await UninstallPackageInternalAsync(packageName);
                                    }
                                    catch (PackageNotFoundException)
                                    {
                                    }

                                    break;
                                }

                                var shellCommand = string.Join(" ", adbArgs);
                                await RunShellCommandAsync(shellCommand, true);
                                break;
                            }
                            default:
                                throw new AdbServiceException($"Encountered unknown adb command: {adbCommand}");
                        }
                    }
                    else
                    {
                        throw new AdbServiceException($"Encountered unknown command: {command}");
                    }
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw new AdbServiceException("Error running install script", e);
            }
        }

        /// <summary>
        ///     Cleans up app remnants from Android/data and Android/obb directories.
        /// </summary>
        /// <param name="packageName">Package name to clean.</param>
        /// <exception cref="ArgumentException">Thrown if <paramref name="packageName" /> is invalid.</exception>
        private async Task CleanupRemnantsAsync(string? packageName)
        {
            EnsureValidPackageName(packageName);
            try
            {
                try
                {
                    await UninstallPackageInternalAsync(packageName, true);
                }
                catch
                {
                    // ignored
                }

                await RunShellCommandAsync(
                    $"rm -r /sdcard/Android/data/{packageName}/; rm -r /sdcard/Android/obb/{packageName}/");
            }
            catch (Exception e)
            {
                Log.Error(e, "Failed to clean up remnants of package {PackageName}", packageName);
                throw new CleanupException(packageName, e);
            }
        }

        /// <summary>
        /// Wrapper around <see cref="InstallPackageInternalAsync"/> that handles some common installation errors.
        /// </summary>
        /// <param name="apkPath">Path to APK file.</param>
        /// <param name="reinstall">Set reinstall flag for pm.</param>
        /// <param name="grantRuntimePermissions">Grant all runtime permissions.</param>
        /// <param name="observer">An optional parameter for install status notifications.</param>
        /// <param name="progress">An optional parameter which, when specified, returns progress notifications. The progress is reported as a value between 0 and 100.</param>
        /// <param name="ct">Cancellation token.</param>
        private async Task InstallPackageAsync(string apkPath, bool reinstall, bool grantRuntimePermissions,
            IObserver<(string status, string? progress)>? observer = default,
            Progress<int>? progress = default,
            CancellationToken ct = default)
        {
            try
            {
                observer?.OnNext((Resources.InstallingApk, null));
                progress ??= new Progress<int>();
                progress.ProgressChanged += (_, args) => { observer?.OnNext((Resources.InstallingApk, args + "%")); };
                await InstallPackageInternalAsync(apkPath, reinstall, grantRuntimePermissions, progress, ct);
            }
            catch (PackageInstallationException e)
            {
                if (e.Message.Contains("INSTALL_FAILED_UPDATE_INCOMPATIBLE") ||
                    e.Message.Contains("INSTALL_FAILED_VERSION_DOWNGRADE"))
                {
                    observer?.OnNext((Resources.IncompatibleUpdateReinstalling, null));
                    Log.Information("Incompatible update, reinstalling. Reason: {Message}", e.Message);
                    var apkInfo = await GeneralUtils.GetApkInfoAsync(apkPath);
                    var backup = await CreateBackupAsync(apkInfo.PackageName, new BackupOptions {NameAppend = "reinstall"},
                        ct);
                    await UninstallPackageInternalAsync(apkInfo.PackageName);
                    progress ??= new Progress<int>();
                    progress.ProgressChanged += (_, args) =>
                    {
                        observer?.OnNext((Resources.IncompatibleUpdateReinstalling, args + "%"));
                    };
                    await InstallPackageInternalAsync(apkPath, false, true, progress, ct);
                    if (backup is not null)
                        await RestoreBackupAsync(backup);
                }
                else
                {
                    throw;
                }
            }
        }

        /// <summary>
        ///     Installs the package from the given path.
        /// </summary>
        /// <param name="apkPath">Path to APK file.</param>
        /// <param name="reinstall">Set reinstall flag for pm.</param>
        /// <param name="grantRuntimePermissions">Grant all runtime permissions.</param>
        /// <param name="progress">An optional parameter which, when specified, returns progress notifications. The progress is reported as a value between 0 and 100.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <remarks>Legacy install method is used to avoid rare hang issues.</remarks>
        private async Task InstallPackageInternalAsync(string apkPath, bool reinstall, bool grantRuntimePermissions,
            IProgress<int>? progress = default,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Log.Information("Installing APK: {ApkFileName}", Path.GetFileName(apkPath));

            // List<string> args = new ();
            // if (reinstall)
            //     args.Add("-r");
            // if (grantRuntimePermissions)
            //     args.Add("-g");
            // using Stream stream = File.OpenRead(apkPath);
            // Adb.AdbClient.Install(this, stream, args.ToArray());

            progress?.Report(0);

            var progressHandler = new EventHandler<InstallProgressEventArgs>((_, args) =>
            {
                var progressValue = args.State switch
                {
                    PackageInstallProgressState.Uploading => (int) Math.Round(args.UploadProgress * 0.9),
                    PackageInstallProgressState.CreateSession => 90,
                    PackageInstallProgressState.WriteSession => 90,
                    PackageInstallProgressState.Installing => 90,
                    PackageInstallProgressState.PostInstall => 95,
                    PackageInstallProgressState.Finished => 100,
                    _ => throw new AdbServiceException("Unknown package install progress state")
                };
                progress?.Report(progressValue);
            });
            PackageManager.InstallProgressChanged += progressHandler;

            try
            {
                // Using legacy PackageManager.InstallPackage method as AdbClient.Install hangs occasionally
                var args = new List<string>();
                if (reinstall)
                    args.Add("-r");
                if (grantRuntimePermissions)
                    args.Add("-g");
                await PackageManager.InstallPackageAsync(apkPath, ct, args.ToArray());
                Log.Information("Package {ApkFileName} installed", Path.GetFileName(apkPath));
            }
            finally
            {
                PackageManager.InstallProgressChanged -= progressHandler;
            }
        }

        /// <summary>
        ///     Uninstalls the package with the given package name and cleans up remnants.
        /// </summary>
        /// <param name="packageName">Package name to uninstall.</param>
        /// <exception cref="ArgumentException">Thrown if <c>packageName</c> is null.</exception>
        public async Task UninstallPackageAsync(string? packageName)
        {
            EnsureValidPackageName(packageName);
            try
            {
                await UninstallPackageInternalAsync(packageName);
            }
            finally
            {
                await CleanupRemnantsAsync(packageName);
                await OnPackageListChangedAsync();
            }
        }

        /// <summary>
        ///     Uninstalls the package with the given package name.
        /// </summary>
        /// <param name="packageName">Package name to uninstall.</param>
        /// <param name="silent">Don't send log messages.</param>
        /// <exception cref="PackageNotFoundException">Thrown if package is not installed.</exception>
        private async Task UninstallPackageInternalAsync(string? packageName, bool silent = false)
        {
            EnsureValidPackageName(packageName);
            try
            {
                if (!silent)
                    Log.Information("Uninstalling package {PackageName}", packageName);
                await _adb.AdbClient.UninstallPackageAsync(this, packageName!);
            }
            catch (PackageInstallationException e)
            {
                if (e.Message.Contains("DELETE_FAILED_INTERNAL_ERROR") && string.IsNullOrWhiteSpace(
                        await RunShellCommandAsync($"pm list packages | grep -w ^package:{Regex.Escape(packageName!)}$")))
                {
                    if (!silent)
                        Log.Warning("Package {PackageName} is not installed", packageName);
                    throw new PackageNotFoundException(packageName!);
                }

                if (!e.Message.Contains("DELETE_FAILED_DEVICE_POLICY_MANAGER")) throw;
                Log.Information("Package {PackageName} is protected by device policy, trying to force uninstall",
                    packageName);
                await RunShellCommandAsync("pm disable-user " + packageName, true);
                await _adb.AdbClient.UninstallPackageAsync(this, packageName!);
            }
        }

        /// <summary>
        ///     Enables Wireless ADB using tcpip mode.
        /// </summary>
        /// <returns>Host IP address.</returns>
        public async Task<string> EnableWirelessAdbAsync()
        {
            const int port = 5555;
            await ApplyWirelessFixAsync();
            var ipRouteOutput = await RunShellCommandAsync("ip route");
            var ipAddress = IpAddressRegex().Match(ipRouteOutput).Groups[1].ToString();
            await _adb.AdbClient.TcpIpAsync(this, port);
            return ipAddress;
        }

        /// <summary>
        ///     Applies settings fixes to make wireless ADB more stable.
        /// </summary>
        private async Task ApplyWirelessFixAsync()
        {
            await RunShellCommandAsync(
                "settings put global wifi_wakeup_available 1 " +
                "&& settings put global wifi_wakeup_enabled 1 " +
                "&& settings put global wifi_sleep_policy 2 " +
                "&& settings put global wifi_suspend_optimizations_enabled 0 " +
                "&& settings put global wifi_watchdog_poor_network_test_enabled 0 " +
                "&& svc wifi enable");
            Log.Debug("Applied wireless fix to {Device}", this);
        }

        /// <summary>
        ///     Backs up app with given package name.
        /// </summary>
        /// <param name="packageName">Package name to backup.</param>
        /// <param name="options"><see cref="BackupOptions"/> to configure backup.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Path to backup, or <c>null</c> if nothing was backed up.</returns>
        public async Task<Backup?> CreateBackupAsync(string packageName, BackupOptions options, CancellationToken ct = default)
        {
            EnsureValidPackageName(packageName);
            Log.Information("Backing up {PackageName}", packageName);
            using var op = Operation.Begin("Backing up {PackageName}", packageName);
            var backupPath = Path.Combine(_sideloaderSettings.BackupsLocation,
                // ReSharper disable once StringLiteralTypo
                $"{DateTime.Now.ToString("yyyyMMddTHHmmss", CultureInfo.InvariantCulture)}_{packageName}");
            if (!string.IsNullOrEmpty(options.NameAppend))
                backupPath += $"_{options.NameAppend}";
            var sharedDataPath = $"/sdcard/Android/data/{packageName}/";
            var privateDataPath = $"/data/data/{packageName}/";
            var obbPath = $"/sdcard/Android/obb/{packageName}/";
            //var backupMetadataPath = Path.Combine(backupPath, "backup.json");
            var sharedDataBackupPath = Path.Combine(backupPath, "data");
            var privateDataBackupPath = Path.Combine(backupPath, "data_private");
            var obbBackupPath = Path.Combine(backupPath, "obb");
            var apkPath = ApkPathRegex().Match(await RunShellCommandAsync($"pm path {packageName}")).Groups[1]
                .ToString();
            Directory.CreateDirectory(backupPath);
            var backupEmpty = true;
            try
            {
                if (options.BackupData)
                {
                    Log.Debug("Backing up private data");
                    if (await RemoteDirectoryExistsAsync("/sdcard/backup_tmp/"))
                    {
                        Log.Information("Found old backup_tmp directory on device, deleting");
                        await RunShellCommandAsync("rm -r /sdcard/backup_tmp/", true);
                    }

                    Directory.CreateDirectory(privateDataBackupPath);
                    await RunShellCommandAsync(
                        $"mkdir -p /sdcard/backup_tmp/{packageName}/; " +
                        // piping files through tar because run-as <package_name> has weird permissions
                        $"run-as {packageName} tar -cf - -C {privateDataPath} . | tar -xvf - -C /sdcard/backup_tmp/{packageName}/",
                        true);
                    await PullDirectoryAsync($"/sdcard/backup_tmp/{packageName}/", privateDataBackupPath,
                        new List<string> {"cache", "code_cache"}, ct);
                    await RunShellCommandAsync("rm -rf /sdcard/backup_tmp/", true);
                    var privateDataHasFiles = Directory.EnumerateFiles(privateDataBackupPath, "*",
                        SearchOption.AllDirectories).Any();
                    if (!privateDataHasFiles)
                    {
                        Log.Debug("No files in pulled private data, deleting");
                        Directory.Delete(privateDataBackupPath, true);
                    }

                    backupEmpty = backupEmpty && !privateDataHasFiles;
                    if (await RemoteDirectoryExistsAsync(sharedDataPath))
                    {
                        backupEmpty = false;
                        Log.Debug("Backing up shared data");
                        Directory.CreateDirectory(sharedDataBackupPath);
                        await PullDirectoryAsync(sharedDataPath, sharedDataBackupPath, new List<string> {"cache"}, ct);
                    }
                }

                if (options.BackupApk)
                {
                    backupEmpty = false;
                    Log.Debug("Backing up APK");
                    await PullFileAsync(apkPath, backupPath, ct);
                }

                if (options.BackupObb && await RemoteDirectoryExistsAsync(obbPath))
                {
                    backupEmpty = false;
                    Log.Debug("Backing up OBB");
                    Directory.CreateDirectory(obbBackupPath);
                    await PullDirectoryAsync(obbPath, obbBackupPath, ct: ct);
                }

                if (!backupEmpty)
                {
                    await File.Create(Path.Combine(backupPath, ".backup")).DisposeAsync();
                    //var json = JsonConvert.SerializeObject(game);
                    //File.WriteAllText("game.json", json);
                    Log.Information("Backup of {PackageName} created", packageName);
                    op.Complete();
                }
                else
                {
                    Log.Information("Nothing was backed up for {PackageName}", packageName);
                    Directory.Delete(backupPath, true);
                    return null;
                }

                var backup = new Backup(backupPath);
                _adbService._backupList.Add(backup);
                _adbService._backupListChangeSubject.OnNext(Unit.Default);
                return backup;
            }
            catch (OperationCanceledException)
            {
                Log.Information("Cleaning up cancelled backup");
                if (options.BackupData)
                    await RunShellCommandAsync("rm -r /sdcard/backup_tmp/", true);
                if (Directory.Exists(backupPath))
                    Directory.Delete(backupPath, true);
                throw;
            }
        }

        /// <summary>
        ///     Restores given backup.
        /// </summary>
        /// <param name="backup">Backup to restore.</param>
        /// <exception cref="DirectoryNotFoundException">Thrown if backup directory doesn't exist.</exception>
        /// <exception cref="ArgumentException">Thrown if backup is invalid.</exception>
        public async Task RestoreBackupAsync(Backup backup)
        {
            Log.Information("Restoring backup from {BackupPath}", backup.Path);
            var sharedDataBackupPath = Path.Combine(backup.Path, "data");
            var privateDataBackupPath = Path.Combine(backup.Path, "data_private");
            var obbBackupPath = Path.Combine(backup.Path, "obb");

            var restoredApk = false;
            if (backup.HasApk)
            {
                var apkPath = Directory.EnumerateFiles(backup.Path, "*.apk", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault();
                Log.Debug("Restoring APK {ApkName}", Path.GetFileName(apkPath));
                await InstallPackageAsync(apkPath!, true, true);
                restoredApk = true;
            }

            if (backup.HasObb)
            {
                Log.Debug("Restoring OBB");
                await PushDirectoryAsync(Directory.EnumerateDirectories(obbBackupPath).First(), "/sdcard/Android/obb/", true);
            }

            if (backup.HasSharedData)
            {
                Log.Debug("Restoring shared data");
                await PushDirectoryAsync(Directory.EnumerateDirectories(sharedDataBackupPath).First(), "/sdcard/Android/data/",
                    true);
            }

            if (backup.HasPrivateData)
            {
                var packageName =
                    Path.GetFileName(Directory.EnumerateDirectories(privateDataBackupPath).FirstOrDefault());
                if (packageName is not null)
                {
                    Log.Debug("Restoring private data");
                    await RunShellCommandAsync("mkdir /sdcard/restore_tmp/", true);
                    await PushDirectoryAsync(Path.Combine(privateDataBackupPath, packageName), "/sdcard/restore_tmp/");
                    await RunShellCommandAsync(
                        // piping files through tar because run-as <package_name> has weird permissions
                        $"tar -cf - -C /sdcard/restore_tmp/{packageName}/ . | run-as {packageName} tar -xvf - -C /data/data/{packageName}/; rm -rf /sdcard/restore_tmp/",
                        true);
                }
            }

            Log.Information("Backup restored");
            if (!restoredApk) return;
            await OnPackageListChangedAsync();
        }

        /// <summary>
        /// Pulls app with the given package name from the device to the given path.
        /// </summary>
        /// <param name="packageName">Package name of app to pull.</param>
        /// <param name="outputPath">Path to pull the app to.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Path to the directory with pulled app.</returns>
        public async Task<string> PullAppAsync(string packageName, string outputPath, CancellationToken ct = default)
        {
            EnsureValidPackageName(packageName);
            Log.Information("Pulling app {PackageName} from device", packageName);
            var path = Path.Combine(outputPath, packageName);
            if (Directory.Exists(path))
                Directory.Delete(path, true);
            Directory.CreateDirectory(path);
            var apkPath = ApkPathRegex().Match(await RunShellCommandAsync($"pm path {packageName}")).Groups[1]
                .ToString();
            var localApkPath = Path.Combine(path, Path.GetFileName(apkPath));
            var obbPath = $"/sdcard/Android/obb/{packageName}/";
            await PullFileAsync(apkPath, path, ct);
            File.Move(localApkPath, Path.Combine(path, packageName + ".apk"));
            if (await RemoteDirectoryExistsAsync(obbPath))
                await PullDirectoryAsync(obbPath, path, ct: ct);
            return path;
        }

        /// <summary>
        /// Ensures that given package name is valid.
        /// </summary>
        /// <param name="packageName">Package name to validate.</param>
        /// <exception cref="ArgumentException">Thrown if provided package name is not valid.</exception>
        private static void EnsureValidPackageName(string? packageName)
        {
            if (string.IsNullOrEmpty(packageName))
                throw new ArgumentException("Package name cannot be null or empty", nameof(packageName));
            if (!PackageNameRegex().IsMatch(packageName))
                throw new ArgumentException($"Package name is not valid: \"{packageName}\"", nameof(packageName));
        }

        private async Task CheckKeyMapperAsync()
        {
            try
            {
                if (await RunShellCommandAsync("getprop ro.build.version.release") != "12")
                    return;
                // PackageManager might be in a weird state, so just try to uninstall and catch 
                await UninstallPackageInternalAsync("io.github.sds100.keymapper.debug", true);
                Log.Information("Uninstalled KeyMapper");
            }
            catch (PackageNotFoundException)
            {
                // ignored
            }
            catch (Exception e)
            {
                Log.Warning(e, "Error uninstalling KeyMapper");
            }
        }

        public async Task<bool> TryFixDateTimeAsync()
        {
            // Set time
            var timeSinceEpoch = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            Log.Information("Setting date and time on device to {TimeSinceEpoch}", timeSinceEpoch);
            if (await RunShellCommandAsync($"service call alarm 2 i64 {timeSinceEpoch}", true) !=
                "Result: Parcel(00000000 00000001   '........')")
                Log.Warning("Unexpected result when setting time");
            // Verify time
            var time = long.Parse(await RunShellCommandAsync("date +%s%N | cut -b1-13", true));
            var timeDiff = Math.Abs(time - timeSinceEpoch);
            if (timeDiff > 1000)
            {
                Log.Error("Time verification failed (diff: {TimeDiff} > 1000ms)", timeDiff);
                return false;
            }

            // Set timezone
            var tzId = GeneralUtils.GetIanaTimeZoneId(TimeZoneInfo.Local);
            Log.Information("Setting timezone on device to {TzId}", tzId);
            if (await RunShellCommandAsync($"service call alarm 3 s16 {tzId}", true) != "Result: Parcel(00000000    '....')")
                Log.Warning("Unexpected result when setting timezone");
            // Verify timezone
            if (await RunShellCommandAsync("getprop persist.sys.timezone", true) == tzId)
                return true;
            Log.Error("Timezone verification failed");
            return false;

        }

        public async Task CleanLeftoverApksAsync()
        {
            Log.Debug("Cleaning leftover APKs");
            await RunShellCommandAsync("rm -v /data/local/tmp/*.apk", true);
        }

        [GeneratedRegex(@"src ([\d]{1,3}.[\d]{1,3}.[\d]{1,3}.[\d]{1,3})")]
        private static partial Regex IpAddressRegex();

        [GeneratedRegex("[0-9]{1,3}")]
        private static partial Regex BatteryLevelRegex();

        /// <summary>
        /// Regex pattern to split command into list of arguments.
        /// </summary>
        [GeneratedRegex("[\\\"].+?[\\\"]|[^ ]+")]
        private static partial Regex CommandArgsRegex();

        [GeneratedRegex("package:(\\S+)")]
        private static partial Regex ApkPathRegex();
    }

    [GeneratedRegex(@"^(?:[A-Za-z]{1}[\w]*\.)+[A-Za-z][\w]*$")]
    public static partial Regex PackageNameRegex();
}
