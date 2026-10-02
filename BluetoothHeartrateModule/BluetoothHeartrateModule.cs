using BluetoothHeartrateModule.UI;
using System.Runtime.InteropServices;
using VRCOSC.App.SDK.Modules;
using VRCOSC.App.SDK.Modules.Heartrate;
using Windows.Devices.Bluetooth.Advertisement;

namespace BluetoothHeartrateModule
{
    [ModuleTitle("Bluetooth Heartrate")]
    [ModuleDescription("Displays heartrate data from Bluetooth-based heartrate sensors")]
    [ModuleInfo("https://github.com/WentTheFox/VRCOSC-BluetoothHeartrate")]
    public partial class BluetoothHeartrateModule : HeartrateModule<BluetoothHeartrateProvider>
    {
        private readonly WebsocketHeartrateServer _wsServer;
        internal BluetoothLEAdvertisementWatcher? Watcher;
        internal AsyncHelper Ah;
        internal DeviceDataManager DeviceDataManager;

        [ModulePersistent("selectedDeviceMac")]
        private string SelectedDeviceMac { get; set; } = string.Empty;

        public BluetoothHeartrateModule()
        {
            Ah = new AsyncHelper(this);
            _wsServer = new WebsocketHeartrateServer(this);
            DeviceDataManager = new(this);
        }

        protected override BluetoothHeartrateProvider CreateProvider()
        {
            LogDebug("Creating provider");
            var provider = new BluetoothHeartrateProvider(this);
            provider.OnHeartrateUpdate += SendWebcoketHeartrate;
            return provider;
        }

        internal new void Log(string message)
        {
            base.Log(message);
        }

        internal new void LogDebug(string message)
        {
            base.LogDebug(message);
        }

        protected override void OnPreLoad()
        {
            LogDebug("Call base class OnLoad");
            base.OnPreLoad();

            LogDebug("Creating settings");
            CreateToggle(BluetoothHeartrateSetting.WebsocketServerEnabled, @"Websocket Server Enabled", @"Broadcast the heartrate data over a local Websocket server", false);
            CreateTextBox(BluetoothHeartrateSetting.WebsocketServerHost, @"Websocket Server Hostname", @"Hostname (IP address) for the Websocket server", "127.0.0.1");
            CreateTextBox(BluetoothHeartrateSetting.WebsocketServerPort, @"Websocket Server Port", @"Port for the Websocket server", 36210);

            CreateVariable<string>(BluetoothHeartratevariable.DeviceName, @"Device Name");

            SetRuntimeView(typeof(BluetoothHeartrateRuntimeView));
        }

        protected override void OnPostLoad()
        {
            LogDebug("Call base class OnPostLoad");
            base.OnPostLoad();
            LogDebug("Updating settings");
            var wsServerEnabledSetting = GetSetting(BluetoothHeartrateSetting.WebsocketServerEnabled);
            if (wsServerEnabledSetting != null)
            {
                wsServerEnabledSetting.OnSettingChange += WsServerEnabledSettingChangeHandler;
            }
            WsServerEnabledSettingChangeHandler();
        }

        private void WsServerEnabledSettingChangeHandler()
        {
            var newValue = GetSettingValue<bool>(BluetoothHeartrateSetting.WebsocketServerEnabled);
            var wsServerHostSetting = GetSetting(BluetoothHeartrateSetting.WebsocketServerHost);
            if (wsServerHostSetting != null)
            {
                wsServerHostSetting.IsEnabled.Value = newValue;
            }
            var wsServerPortSetting = GetSetting(BluetoothHeartrateSetting.WebsocketServerPort);
            if (wsServerPortSetting != null)
            {
                wsServerPortSetting.IsEnabled.Value = newValue;
            }
        }

        protected override async Task<bool> OnModuleStart()
        {
            LogDebug("Starting module");
            AppDomain.CurrentDomain.UnhandledException += LogUnhandledException;
            TaskScheduler.UnobservedTaskException += LogUnobservedTaskException;
            CreateWatcher();
            LogDebug("Call base class OnModuleStart");
            await base.OnModuleStart();
            if (GetWebocketEnabledSetting())
            {
                LogDebug("Starting wsServer");
                _ = _wsServer.Start();
            }
            return true;
        }

        protected override async Task<bool> OnModuleStop()
        {
            LogDebug("Call base class OnModuleStop");
            await base.OnModuleStop();
            LogDebug("Stopping module");
            StopWatcher();
            // Stop regardless of the current setting value, it may have been toggled while running
            LogDebug("Stopping wsServer");
            _wsServer.Stop();
            AppDomain.CurrentDomain.UnhandledException -= LogUnhandledException;
            TaskScheduler.UnobservedTaskException -= LogUnobservedTaskException;
            return true;
        }

        internal string GetDeviceMacSetting()
        {
            return SelectedDeviceMac;
        }
        internal bool GetWebocketEnabledSetting()
        {
            return GetSettingValue<bool>(BluetoothHeartrateSetting.WebsocketServerEnabled);
        }
        internal string GetWebocketHostSetting()
        {
            return GetSettingValue<string>(BluetoothHeartrateSetting.WebsocketServerHost) ?? "";
        }
        internal int GetWebocketPortSetting()
        {
            return GetSettingValue<int>(BluetoothHeartrateSetting.WebsocketServerPort);
        }

        internal void SetDeviceMacSetting(string deviceMac)
        {
            if (SelectedDeviceMac != deviceMac)
            {
                SelectedDeviceMac = deviceMac;
                Log($"Selected device with MAC {deviceMac}");
            }
        }

        internal void ClearDeviceMacSetting()
        {
            ResetDeviceData();
            SelectedDeviceMac = string.Empty;
            DeviceDataManager.ConnectedDeviceMac = string.Empty;
            DeviceDataManager.Refresh();
            StartWatcher();
        }
        internal void SetDeviceName(string deviceName)
        {
            SetVariableValue(BluetoothHeartratevariable.DeviceName, deviceName);
        }

        public void ResetCurrentDevice()
        {
            // Atomically take ownership of the device, as this can be called concurrently
            // from advertisement handlers, disconnect handling and teardown
            var device = Interlocked.Exchange(ref DeviceDataManager.CurrentDevice, null);
            if (device == null)
            {
                return;
            }

            LogDebug("Resetting currentDevice");
            try
            {
                LogDebug("Disposing of currentDevice");
                device.Dispose();
                LogDebug("currentDevice has been reset");
            }
            catch (ObjectDisposedException)
            {
                // Ignore if object is already disposed
                LogDebug("currentDevice already disposed");
            }
            catch (Exception ex)
            {
                LogException("Failed to dispose of currentDevice", ex);
            }
        }

        public void ResetDeviceData()
        {
            LogDebug("Resetting device data");
            RunResetStep("reset heart rate service", DeviceDataManager.ResetHeartRateService);
            RunResetStep("reset heart rate characteristic", DeviceDataManager.ResetHeartRateCharacteristic);
            RunResetStep("reset current device", ResetCurrentDevice);
            RunResetStep("reset missing characteristic devices", DeviceDataManager.ResetMissingCharacterisicsDevices);
            LogDebug("Device data has been reset");
        }

        // Each step is isolated so a failure in one doesn't skip the others, or escape into the host app
        private void RunResetStep(string description, Action step)
        {
            try
            {
                step();
            }
            catch (Exception ex)
            {
                LogException($"Failed to {description}", ex);
            }
        }

        internal void LogException(string context, Exception ex)
        {
            Log($"[ERROR] {context}: {ex.GetType().FullName}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            if (ex.InnerException != null)
            {
                Log($"[ERROR] Inner exception: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}{Environment.NewLine}{ex.InnerException.StackTrace}");
            }
        }

        private void LogUnhandledException(object? sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                LogException($"Unhandled exception (terminating: {e.IsTerminating})", ex);
            }
            else
            {
                Log($"[ERROR] Unhandled non-exception object (terminating: {e.IsTerminating}): {e.ExceptionObject}");
            }
        }

        private void LogUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            LogException("Unobserved task exception", e.Exception);
        }


        private async void SendWebcoketHeartrate(int heartrate)
        {
            if (!GetWebocketEnabledSetting())
            {
                LogDebug("Not sending HR to websocket because it is disabled");
                return;
            }

            try
            {
                await _wsServer.SendIntMessage(heartrate);
            }
            catch (Exception ex)
            {
                LogException("Failed to send heartrate to websocket", ex);
            }
        }
        internal BluetoothLEAdvertisementWatcher CreateWatcher()
        {
            if (Watcher == null)
            {
                LogDebug("Creating new watcher");
                Watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
                LogDebug("Adding watcher stopped event handler");
                Watcher.Stopped += Watcher_Stopped;
            }
            return Watcher;
        }

        private void Watcher_Stopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
        {
            try
            {
                string scanStatus = args.Error.ToString();
                LogDebug($"Watcher stopped, error: {scanStatus}");
                if (scanStatus == "RadioNotAvailable")
                {
                    DeviceDataManager.UpdateBluetoothAvailability(false);
                }
                var newConnectionStatus = DeviceDataManager.ConnectedDeviceMac != string.Empty
                        ? DeviceDataManager.PossibleConnectionStates.Connected
                        : DeviceDataManager.PossibleConnectionStates.Idle;
                DeviceDataManager.UpdateConnestionStatus(newConnectionStatus);
                if (DeviceDataManager.CurrentDevice == null)
                {
                    LogDebug("Invoking OnDisconnected action");
                    DeviceDataManager.OnDisconnected?.Invoke();
                }
                DeviceDataManager.Refresh();
            }
            catch (Exception ex)
            {
                LogException("Failed to handle watcher stop", ex);
            }
        }

        internal Task<bool> StartWatcher()
        {
            var existingWatcher = CreateWatcher();
            LogDebug($"Starting watcher, current status: {existingWatcher.Status}");
            if (existingWatcher.Status != BluetoothLEAdvertisementWatcherStatus.Started)
            {
                try
                {
                    existingWatcher.Start();
                    DeviceDataManager.UpdateConnestionStatus(DeviceDataManager.PossibleConnectionStates.Scanning);
                    var deviceMacSetting = GetDeviceMacSetting();
                    LogDebug($"Scanning for {(deviceMacSetting == string.Empty ? "devices" : $"device with MAC {deviceMacSetting}")}");
                } catch (Exception ex)
                {
                    if (ex is COMException)
                    {
                        DeviceDataManager.UpdateBluetoothAvailability(false);
                    }

                    Log($"Could not start scanning for devices [{ex.GetType()}] ({ex.Message})");
                    return Task.FromResult(false);
                }
            }
            return Task.FromResult(true);
        }

        internal void StopWatcher()
        {
            LogDebug("Stopping watcher");
            try
            {
                Watcher?.Stop();
            }
            catch (Exception ex)
            {
                LogException("Failed to stop watcher", ex);
            }
        }

        internal enum BluetoothHeartrateSetting
        {
            WebsocketServerEnabled,
            WebsocketServerHost,
            WebsocketServerPort
        }

        internal enum BluetoothHeartratevariable
        {
            DeviceName
        }
    }
}
