using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace BluetoothHeartrateModule
{
    public class DeviceDataManager
    {
        Dictionary<string, DeviceData> _devices = new();

        BluetoothHeartrateModule _module;
        DeviceNameResolver _dnr;
        AsyncHelper _ah;

        internal string ConnectedDeviceMac = string.Empty;
        internal string ProcessingDeviceMac = string.Empty;
        internal bool IsBluetoothAvailable = true;
        internal PossibleConnectionStates ConnectionStatus = PossibleConnectionStates.Idle;
        internal GattDeviceService? HeartRateService;
        internal GattCharacteristic? HeartRateCharacteristic;
        internal BluetoothLEDevice? CurrentDevice;
        internal readonly HashSet<string> MissingCharacteristicDevices = new();

        public enum PossibleConnectionStates
        {
            Idle,
            Scanning,
            Connecting,
            Connected,
        }

        public Action? OnDeviceListUpdate { get; internal set; }
        public Action<byte>? OnHeartRateCharacteristicValueChange { get; internal set; }
        public Action? OnConnected { get; internal set; }
        public Action? OnDisconnected { get; internal set; }
        public Action? OnBluetoothAvailabilityChange { get; internal set; }
        public Action? OnConnectionStatusChange { get; internal set; }

        private Dictionary<ulong, bool> _processingAdvertisementMap = new();
        internal Dictionary<string, string> PrefixData = new();

        public DeviceDataManager(BluetoothHeartrateModule module) {
            this._module = module;
            this._ah = module.Ah;
            this._dnr = new DeviceNameResolver(module);
            // TODO Figure this out later
            // this.prefixData = GetPrefixData();
        }

        private Dictionary<string, string> GetPrefixData()
        {
            Dictionary<string, string> data = new();
            var ns = this.GetType().Namespace;
            string fileName = "oui.txt";
            var manufacturerLineRegex = new Regex(@"^([A-F\d]{2}-[A-F\d]{2}-[A-F\d]{2})\s+\(hex\)\s+\w+(.*)\s*$");
            try
            {
                var resourceStream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"{ns}.{fileName}");
                if (resourceStream == null)
                {
                    throw new FileNotFoundException();
                }
                using (var sr = new StreamReader(resourceStream))
                {
                    var line = sr.ReadLine();
                    if (line != null)
                    {
                        var lineMatch = manufacturerLineRegex.Match(line);
                        if (lineMatch.Success)
                        {
                            data.Add(lineMatch.Groups[0].Value.Replace('-', ':'), lineMatch.Groups[1].Value);
                        }
                    }
                }
                _module.LogDebug($"Loaded manufacturer data ({data.Count} entries)");
            }
            catch (FileNotFoundException)
            {
                _module.LogDebug($"Optional resource {fileName} not found, manufacturer data will not be available");
            }
            return data;
        }

        private DeviceData? Create(string mac)
        {
            var data = new DeviceData(mac, this);
            try
            {
                _devices[mac] = data;
            }
            catch (IndexOutOfRangeException e)
            {
                _module.Log($"Could not create device with mac {mac}: {e.Message}");
                return null;
            }

            return data;
        }
        internal DeviceData? Add(string advertisementMac, string deviceName)
        {
            var deviceData = Create(advertisementMac);
            if (deviceData == null) return null;
            
            deviceData.Name = deviceName;

            Refresh();
            return deviceData;
        }

        internal async Task<DeviceData?> Add(ulong bluetoothAddress, BluetoothLEAdvertisement advertisement, CancellationTokenSource cancelToken)
        {
            if (_processingAdvertisementMap.ContainsKey(bluetoothAddress)) { return null; }

            _processingAdvertisementMap.Add(bluetoothAddress, true);
            try
            {
                var advertisementMac = Converter.FormatAsMac(bluetoothAddress);
                var logPrefix = $"[MAC:{advertisementMac}]";
                _module.LogDebug($"{logPrefix} Resolving device name");
                string resolvedDeviceName = string.Empty;
                var deviceName = await _module.Ah.WaitAsync(_dnr.GetDeviceNameAsync(advertisement, bluetoothAddress), AsyncTask.GetDeviceName, cancelToken);
                if (deviceName != null)
                {
                    resolvedDeviceName = deviceName;
                }
                return Add(advertisementMac, resolvedDeviceName);
            }
            finally
            {
                _processingAdvertisementMap.Remove(bluetoothAddress);
            }
        }

        public DeviceData? Get(string mac) {
            return _devices.ContainsKey(mac) ? _devices[mac] : null;
        }

        public bool Has(string mac) {
            return _devices.ContainsKey(mac);
        }

        public void Remove(string mac)
        {
            _devices.Remove(mac);
            Refresh();
        }
        public void Refresh()
        {
            OnDeviceListUpdate?.Invoke();
        }

        public DeviceData[] GetDevices()
        {
            return _devices.Values.ToArray();
        }

        public void ClearDevices() { _devices.Clear(); }

        internal void SetHasHeartrateService(string advertisementMac, bool hasHeartrateService)
        {
            var device = Get(advertisementMac);
            if (device != null)
            {
                device.NoHeartrateService = !hasHeartrateService;
            }
        }
        internal void SetHasHeartrateCharacteristic(string advertisementMac, bool hasHeartrateCharacteristic)
        {
            var device = Get(advertisementMac);
            if (device != null)
            {
                device.NoHeartrateCharacteristic = !hasHeartrateCharacteristic;
            }
        }

        public void ResetHeartRateService()
        {
            if (HeartRateService != null)
            {
                _module.LogDebug("Resetting heartRateService");
                try
                {
                    _module.LogDebug("Disposing of heartRateService");
                    HeartRateService.Dispose();
                }
                catch (ObjectDisposedException)
                {
                    // Ignore if object is already disposed
                    _module.LogDebug("heartRateService already disposed");
                }
                HeartRateService = null;
                _module.LogDebug("heartRateService has been reset");
            }
        }
        public void ResetHeartRateCharacteristic()
        {
            if (HeartRateCharacteristic != null)
            {
                _module.LogDebug("Resetting heartRateCharacteristic");
                try
                {
                    _module.LogDebug("Unregistering ValueChanged handler");
                    HeartRateCharacteristic.ValueChanged -= HeartRateCharacteristic_ValueChanged;
                }
                catch (ObjectDisposedException)
                {
                    // Ignore if object is already disposed
                    _module.LogDebug("heartRateCharacteristic already disposed");
                }
                HeartRateCharacteristic = null;
                _module.LogDebug("heartRateCharacteristic has been reset");
            }
        }

        public void ResetMissingCharacterisicsDevices()
        {
            _module.LogDebug("Clearing missing characteristics");
            MissingCharacteristicDevices.Clear();
        }

        public void UpdateBluetoothAvailability(bool available)
        {
            if (IsBluetoothAvailable == available)
            {
                return;
            }
            _module.LogDebug($"Updated bluetooth state (available: {available}");
            IsBluetoothAvailable = available;
            OnBluetoothAvailabilityChange?.Invoke();
        }

        public bool GetBluetoothAvailability()
        {
            return IsBluetoothAvailable;
        }
        public void UpdateConnestionStatus(PossibleConnectionStates status)
        {
            if (ConnectionStatus == status)
            {
                return;
            }
            _module.LogDebug($"Bluetooth connection status change (status: {status})");
            ConnectionStatus = status;
            OnConnectionStatusChange?.Invoke();
        }

        public PossibleConnectionStates GetConnectionStatus()
        {
            return ConnectionStatus;
        }

        public async Task HandleHeartRateCharacteristicFound(string logPrefix, GattDeviceService firstService, GattCharacteristic characteristic, CancellationTokenSource watcherStopper)
        {
            HeartRateService = firstService;
            HeartRateCharacteristic = characteristic;
            _module.LogDebug("Found heartrate measurement characteristic");

            HeartRateCharacteristic.ValueChanged += HeartRateCharacteristic_ValueChanged;
            _module.LogDebug("Registered heartrate characteristic value change handler");

            // Enable notifications for heart rate measurements
            _module.LogDebug("Requesting characteristic notifications");
            GattCommunicationStatus? status = await _ah.WaitAsync(HeartRateCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify), AsyncTask.WriteCharacteristicConfigDescriptor, watcherStopper);
            if (status == GattCommunicationStatus.Success)
            {
                _module.LogDebug($"{logPrefix} Invoking OnConnected action");
                OnConnected?.Invoke();
                _module.LogDebug("Connection successful");
                Refresh();
                _module.LogDebug($"{logPrefix} Stopping watcher");
                _module.StopWatcher();
            }
            else
            {
                _module.LogDebug($"Failed to enable heart rate notifications. Status: {status}");
            }
        }

        private void HeartRateCharacteristic_ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            _module.LogDebug("HeartRateCharacteristic_ValueChanged");
            var data = new byte[args.CharacteristicValue.Length];
            DataReader.FromBuffer(args.CharacteristicValue).ReadBytes(data);

            var updateData = data[1];
            _module.LogDebug($"Invoking OnHeartrateUpdate action with data {updateData}");
            OnHeartRateCharacteristicValueChange?.Invoke(updateData);
        }

        internal void RegiterConnectionStatusChangeHandler(string logPrefix)
        {
            _module.LogDebug($"{logPrefix} Register connection status change handler");
            if (CurrentDevice != null)
            {
                CurrentDevice.ConnectionStatusChanged += Device_ConnectionStatusChanged;
            }
        }

        private void Device_ConnectionStatusChanged(BluetoothLEDevice sender, object args)
        {
            _module.LogDebug($"Device connection status changed to {sender.ConnectionStatus}");
            if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
            {
                OnDisconnected?.Invoke();
                Refresh();
            }
        }
    }

}
