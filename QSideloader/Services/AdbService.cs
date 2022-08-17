using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reactive;
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
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using CliWrap;
using CliWrap.Buffered;
using QSideloader.Models;
using QSideloader.Utilities;
using QSideloader.ViewModels;
using Serilog;
using Serilog.Events;
using SerilogTimings;

namespace QSideloader.Services;

/// <summary>
///     Service for all ADB operations.
/// </summary>
public class AdbService
{
    private static readonly SemaphoreSlim DeviceSemaphoreSlim = new(1, 1);
    private static readonly SemaphoreSlim DeviceListSemaphoreSlim = new(1, 1);
    private static readonly SemaphoreSlim BackupListSemaphoreSlim = new(1, 1);
    private static readonly SemaphoreSlim AdbServerSemaphoreSlim = new(1, 1);
    private static readonly SemaphoreSlim PackageOperationSemaphoreSlim = new(1, 1);
    private readonly AdbServerClient _adb;
    private readonly Subject<Unit> _backupListChangeSubject = new();
    private readonly Subject<DeviceState> _deviceStateChangeSubject = new();
    private readonly Subject<List<AdbDevice>> _deviceListChangeSubject = new();
    private readonly Subject<Unit> _packageListChangeSubject = new();
    private readonly SideloaderSettingsViewModel _sideloaderSettings;
    private readonly List<AdbDevice> _deviceList = new();
    private readonly List<Backup> _backupList = new();
    private List<DeviceData> _unauthorizedDeviceList = new();
    private DeviceMonitor? _deviceMonitor;
    private bool _forcePreferWireless;

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
        WhenDeviceListChanged.Subscribe(_ => CheckConnectionPreference());
        Task.Run(async () =>
        {
            RefreshBackupList();
            CheckDeviceConnection();
            var lastWirelessAdbHost = _sideloaderSettings.LastWirelessAdbHost;
            if (!string.IsNullOrEmpty(lastWirelessAdbHost))
                await TryConnectWirelessAdbAsync(lastWirelessAdbHost);
        });
    }

    public static AdbService Instance { get; } = new();
    private bool FirstDeviceSearch { get; set; } = true;

    public AdbDevice? Device { get; private set; }

    public IReadOnlyList<AdbDevice> DeviceList => _deviceList.AsReadOnly();
    public IReadOnlyList<Backup> BackupList => _backupList.AsReadOnly();

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
    ///     Checks that device is connected and if not, tries to find an appropriate device and connect to it.
    /// </summary>
    /// <param name="assumeOffline">Assume that current device is offline and run full search</param>
    /// <returns>
    ///     <c>true</c> if device is connected, <c>false</c> if no device was found.
    /// </returns>
    public bool CheckDeviceConnection(bool assumeOffline = false)
    {
        if (Design.IsDesignMode) return false;
        try
        {
            EnsureADBRunning();
        }
        catch
        {
            return false;
        }

        var connectionStatus = false;
        DeviceSemaphoreSlim.Wait();
        try
        {
            if (Device is not null && !assumeOffline) connectionStatus = PingDevice(Device);

            if (!connectionStatus)
            {
                AdbDevice? foundDevice = null;
                RefreshDeviceList();
                foreach (var device in DeviceList)
                {
                    connectionStatus = PingDevice(device);
                    if (!connectionStatus) continue;
                    foundDevice = device;
                    break;
                }

                if ((Device is null || Device?.Serial != foundDevice?.Serial) && foundDevice is not null &&
                    connectionStatus)
                {
                    OnDeviceOnline(foundDevice);
                }
                else if (Device is not null && !connectionStatus)
                {
                    OnDeviceOffline(Device);
                }
                if (DeviceList.Count == 0)
                {
                    if (_unauthorizedDeviceList.Count > 0)
                        OnDeviceUnauthorized(_unauthorizedDeviceList[0]);
                    else
                        OnDeviceOffline(null);
                }
            }
        }
        catch (Exception e)
        {
            Log.Error(e, "Error while checking device connection");
            Globals.ShowErrorNotification(e, "Error while checking device connection");
        }
        finally
        {
            DeviceSemaphoreSlim.Release();
        }

        FirstDeviceSearch = false;
        return connectionStatus;
    }

    /// <summary>
    ///     Simple check of current connection status (only background ping and background device list scanning when needed).
    ///     For full check use <see cref="CheckDeviceConnection" />.
    /// </summary>
    /// <returns>
    ///     <c>true</c> if device is connected, <c>false</c> otherwise.
    /// </returns>
    public bool CheckDeviceConnectionSimple()
    {
        if (FirstDeviceSearch)
            return CheckDeviceConnection();
        if (Device is null) return false;
        if (Device.State != DeviceState.Online)
        {
            Task.Run(() => CheckDeviceConnection());
            return false;
        }
        Task.Run(() => WakeDevice(Device));
        return true;
    }

    /// <summary>
    ///     Refreshes the device list.
    /// </summary>
    private void RefreshDeviceList()
    {
        try
        {
            var skipScan = DeviceListSemaphoreSlim.CurrentCount == 0;
            DeviceListSemaphoreSlim.Wait();
            try
            {
                EnsureADBRunning();
            }
            catch
            {
                return;
            }

            if (skipScan) return;
            var deviceList = GetOculusDevices(out var unauthorizedDevices);
            _unauthorizedDeviceList = unauthorizedDevices;
            var addedDevices = deviceList.Where(d => _deviceList.All(x => x.Serial != d.Serial)).ToList();
            var removedDevices = _deviceList.Where(d => deviceList.All(x => x.Serial != d.Serial)).ToList();
            if (!addedDevices.Any() && !removedDevices.Any()) return;

            foreach (var device in addedDevices)
            {
                device.Initialize();
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
    private void CheckConnectionPreference()
    {
        if (Device is null) return;
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
                    TrySwitchDevice(preferredDevice);
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
                    TrySwitchDevice(preferredDevice);
                }

                break;
            }
        }
    }

    /// <summary>
    ///     Ensures that ADB server is running.
    /// </summary>
    /// <exception cref="AdbServiceException">Thrown if ADB server start failed.</exception>
    private void EnsureADBRunning()
    {
        try
        {
            var restartNeeded = false;
            AdbServerSemaphoreSlim.Wait();
            try
            {
                var adbServerStatus = _adb.AdbServer.GetStatus();
                if (adbServerStatus.IsRunning)
                {
                    var requiredAdbVersion = new Version("1.0.40");
                    if (adbServerStatus.Version >= requiredAdbVersion)
                    {
                        StartDeviceMonitor(false);
                        return;
                    }

                    restartNeeded = true;
                    Log.Warning("ADB daemon is outdated and will be restarted");
                }
            }
            catch
            {
                Log.Warning("Failed to check ADB server status");
                restartNeeded = true;
            }

            Log.Information("Starting ADB server");

            // Workaround for issues with AdbServer.StartServer
            try
            {
                var adbPath = PathHelper.AdbPath;
                if (restartNeeded)
                    try
                    {
                        Cli.Wrap(adbPath)
                            .WithArguments("kill-server")
                            .ExecuteBufferedAsync()
                            .GetAwaiter().GetResult();
                    }
                    catch
                    {
                        Array.ForEach(Process.GetProcessesByName("adb"), p => p.Kill());
                    }

                Cli.Wrap(adbPath)
                    .WithArguments("start-server")
                    .ExecuteBufferedAsync()
                    .GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                // TODO: handle failures
                Log.Error(e, "Failed to start ADB server");
                Globals.ShowErrorNotification(e, "Failed to start ADB server");
                throw new AdbServiceException("Failed to start ADB server", e);
            }

            if (!_adb.AdbServer.GetStatus().IsRunning)
            {
                Log.Error("Failed to start ADB server");
                Globals.ShowNotification("ADB", "Failed to start ADB server", NotificationType.Error,
                    TimeSpan.Zero);
                throw new AdbServiceException("Failed to start ADB server");
            }

            _adb.AdbClient.Connect("127.0.0.1:62001");
            Log.Information("Started ADB server");
            StartDeviceMonitor(true);
        }
        finally
        {
            AdbServerSemaphoreSlim.Release();
        }
    }

    /// <summary>
    ///     Wakes up the device by sending a power button key event.
    /// </summary>
    /// <param name="device">Device to wake.</param>
    private void WakeDevice(DeviceData device)
    {
        try
        {
            RunShellCommand(device, "input keyevent KEYCODE_WAKEUP");
        }
        catch (Exception e)
        {
            Log.Information("Failed to send wake command to device {Device}", device);
            Log.Verbose(e, "Failed to send wake command to device {Device}", device);
        }
    }

    /// <summary>
    ///     Pings the device to ensure it is still connected and responding.
    /// </summary>
    /// <param name="device">Device to ping.</param>
    /// <returns>
    ///     <c>true</c> if device responded, <c>false</c> otherwise.
    /// </returns>
    public bool PingDevice(DeviceData device)
    {
        WakeDevice(device);
        if (device.State != DeviceState.Online) return false;
        try
        {
            return RunShellCommand(device, "echo 1").Trim() == "1";
        }
        catch (Exception e)
        {
            Log.Information("Failed to ping device {Device}", device);
            Log.Verbose(e, "Failed to ping device {Device}", device);
        }

        return false;
    }

    /// <summary>
    ///     (Re)starts <see cref="DeviceMonitor" />
    /// </summary>
    /// <param name="restart">Should restart device monitor.</param>
    private void StartDeviceMonitor(bool restart)
    {
        if (_deviceMonitor is null || restart)
        {
            if (_deviceMonitor is not null)
                _deviceMonitor.DeviceChanged -= OnDeviceChanged;
            _deviceMonitor = new DeviceMonitor(new AdbSocket(new IPEndPoint(IPAddress.Loopback, 5037)));
        }

        if (_deviceMonitor.IsRunning) return;
        _deviceMonitor.DeviceChanged += OnDeviceChanged;
        _deviceMonitor.Start();
        Log.Debug("Started device monitor");
    }

    /// <summary>
    ///     Method for handling <see cref="DeviceMonitor.DeviceChanged" /> event.
    /// </summary>
    private void OnDeviceChanged(object? sender, DeviceDataEventArgs e)
    {
        Log.Debug("OnDeviceChanged: got event. Device State = {DeviceState}", e.Device.State);
        switch (e.Device.State)
        {
            case DeviceState.Online:
                if (DeviceList.All(x => x.Serial != e.Device.Serial))
                {
                    RefreshDeviceList();
                }

                CheckDeviceConnection();
                break;
            case DeviceState.Offline:
                if (e.Device.Serial == Device?.Serial)
                {
                    CheckDeviceConnection(true);
                }
                else
                {
                    RefreshDeviceList();
                    CheckDeviceConnection();
                }

                break;
            case DeviceState.Unauthorized:
                CheckDeviceConnection();
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
        if (!FirstDeviceSearch)
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
            Globals.ShowErrorNotification(e, "Error while notifying device state change");
        }
    }

    /// <summary>
    ///     Gets hashed ID from device serial number.
    /// </summary>
    /// <param name="deviceSerial">Device serial to convert.</param>
    /// <returns>Hashed ID as <see cref="string" />.</returns>
    /// <remarks><see cref="GeneralUtils.GetHwid"/> is used as salt.</remarks>
    private static string GetHashedId(string deviceSerial)
    {
        var hwid = GeneralUtils.GetHwid();
        var saltedSerial = hwid + deviceSerial;
        using var sha256Hash = SHA256.Create();
        var hashedId = Convert.ToHexString(sha256Hash.ComputeHash(Encoding.ASCII.GetBytes(saltedSerial)))[..16];
        return hashedId;
    }

    /// <summary>
    ///     Gets the list of Oculus devices.
    /// </summary>
    /// <returns><see cref="List{T}" /> of <see cref="AdbDevice" />.</returns>
    private List<AdbDevice> GetOculusDevices(out List<DeviceData> unauthorizedDevices)
    {
        Log.Information("Searching for devices");
        unauthorizedDevices = new List<DeviceData>();
        List<AdbDevice> oculusDeviceList = new();
        var deviceList = _adb.AdbClient.GetDevices();
        if (deviceList.Count == 0)
        {
            Log.Warning("No ADB devices found");
            return oculusDeviceList;
        }

        foreach (var device in deviceList)
        {
            var hashedDeviceId = GetHashedId(device.Serial);
            if (IsOculusQuest(device))
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

        if (oculusDeviceList.Count == 0)
        {
            Log.Warning("No Oculus devices found");
            return oculusDeviceList;
        }

        return _sideloaderSettings.PreferredConnectionType switch
        {
            "USB" => oculusDeviceList.OrderBy(x => x.IsWireless).ToList(),
            "Wireless" => oculusDeviceList.OrderByDescending(x => x.IsWireless).ToList(),
            _ => oculusDeviceList
        };
    }

    /// <summary>
    ///     Tries to switch to another device.
    /// </summary>
    /// <param name="device">Device to switch to.</param>
    public void TrySwitchDevice(AdbDevice device)
    {
        if (device.Serial == Device?.Serial) return;
        if (device.State != DeviceState.Online || !PingDevice(device))
        {
            Log.Warning("Attempted switch to offline device {Device}", device);
            RefreshDeviceList();
            return;
        }

        DeviceSemaphoreSlim.Wait();
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
    ///     Enables Wireless ADB on the device.
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
            var host = device.EnableWirelessAdb();
            await Task.Delay(1000);
            await TryConnectWirelessAdbAsync(host, true);
            _sideloaderSettings.LastWirelessAdbHost = host;
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to enable Wireless ADB");
            Globals.ShowErrorNotification(e, "Failed to enable Wireless ADB");
        }
    }

    /// <summary>
    ///     Tries to connect to the wireless adb host.
    /// </summary>
    /// <param name="host">Host to connect to.</param>
    /// <param name="silent">Don't send log messages.</param>
    private async Task TryConnectWirelessAdbAsync(string host, bool silent = false)
    {
        if (DeviceList.Any(x => x.Serial.Contains(host)))
        {
            if (!silent)
                Log.Debug("Wireless device on {Host} is already connected, skipping", host);
            return;
        }

        if (!silent)
            Log.Debug("Trying to connect to wireless device, host {Host}", host);

        try
        {
            for (var i = 0; i < 5; i++)
            {
                _adb.AdbClient.Connect(host);
                if (_adb.AdbClient.GetDevices().Any(x => x.Serial.Contains(host)))
                {
                    RefreshDeviceList();
                    _sideloaderSettings.LastWirelessAdbHost = host;
                    return;
                }

                Log.Debug("Wireless device on {Host} not connected, trying again", host);
                await Task.Delay(500);
            }
        }
        catch
        {
            
            _sideloaderSettings.LastWirelessAdbHost = "";
        }
        if (!silent)
            Log.Warning("Couldn't connect to wireless device");
    }

    /// <summary>
    ///     Checks if device is an Oculus Quest device.
    /// </summary>
    /// <param name="device">Device to check.</param>
    /// <returns></returns>
    private static bool IsOculusQuest(DeviceData device)
    {
        return device.Product is "hollywood" or "monterey" or "vr_monterey";
    }

    /// <summary>
    ///     Takes the package operation lock.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public static async Task TakePackageOperationLockAsync(CancellationToken ct = default)
    {
        await PackageOperationSemaphoreSlim.WaitAsync(ct);
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
    public async Task<string> GetDevicesStringAsync()
    {
        var adbPath = PathHelper.AdbPath;
        try
        {
            var commandResult = await Cli.Wrap(adbPath)
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
                _backupList.Add(new Backup(backup.path));
            if (toRemove.Count > 0 || toAdd.Count > 0)
                _backupListChangeSubject.OnNext(Unit.Default);
        }
        finally
        {
            BackupListSemaphoreSlim.Release();
        }
    }

    /// <summary>
    ///     Helper class to store <see cref="AdvancedAdbClient" />-<see cref="AdbServer" /> pair.
    /// </summary>
    private class AdbServerClient
    {
        public AdvancedAdbClient AdbClient { get; } = new();
        public AdbServer AdbServer { get; } = new();
    }


    /// <summary>
    ///     Adb device class for device-specific operations.
    /// </summary>
    public class AdbDevice : DeviceData
    {
        private readonly SemaphoreSlim _deviceInfoSemaphoreSlim = new(1, 1);
        private readonly SemaphoreSlim _packagesSemaphoreSlim = new(1, 1);
        private readonly AdbServerClient _adb;
        private readonly AdbService _adbService;
        private readonly SideloaderSettingsViewModel _sideloaderSettings;
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
            IsWireless = deviceData.Serial.Contains('.');
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

            FriendlyName = deviceData.Product switch
            {
                "monterey" => "Quest 1",
                "hollywood" => "Quest 2",
                _ => "Unknown?"
            };

            HashedId = GetHashedId(TrueSerial ?? Serial);

            PackageManager = new PackageManager(_adb.AdbClient, this, true);

        }

        private PackageManager PackageManager { get; }
        public float SpaceUsed { get; private set; }
        public float SpaceFree { get; private set; }
        public float SpaceTotal { get; private set; }
        public float BatteryLevel { get; private set; }
        public List<(string packageName, VersionInfo? versionInfo)> InstalledPackages { get; } = new();
        public List<InstalledGame> InstalledGames { get; set; } = new();
        public List<InstalledApp> InstalledApps { get; set; } = new();
        public bool IsRefreshingInstalledGames => _packagesSemaphoreSlim.CurrentCount == 0;
        public string FriendlyName { get; }
        
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
        
        public void Initialize()
        {
            Task.Run(OnPackageListChanged);
        }

        /// <summary>
        ///     Runs a shell command on the device.
        /// </summary>
        /// <param name="command">Command to run.</param>
        /// <param name="logCommand">Should log the command and result.</param>
        /// <returns>Output of executed command.</returns>
        /// <exception cref="AdbServiceException">Thrown if an error occured when running the command.</exception>
        public string RunShellCommand(string command, bool logCommand = false)
        {
            return _adbService.RunShellCommand(this, command, logCommand);
        }

        /// <summary>
        ///     Refreshes the <see cref="InstalledPackages" /> list
        /// </summary>
        private void RefreshInstalledPackages()
        {
            using var op = Operation.At(LogEventLevel.Debug).Begin("Refreshing installed packages on {Device}", this);
            var skip = _packagesSemaphoreSlim.CurrentCount == 0;
            _packagesSemaphoreSlim.Wait();
            try
            {
                if (skip)
                {
                    op.Cancel();
                    return;
                }
                Log.Debug("Refreshing list of installed packages on {Device}", this);
                PackageManager.RefreshPackages();
                for (var i = 0; i < InstalledPackages.Count; i++)
                {
                    var package = InstalledPackages[i];
                    if (PackageManager.Packages.ContainsKey(package.packageName))
                    {
                        package.versionInfo = PackageManager.GetVersionInfo(package.packageName);
                        InstalledPackages[i] = package;
                    }
                    else
                        InstalledPackages.Remove(package);
                }
                foreach (var package in PackageManager.Packages.Keys.Where(package =>
                             InstalledPackages.All(x => x.packageName != package)).ToList())
                    InstalledPackages.Add((package, PackageManager.GetVersionInfo(package)));
                op.Complete();
            }
            finally
            {
                _packagesSemaphoreSlim.Release();
            }
        }

        /// <summary>
        ///     Refreshes device info.
        /// </summary>
        /// <remarks>
        ///     If this method is called while another device info refresh is in progress,
        ///     the call will wait until the other refresh is finished and then return.
        /// </remarks>
        public void RefreshInfo()
        {
            using var op = Operation.At(LogEventLevel.Debug).Begin("Refreshing device info");
            // Check whether refresh is already running
            var alreadyRefreshing = _deviceInfoSemaphoreSlim.CurrentCount < 1;
            _deviceInfoSemaphoreSlim.Wait();
            try
            {
                // If device info has just been refreshed we can skip
                if (alreadyRefreshing)
                {
                    op.Cancel();
                    return;
                }

                var dfOutput = RunShellCommand("df /storage/emulated");
                var dfOutputSplit = dfOutput.Split(new[] {"\r\n", "\r", "\n"}, StringSplitOptions.None);
                var line = Regex.Split(dfOutputSplit[1], @"\s{1,}");
                SpaceTotal = (float) Math.Round(float.Parse(line[1]) / 1000000, 2);
                SpaceUsed = (float) Math.Round(float.Parse(line[2]) / 1000000, 2);
                SpaceFree = (float) Math.Round(float.Parse(line[3]) / 1000000, 2);

                var dumpsysOutput = RunShellCommand("dumpsys battery | grep level");
                BatteryLevel = int.Parse(Regex.Match(dumpsysOutput, @"[0-9]{1,3}").ToString());
                op.Complete();
            }
            catch (Exception e)
            {
                SpaceTotal = 0;
                SpaceUsed = 0;
                SpaceFree = 0;
                BatteryLevel = 0;
                op.SetException(e);
                op.Abandon();
            }
            finally
            {
                _deviceInfoSemaphoreSlim.Release();
            }
        }

        private void OnPackageListChanged()
        {
            RefreshInstalledPackages();
            RefreshInstalledGames();
            RefreshInstalledApps();
            _adbService._packageListChangeSubject.OnNext(Unit.Default);
        }

        /// <summary>
        ///     Refreshes the <see cref="InstalledGames" /> list
        /// </summary>
        public void RefreshInstalledGames()
        {
            using var op = Operation.At(LogEventLevel.Debug).Begin("Refreshing installed games on {Device}", this);
            var skip = _packagesSemaphoreSlim.CurrentCount == 0;
            _packagesSemaphoreSlim.Wait();
            try
            {
                if (skip)
                {
                    op.Cancel();
                    return;
                }
                Log.Information("Refreshing list of installed games on {Device}", this);
                _downloaderService.EnsureMetadataAvailableAsync().GetAwaiter().GetResult();
                if (InstalledPackages.Count == 0) RefreshInstalledPackages();
                var query = from package in InstalledPackages.ToList()
                    where package.versionInfo is not null
                    // We can't determine which particular release is installed, so we list all releases with appropriate package name
                    let games = _downloaderService.AvailableGames!.Where(g => g.PackageName == package.packageName)
                    where games.Any()
                    from game in games
                    select new InstalledGame(game, package.versionInfo.VersionCode, package.versionInfo.VersionName);
                InstalledGames = query.ToList();
                var tupleList = InstalledGames.Select(g => (g.ReleaseName, g.PackageName)).ToList();
                Log.Debug("Found {Count} installed games on {Device}: {InstalledGames}", InstalledGames.Count, this,
                    tupleList);
                op.Complete();
            }
            catch (Exception e)
            {
                Log.Error(e, "Failed to refresh installed games");
                Globals.ShowErrorNotification(e, "Failed to refresh installed games");
                InstalledGames = new List<InstalledGame>();
            }
            finally
            {
                _packagesSemaphoreSlim.Release();
            }
        }

        /// <summary>
        ///     Refreshes the <see cref="InstalledApps" /> list
        /// </summary>
        public void RefreshInstalledApps()
        {
            using var op = Operation.At(LogEventLevel.Debug).Begin("Refreshing installed apps on {Device}", this);
            bool metadataAvailable;
            try
            {
                _downloaderService.EnsureMetadataAvailableAsync().GetAwaiter().GetResult();
                metadataAvailable = true;
            }
            catch
            {
                metadataAvailable = false;
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
                    let isBlacklisted = _downloaderService.DonationBlacklistedPackages.Contains(packageName)
                    let isNew = _downloaderService.AvailableGames!.All(g => g.PackageName != packageName)
                    let isIgnored = _sideloaderSettings.IgnoredDonationPackages.Any(i => i == packageName)
                    let isDonated = _sideloaderSettings.DonatedPackages.Any(i =>
                        i.packageName == packageName && i.versionCode >= versionCode)
                    let isNewVersion = _downloaderService.AvailableGames!.Where(g => g.PackageName == packageName)
                        .Any(g => versionCode > g.VersionCode)
                    let isHiddenFromDonation = isBlacklisted || isIgnored || isDonated || !(isNew || isNewVersion)
                    let donationStatus = !isHiddenFromDonation ? isNew ? "New App" : "New version" :
                        isDonated ? "Donated" :
                        isIgnored ? "Ignored" :
                        isBlacklisted ? "Blacklisted" : "Up To Date"
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
            Log.Debug("Found {Count} installed apps: {InstalledApps}", InstalledApps.Count,
                InstalledApps.Select(x => x.Name));
            op.Complete();
        }

        /// <summary>
        ///     Pushes a file to the device.
        /// </summary>
        /// <param name="localPath">Path to a local file.</param>
        /// <param name="remotePath">Remote path to push the file to.</param>
        private void PushFile(string localPath, string remotePath)
        {
            Log.Debug("Pushing file: \"{LocalPath}\" -> \"{RemotePath}\"", localPath, remotePath);
            using var syncService = new SyncService(_adb.AdbClient, this);
            using var file = File.OpenRead(localPath);
            syncService.Push(file, remotePath, 771, DateTime.Now, null, CancellationToken.None);
        }

        /// <summary>
        ///     Pushes a directory to the device.
        /// </summary>
        /// <param name="localPath">Path to a local directory.</param>
        /// <param name="remotePath">Remote path to push the file to.</param>
        /// <param name="overwrite">Pushed directory should fully overwrite existing one (if exists).</param>
        private void PushDirectory(string localPath, string remotePath, bool overwrite = false)
        {
            if (!remotePath.EndsWith("/"))
                remotePath += "/";
            var localDir = new DirectoryInfo(localPath).Name;
            Log.Debug("Pushing directory: \"{LocalPath}\" -> \"{RemotePath}\", overwrite: {Overwrite}", 
                localPath, remotePath + localPath.Replace("\\", "/"), overwrite);
            var dirList = Directory.GetDirectories(localPath, "*", SearchOption.AllDirectories).ToList();
            var relativeDirList = dirList.Select(dirPath => Path.GetRelativePath(localPath, dirPath));

            var fullPath = (remotePath + localDir).Replace(@"\", "/");
            if (overwrite && RemoteDirectoryExists(fullPath))
                RunShellCommand($"rm -rf {fullPath}", true);
            RunShellCommand($"mkdir -p \"{fullPath}/\"", true);
            foreach (var dirPath in relativeDirList)
                RunShellCommand(
                    $"mkdir -p \"{fullPath + "/" + dirPath.Replace("./", "")}\"".Replace(@"\", "/"), true);

            var fileList = Directory.EnumerateFiles(localPath, "*.*", SearchOption.AllDirectories);
            var relativeFileList = fileList.Select(filePath => Path.GetRelativePath(localPath, filePath));
            foreach (var file in relativeFileList)
                PushFile(localPath + Path.DirectorySeparatorChar + file,
                    fullPath + "/" + file.Replace(@"\", "/"));
        }

        /// <summary>
        ///     Pulls a file from the device.
        /// </summary>
        /// <param name="remotePath">Path to a file on the device.</param>
        /// <param name="localPath">Local path to pull to.</param>
        private void PullFile(string remotePath, string localPath)
        {
            var remoteFileName = remotePath.Split('/').Last(x => !string.IsNullOrEmpty(x));
            var localFilePath = localPath + Path.DirectorySeparatorChar + remoteFileName;
            Log.Debug("Pulling file: \"{RemotePath}\" -> \"{LocalPath}\"", remotePath, localFilePath);
            using var syncService = new SyncService(_adb.AdbClient, this);
            using var file = File.OpenWrite(localFilePath);
            syncService.Pull(remotePath, file, null, CancellationToken.None);
        }

        /// <summary>
        ///     Recursively pulls a directory from the device.
        /// </summary>
        /// <param name="remotePath">Path to a directory on the device.</param>
        /// <param name="localPath">Local path to pull to.</param>
        /// <param name="excludeDirs">Names of directories to exclude from pulling.</param>
        private void PullDirectory(string remotePath, string localPath, IEnumerable<string>? excludeDirs = default)
        {
            if (!remotePath.EndsWith("/"))
                remotePath += "/";
            var remoteDirName = remotePath.Split('/').Last(x => !string.IsNullOrEmpty(x));
            var localDir = Path.Combine(localPath, remoteDirName);
            var excludeDirsList = excludeDirs?.ToList() ?? new List<string>();
            Directory.CreateDirectory(localDir);
            Log.Debug("Pulling directory: \"{RemotePath}\" -> \"{LocalPath}\"",
                remotePath, localDir);
            using var syncService = new SyncService(_adb.AdbClient, this);
            // Get listing for remote directory, excluding directories in excludeDirs
            var remoteDirectoryListing = syncService.GetDirectoryListing(remotePath).Where(x =>
                !excludeDirsList.Contains(x.Path.Split('/').Last(s => !string.IsNullOrEmpty(s)))).ToList();
            foreach (var remoteFile in remoteDirectoryListing.Where(x => x.Path != "." && x.Path != ".."))
                if (remoteFile.FileMode.HasFlag(UnixFileMode.Directory))
                    PullDirectory(remotePath + remoteFile.Path, localDir, excludeDirsList);
                else if (remoteFile.FileMode.HasFlag(UnixFileMode.Regular))
                    PullFile(remotePath + remoteFile.Path, localDir);
        }

        /// <summary>
        ///     Checks if specified directory exists on the device.
        /// </summary>
        /// <param name="path">Path to directory</param>
        /// <returns>
        ///     <see langword="true" /> if directory exists, <see langword="false" /> otherwise.
        /// </returns>
        private bool RemoteDirectoryExists(string path)
        {
            try
            {
                using var syncService = new SyncService(_adb.AdbClient, this);
                return syncService.Stat(path).FileMode.HasFlag(UnixFileMode.Directory);
            }
            catch (Exception e)
            {
                Log.Error(e, "Failed to stat the provided path");
                return false;
            }
        }

        /// <summary>
        ///     Sideloads the specified game to the device.
        /// </summary>
        /// <param name="game"><see cref="Game" /> to sideload.</param>
        /// <param name="gamePath">Path to game files.</param>
        /// <returns><see cref="IObservable{T}" /> that reports current status.</returns>
        public IObservable<string> SideloadGame(Game game, string gamePath)
        {
            return Observable.Create<string>(observer =>
            {
                using var op = Operation.At(LogEventLevel.Information, LogEventLevel.Error)
                    .Begin("Sideloading game {GameName}", game.GameName ?? "Unknown");
                var reinstall = false;
                try
                {
                    Log.Information("Sideloading game {GameName}", game.GameName);

                    if (game.PackageName is not null)
                        reinstall = InstalledPackages.Any(x => x.packageName == game.PackageName);

                    if (File.Exists(gamePath))
                    {
                        if (gamePath.EndsWith(".apk"))
                        {
                            observer.OnNext("Installing APK");
                            InstallApk(observer, gamePath);
                        }
                        else
                        {
                            throw new InvalidOperationException("Attempted to sideload a non-APK file");
                        }
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
                            observer.OnNext("Performing custom install");
                            var installScriptName = Path.GetFileName(installScriptPath);
                            Log.Information("Running commands from {InstallScriptName}", installScriptName);
                            RunInstallScript(installScriptPath);
                        }
                        else
                            // install APKs, copy OBB dir
                        {
                            foreach (var apkPath in Directory.EnumerateFiles(gamePath, "*.apk"))
                            {
                                observer.OnNext("Installing APK");
                                InstallApk(observer, apkPath);
                            }

                            if (game.PackageName is not null &&
                                Directory.Exists(Path.Combine(gamePath, game.PackageName)))
                            {
                                Log.Information("Found OBB directory for {PackageName}, pushing to device",
                                    game.PackageName);
                                observer.OnNext("Pushing OBB files");
                                PushDirectory(Path.Combine(gamePath, game.PackageName), "/sdcard/Android/obb/", true);
                            }
                        }
                    }

                    op.Complete();
                    OnPackageListChanged();
                    observer.OnCompleted();
                }
                catch (Exception e)
                {
                    op.SetException(e);
                    op.Abandon();
                    if (!reinstall && game.PackageName is not null)
                    {
                        Log.Information("Cleaning up failed install");
                        CleanupRemnants(game.PackageName);
                    }
                    observer.OnError(new AdbServiceException("Failed to sideload game", e));
                }

                return Disposable.Empty;
            });

            void InstallApk(IObserver<string> observer, string apkPath)
            {
                try
                {
                    InstallPackage(apkPath, false, true);
                }
                catch (PackageInstallationException e)
                {
                    if (e.Message.Contains("INSTALL_FAILED_UPDATE_INCOMPATIBLE") ||
                        e.Message.Contains("INSTALL_FAILED_VERSION_DOWNGRADE"))
                    {
                        observer.OnNext("Incompatible update, reinstalling");
                        Log.Information("Incompatible update, reinstalling. Reason: {Message}", e.Message);
                        var apkInfo = GeneralUtils.GetApkInfo(apkPath);
                        var backup = CreateBackup(apkInfo.PackageName, new BackupOptions {NameAppend = "reinstall"});
                        UninstallPackageInternal(apkInfo.PackageName);
                        InstallPackage(apkPath, false, true);
                        if (backup is not null)
                            RestoreBackup(backup);
                    }
                    else
                    {
                        throw;
                    }
                } 
            }
        }

        /// <summary>
        ///     Runs custom install script.
        /// </summary>
        /// <param name="scriptPath">Path to install script.</param>
        /// <exception cref="FileNotFoundException">Thrown if install script not found.</exception>
        /// <exception cref="AdbServiceException">Thrown if an error occured when running the script.</exception>
        private void RunInstallScript(string scriptPath)
        {
            try
            {
                if (!File.Exists(scriptPath))
                    throw new FileNotFoundException("Install script not found", scriptPath);
                var scriptName = Path.GetFileName(scriptPath);
                // Regex pattern to split command into list of arguments
                const string argsPattern = @"[\""].+?[\""]|[^ ]+";
                var gamePath = Path.GetDirectoryName(scriptPath)!;
                var scriptCommands = File.ReadAllLines(scriptPath);
                foreach (var archivePath in Directory.GetFiles(gamePath, "*.7z", SearchOption.TopDirectoryOnly))
                    ZipUtil.ExtractArchive(archivePath, gamePath);
                foreach (var rawCommand in scriptCommands)
                {
                    if (string.IsNullOrWhiteSpace(rawCommand) || rawCommand.StartsWith("#")) continue;
                    var command = rawCommand.Replace(" > NUL 2>&1", "");
                    Log.Information("{ScriptName}: Running command: \"{Command}\"", scriptName, command);
                    var args = Regex.Matches(command, argsPattern)
                        .Select(x => x.Value.Trim('"'))
                        .ToList();
                    if (args[0] == "adb")
                    {
                        var adbCommand = args[1];
                        args.RemoveRange(0, 2);
                        switch (adbCommand)
                        {
                            case "install":
                            {
                                var reinstall = args.Contains("-r");
                                var grantRuntimePermissions = args.Contains("-g");
                                var apkPath = Path.Combine(gamePath, args.First(x => x.EndsWith(".apk")));
                                InstallPackage(apkPath, reinstall, grantRuntimePermissions);
                                break;
                            }
                            case "uninstall":
                            {
                                var packageName = args.First();
                                CreateBackup(packageName, new BackupOptions());
                                try
                                {
                                    UninstallPackageInternal(packageName);
                                }
                                catch (PackageNotFoundException)
                                {
                                }

                                break;
                            }
                            case "push":
                            {
                                var source = Path.Combine(gamePath, args[0]);
                                var destination = Path.Combine(gamePath, args[1]);
                                if (Directory.Exists(source))
                                    PushDirectory(source, destination);
                                else
                                    PushFile(source, destination);
                                break;
                            }
                            case "shell":
                            {
                                args = args.Select(x => x.Contains(' ') ? $"\"{x}\"" : x).ToList();
                                if (args.Count > 2 && args[0] == "pm" && args[1] == "uninstall")
                                {
                                    var packageName = args[2];
                                    CreateBackup(packageName, new BackupOptions());
                                    try
                                    {
                                        UninstallPackageInternal(packageName);
                                    }
                                    catch (PackageNotFoundException)
                                    {
                                    }

                                    break;
                                }

                                var shellCommand = string.Join(" ", args);
                                RunShellCommand(shellCommand, true);
                                break;
                            }
                            default:
                                throw new AdbServiceException("Encountered unknown adb command");
                        }
                    }
                    else
                    {
                        throw new AdbServiceException("Encountered unknown command");
                    }
                }
            }
            catch (Exception e)
            {
                throw new AdbServiceException("Error running install script", e);
            }
        }

        /// <summary>
        ///     Cleans up app remnants from Android/data and Android/obb directories.
        /// </summary>
        /// <param name="packageName">Package name to clean.</param>
        /// <exception cref="ArgumentException">Thrown if <paramref name="packageName" /> is invalid.</exception>
        private void CleanupRemnants(string? packageName)
        {
            EnsureValidPackageName(packageName);
            try
            {
                try
                {
                    UninstallPackageInternal(packageName, true);
                }
                catch
                {
                    // ignored
                }

                RunShellCommand(
                    $"rm -r /sdcard/Android/data/{packageName}/; rm -r /sdcard/Android/obb/{packageName}/");
            }
            catch (Exception e)
            {
                Log.Warning(e, "Failed to clean up remnants of package {PackageName}", packageName);
            }
        }

        /// <summary>
        ///     Installs the package from the given path.
        /// </summary>
        /// <param name="apkPath">Path to APK file.</param>
        /// <param name="reinstall">Set reinstall flag for pm.</param>
        /// <param name="grantRuntimePermissions">Grant all runtime permissions.</param>
        /// <remarks>Legacy install method is used to avoid rare hang issues.</remarks>
        private void InstallPackage(string apkPath, bool reinstall, bool grantRuntimePermissions)
        {
            Log.Information("Installing APK: {ApkFileName}", Path.GetFileName(apkPath));
            
            // List<string> args = new ();
            // if (reinstall)
            //     args.Add("-r");
            // if (grantRuntimePermissions)
            //     args.Add("-g");
            // using Stream stream = File.OpenRead(apkPath);
            // Adb.AdbClient.Install(this, stream, args.ToArray());
            
            // Using legacy PackageManager.InstallPackage method as AdbClient.Install hangs occasionally
            PackageManager.InstallPackage(apkPath, reinstall, grantRuntimePermissions);
            Log.Information("Package installed");
        }
        
        /// <summary>
        ///     Uninstalls the package with the given package name and cleans up remnants.
        /// </summary>
        /// <param name="packageName">Package name to uninstall.</param>
        /// <exception cref="ArgumentException">Thrown if <c>packageName</c> is null.</exception>
        public void UninstallPackage(string? packageName)
        {
            EnsureValidPackageName(packageName);
            try
            {
                UninstallPackageInternal(packageName);
            }
            finally
            {
                CleanupRemnants(packageName);
                OnPackageListChanged();
            }
        }

        /// <summary>
        ///     Uninstalls the package with the given package name.
        /// </summary>
        /// <param name="packageName">Package name to uninstall.</param>
        /// <param name="silent">Don't send log messages.</param>
        /// <exception cref="PackageNotFoundException">Thrown if package is not installed.</exception>
        private void UninstallPackageInternal(string? packageName, bool silent = false)
        {
            EnsureValidPackageName(packageName);
            try
            {
                if (!silent)
                    Log.Information("Uninstalling package {PackageName}", packageName);
                _adb.AdbClient.UninstallPackage(this, packageName!);
            }
            catch (PackageInstallationException e)
            {
                if (e.Message.Contains("DELETE_FAILED_INTERNAL_ERROR") && string.IsNullOrWhiteSpace(
                        RunShellCommand($"pm list packages -3 | grep -w \"package:{packageName}\"")))
                {
                    if (!silent)
                        Log.Warning("Package {PackageName} is not installed", packageName);
                    throw new PackageNotFoundException(packageName!);
                }
                if (e.Message.Contains("DELETE_FAILED_DEVICE_POLICY_MANAGER"))
                {
                    Log.Information("Package {PackageName} is protected by device policy, trying to force uninstall",
                        packageName);
                    RunShellCommand("pm disable-user " + packageName, true);
                    _adb.AdbClient.UninstallPackage(this, packageName!);
                    return;
                }

                throw;
            }
        }

        /// <summary>
        ///     Prepares device wifi settings and enable Wireless ADB.
        /// </summary>
        /// <returns>Host IP address.</returns>
        public string EnableWirelessAdb()
        {
            const int port = 5555;
            const string ipAddressPattern = @"src ([\d]{1,3}.[\d]{1,3}.[\d]{1,3}.[\d]{1,3})";
            RunShellCommand(
                "settings put global wifi_wakeup_available 1 " +
                "&& settings put global wifi_wakeup_enabled 1 " +
                "&& settings put global wifi_sleep_policy 2 " +
                "&& settings put global wifi_suspend_optimizations_enabled 0 " +
                "&& settings put global wifi_watchdog_poor_network_test_enabled 0 " +
                "&& svc wifi enable");
            var ipRouteOutput = RunShellCommand("ip route");
            var ipAddress = Regex.Match(ipRouteOutput, ipAddressPattern).Groups[1].ToString();
            _adb.AdbClient.TcpIp(this, port);
            return ipAddress;
        }

        /// <summary>
        ///     Backs up given game.
        /// </summary>
        /// <param name="game"><see cref="Game" /> to backup.</param>
        /// <param name="options"><see cref="BackupOptions"/> to configure backup.</param>
        /// <returns>Path to backup, or empty string if nothing was backed up.</returns>
        /// <seealso cref="CreateBackup(string,BackupOptions)" />
        public Backup? CreateBackup(Game game, BackupOptions options)
        {
            return CreateBackup(game.PackageName!, options);
        }

        /// <summary>
        ///     Backs up app with given package name.
        /// </summary>
        /// <param name="packageName">Package name to backup.</param>
        /// <param name="options"><see cref="BackupOptions"/> to configure backup.</param>
        /// <returns>Path to backup, or <c>null</c> if nothing was backed up.</returns>
        /// <seealso cref="CreateBackup(QSideloader.Models.Game,BackupOptions)" />
        public Backup? CreateBackup(string packageName, BackupOptions options)
        {
            EnsureValidPackageName(packageName);
            Log.Information("Backing up {PackageName}", packageName);
            var backupPath = Path.Combine(_sideloaderSettings.BackupsLocation,
                $"{DateTime.Now:yyyyMMddTHHmmss}_{packageName}");
            if (!string.IsNullOrEmpty(options.NameAppend))
                backupPath += $"_{options.NameAppend}";
            var sharedDataPath = $"/sdcard/Android/data/{packageName}/";
            var privateDataPath = $"/data/data/{packageName}/";
            var obbPath = $"/sdcard/Android/obb/{packageName}/";
            //var backupMetadataPath = Path.Combine(backupPath, "backup.json");
            var sharedDataBackupPath = Path.Combine(backupPath, "data");
            var privateDataBackupPath = Path.Combine(backupPath, "data_private");
            var obbBackupPath = Path.Combine(backupPath, "obb");
            const string apkPathPattern = @"package:(\S+)";
            var apkPath = Regex.Match(RunShellCommand($"pm path {packageName}"), apkPathPattern).Groups[1]
                .ToString();
            Directory.CreateDirectory(backupPath);
            File.Create(Path.Combine(backupPath, ".backup")).Dispose();
            var backupEmpty = true;
            if (options.BackupData)
            {
                Log.Debug("Backing up private data");
                Directory.CreateDirectory(privateDataBackupPath);
                RunShellCommand(
                    $"mkdir /sdcard/backup_tmp/; run-as {packageName} cp -av {privateDataPath} /sdcard/backup_tmp/{packageName}/",
                    true);
                PullDirectory($"/sdcard/backup_tmp/{packageName}/", privateDataBackupPath,
                    new List<string> {"cache", "code_cache"});
                RunShellCommand("rm -rf /sdcard/backup_tmp/", true);
                var privateDataHasFiles = Directory.EnumerateFiles(privateDataBackupPath).Any();
                if (!privateDataHasFiles)
                    Directory.Delete(privateDataBackupPath, true);
                backupEmpty = backupEmpty && !privateDataHasFiles;
                if (RemoteDirectoryExists(sharedDataPath))
                {
                    backupEmpty = false;
                    Log.Debug("Backing up shared data");
                    Directory.CreateDirectory(sharedDataBackupPath);
                    PullDirectory(sharedDataPath, sharedDataBackupPath, new List<string> {"cache"});
                }
            }

            if (options.BackupApk)
            {
                backupEmpty = false;
                Log.Debug("Backing up APK");
                PullFile(apkPath, backupPath);
            }

            if (options.BackupObb && RemoteDirectoryExists(obbPath))
            {
                backupEmpty = false;
                Log.Debug("Backing up OBB");
                Directory.CreateDirectory(obbBackupPath);
                PullDirectory(obbPath, obbBackupPath);
            }

            if (!backupEmpty)
            {
                //var json = JsonConvert.SerializeObject(game);
                //File.WriteAllText("game.json", json);
                Log.Information("Backup created");
            }
            else
            {
                Log.Information("Nothing was backed up");
                Directory.Delete(backupPath, true);
                return null;
            }

            var backup = new Backup(backupPath);
            _adbService._backupList.Add(backup);
            _adbService._backupListChangeSubject.OnNext(Unit.Default);
            return backup;
        }
        
        /// <summary>
        ///     Restores given backup.
        /// </summary>
        /// <param name="backup">Backup to restore.</param>
        /// <exception cref="DirectoryNotFoundException">Thrown if backup directory doesn't exist.</exception>
        /// <exception cref="ArgumentException">Thrown if backup is invalid.</exception>
        public void RestoreBackup(Backup backup)
        {
            Log.Information("Restoring backup from {BackupPath}", backup.Path);
            var sharedDataBackupPath = Path.Combine(backup.Path, "data");
            var privateDataBackupPath = Path.Combine(backup.Path, "data_private");
            var obbBackupPath = Path.Combine(backup.Path, "obb");
            
            var restoredApk = false;
            if (backup.ContainsApk)
            {
                var apkPath = Directory.EnumerateFiles(backup.Path, "*.apk", SearchOption.TopDirectoryOnly).FirstOrDefault();
                Log.Debug("Restoring APK {ApkName}", Path.GetFileName(apkPath));
                InstallPackage(apkPath!, true, true);
                restoredApk = true;
            }

            if (backup.ContainsObb)
            {
                Log.Debug("Restoring OBB");
                PushDirectory(Directory.EnumerateDirectories(obbBackupPath).First(), "/sdcard/Android/obb/", true);
            }

            if (backup.ContainsSharedData)
            {
                Log.Debug("Restoring shared data");
                PushDirectory(Directory.EnumerateDirectories(sharedDataBackupPath).First(), "/sdcard/Android/data/", true);
            }

            if (backup.ContainsPrivateData)
            {
                var packageName =
                    Path.GetFileName(Directory.EnumerateDirectories(privateDataBackupPath).FirstOrDefault());
                if (packageName is not null)
                {
                    Log.Debug("Restoring private data");
                    RunShellCommand("mkdir /sdcard/restore_tmp/", true);
                    PushDirectory(Path.Combine(privateDataBackupPath, packageName), "/sdcard/restore_tmp/");
                    RunShellCommand(
                        $"run-as {packageName} cp -av /sdcard/restore_tmp/{packageName}/ /data/data/{packageName}/; rm -rf /sdcard/restore_tmp/",
                        true);
                }
            }

            Log.Information("Backup restored");
            if (!restoredApk) return;
            OnPackageListChanged();
        }

        /// <summary>
        /// Pulls app with the given package name from the device to the given path.
        /// </summary>
        /// <param name="packageName">Package name of app to pull.</param>
        /// <param name="outputPath">Path to pull the app to.</param>
        /// <returns>Path to the directory with pulled app.</returns>
        public string PullApp(string packageName, string outputPath)
        {
            EnsureValidPackageName(packageName);
            Log.Information("Pulling app {PackageName} from device", packageName);
            var path = Path.Combine(outputPath, packageName);
            if (Directory.Exists(path))
                Directory.Delete(path, true);
            Directory.CreateDirectory(path);
            var apkPath = Regex.Match(RunShellCommand($"pm path {packageName}"), @"package:(\S+)").Groups[1]
                .ToString();
            var localApkPath = Path.Combine(path, Path.GetFileName(apkPath));
            var obbPath = $"/sdcard/Android/obb/{packageName}/";
            PullFile(apkPath, path);
            File.Move(localApkPath, Path.Combine(path, packageName + ".apk"));
            if (RemoteDirectoryExists(obbPath))
                PullDirectory(obbPath, path);
            return path;
        }

        /// <summary>
        /// Ensures that given package name is valid.
        /// </summary>
        /// <param name="packageName">Package name to validate.</param>
        /// <exception cref="ArgumentException">Thrown if provided package name is not valid.</exception>
        private static void EnsureValidPackageName(string? packageName)
        {
            const string packageNamePattern = @"^([A-Za-z]{1}[A-Za-z\d_]*\.)+[A-Za-z][A-Za-z\d_]*$";
            if (string.IsNullOrEmpty(packageName))
                throw new ArgumentException("Package name cannot be null or empty", nameof(packageName));
            if (!Regex.IsMatch(packageName, packageNamePattern))
                throw new ArgumentException("Package name is not valid", nameof(packageName));
        }
    }
}

public class AdbServiceException : Exception
{
    public AdbServiceException(string message)
        : base(message)
    {
    }

    public AdbServiceException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

public class PackageNotFoundException : AdbServiceException
{
    public PackageNotFoundException(string packageName)
        : base($"Package {packageName} not found")
    {
    }
}