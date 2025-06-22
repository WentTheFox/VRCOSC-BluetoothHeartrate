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
            if (GetWebocketEnabledSetting())
            {
                LogDebug("Stopping wsServer");
                _wsServer.Stop();
            }
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
            if (DeviceDataManager.CurrentDevice != null)
            {
                LogDebug("Resetting currentDevice");
                try
                {
                    LogDebug("Disposing of currentDevice");
                    DeviceDataManager.CurrentDevice.Dispose();
                }
                catch (ObjectDisposedException)
                {
                    // Ignore if object is already disposed
                    LogDebug("currentDevice already disposed");
                }
                DeviceDataManager.CurrentDevice = null;
                LogDebug("currentDevice has been reset");
            }
        }

        public void ResetDeviceData()
        {
            LogDebug("Resetting device data");
            DeviceDataManager.ResetHeartRateService();
            DeviceDataManager.ResetHeartRateCharacteristic();
            ResetCurrentDevice();
            DeviceDataManager.ResetMissingCharacterisicsDevices();
            LogDebug("Device data has been reset");
        }


        private async void SendWebcoketHeartrate(int heartrate)
        {
            if (!GetWebocketEnabledSetting())
            {
                LogDebug("Not sending HR to websocket because it is disabled");
                return;
            }

            await _wsServer.SendIntMessage(heartrate);
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
            Watcher?.Stop();
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
