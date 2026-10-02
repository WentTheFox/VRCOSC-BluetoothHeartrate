using VRCOSC.App.SDK.Modules.Heartrate;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace BluetoothHeartrateModule
{
    public class BluetoothHeartrateProvider(BluetoothHeartrateModule module) : HeartrateProvider
    {
        private CancellationTokenSource _watcherStopper = new();
        private readonly DeviceDataManager _ddm = module.DeviceDataManager;
        private readonly AsyncHelper _ah = module.Ah;
        public override bool IsConnected => _ddm.CurrentDevice != null && _ddm.HeartRateCharacteristic != null && _ddm.CurrentDevice.ConnectionStatus == BluetoothConnectionStatus.Connected;
        private int _scanAttempts;

        public override async Task<bool> Initialise()
        {
            module.LogDebug("Initializing BluetoothHeartrateProvider");
            if (module.GetDeviceMacSetting() == string.Empty)
            {
                module.LogDebug("Device MAC setting is not set, module will log discovered devices");
            }

            if (module.Watcher == null)
            {
                module.LogDebug("Watcher is not defined");
                return false;
            }
            _scanAttempts = 0;

            return await StartWatcher();
        }

        private async Task<bool> StartWatcher(bool invokeDisconnect = true)
        {
            if (module.Watcher != null)
            {
                module.LogDebug("Registering watcher received handler");
                module.Watcher.Received += Watcher_Received;
            }
            module.LogDebug("Registering characteristic change handler");
            _ddm.OnHeartRateCharacteristicValueChange += HandleHeartRateCharacteristicValueChange;
            _ddm.OnConnected += HandleConnected;
            _ddm.OnDisconnected += HandleDisconnected;
            module.LogDebug("Starting watcher");
            _watcherStopper = new();
            module.LogDebug("Generated new watcherStopper");
            var startResult = await module.StartWatcher();
            if (invokeDisconnect && !startResult)
            {
                OnDisconnected?.Invoke();
            }
            return startResult;
        }

        public override Task Teardown()
        {
            Reset();
            _scanAttempts = 0;

            return Task.CompletedTask;
        }

        private void Reset()
        {
            module.LogDebug("Resetting provider");
            module.LogDebug("Cancelling watcherStopper");
            _watcherStopper.Cancel();
            module.LogDebug("Clearing device names");
            _ddm.ClearDevices();
            _ddm.ResetMissingCharacterisicsDevices();
            module.ResetDeviceData();
            module.LogDebug("Clearing processing device MAC");
            _ddm.ProcessingDeviceMac = string.Empty;
            module.LogDebug("Clearing connected device MAC");
            _ddm.ConnectedDeviceMac = string.Empty;
            _ddm.Refresh();
            _ddm.OnHeartRateCharacteristicValueChange -= HandleHeartRateCharacteristicValueChange;
            _ddm.OnConnected -= HandleConnected;
            _ddm.OnDisconnected -= HandleDisconnected;
        }


        // async void handlers rethrow on the thread pool, which would crash the host app, so nothing may escape from here
        private async void Watcher_Received(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            try
            {
                await HandleAdvertisement(args);
            }
            catch (Exception ex)
            {
                module.LogException($"Unhandled error while processing advertisement from {Converter.FormatAsMac(args.BluetoothAddress)}", ex);
            }
        }

        private async Task HandleAdvertisement(BluetoothLEAdvertisementReceivedEventArgs args)
        {
            _ddm.UpdateBluetoothAvailability(true);
            var advertisementMac = Converter.FormatAsMac(args.BluetoothAddress);
            if (_ddm.ProcessingDeviceMac == advertisementMac)
            {
                // Skip advertisements for the device which is already being processed
                return;
            }

            // We need a prefix to follow the logs as advertisements can come in pretty rapidly
            var logPrefix = $"[MAC:{advertisementMac}]";
            var deviceMacSetting = module.GetDeviceMacSetting();
            var isDeviceConfigured = deviceMacSetting != string.Empty;
            var isConfiguredDevice = advertisementMac == deviceMacSetting;

            try
            {
                var deviceData = _ddm.Get(advertisementMac);
                if (deviceData == null)
                {
                    var newDeviceData = await _ddm.Add(args.BluetoothAddress, args.Advertisement, _watcherStopper);
                    if (newDeviceData != null)
                    {
                        deviceData = newDeviceData;
                        module.LogDebug($"{logPrefix} Discovered device: {deviceData?.Label}");
                    }
                }
                else
                {
                    deviceData.LastAdvertisementDateTime = DateTime.Now;
                }

                if (!isDeviceConfigured || !isConfiguredDevice)
                {
                    // Waiting for device mac selection before progressing further
                    return;
                }

                if (_ddm.HeartRateCharacteristic != null)
                {
                    // Characteristic already found
                    return;
                }

                if (_ddm.ProcessingDeviceMac == advertisementMac)
                {
                    // Skip advertisements for the device which is already being processed
                    return;
                }

                _ddm.ProcessingDeviceMac = advertisementMac;
                _ddm.UpdateConnestionStatus(DeviceDataManager.PossibleConnectionStates.Connecting);
                _ddm.Refresh();
                module.LogDebug($"{logPrefix} Begin processing advertisement data");
                IEnumerable<GattDeviceService>? cleanupServices = null;
                try
                {
                    if (_ddm.CurrentDevice == null)
                    {
                        module.LogDebug($"{logPrefix} Setting currrent device");
                        var newCurrentDevice = await _ah.WaitAsync(BluetoothLEDevice.FromBluetoothAddressAsync(args.BluetoothAddress), AsyncTask.SetCurrentDevice, _watcherStopper);
                        if (newCurrentDevice != null)
                        {
                            _ddm.CurrentDevice = newCurrentDevice;
                            var currentDeviceName = _ddm.Get(advertisementMac)?.Name ?? string.Empty;
                            module.LogDebug($"{logPrefix} Found device named {currentDeviceName} for MAC {advertisementMac}");
                            module.SetDeviceName(currentDeviceName);
                            _ddm.RegiterConnectionStatusChangeHandler(logPrefix);
                        }
                        else
                        {
                            module.LogDebug($"{logPrefix} Current device could not be set");
                            return;
                        }
                    }
                    else
                    {
                        module.LogDebug($"{logPrefix} Current device already set");
                    }

                    // CurrentDevice can be reset concurrently, so work from a local reference
                    var currentDevice = _ddm.CurrentDevice;
                    if (currentDevice == null)
                    {
                        module.LogDebug($"{logPrefix} Current device was reset while processing, skipping");
                        return;
                    }

                    var missingCharacteristicUnknown = !_ddm.MissingCharacteristicDevices.Contains(deviceMacSetting);
                    cleanupServices = await FindHeartrateCharacteristic(currentDevice, advertisementMac, logPrefix, deviceMacSetting, missingCharacteristicUnknown);

                    if (_ddm.HeartRateCharacteristic == null && missingCharacteristicUnknown)
                    {
                        _ddm.MissingCharacteristicDevices.Add(deviceMacSetting);
                    }
                }
                catch (Exception ex)
                {
                    module.LogException($"{logPrefix} Failed to connect", ex);
                    module.ResetDeviceData();
                }
                finally
                {
                    try
                    {
                        if (cleanupServices != null)
                        {
                            foreach (var service in cleanupServices.ToList())
                            {
                                try
                                {
                                    service.Dispose();
                                }
                                catch (Exception ex)
                                {
                                    module.LogException($"{logPrefix} Failed to dispose of GATT service", ex);
                                }
                            }
                        }
                        if (_ddm.ConnectedDeviceMac == string.Empty)
                        {
                            _ddm.UpdateConnestionStatus(DeviceDataManager.PossibleConnectionStates.Scanning);
                        }
                    }
                    finally
                    {
                        _ddm.ProcessingDeviceMac = string.Empty;
                        _ddm.Refresh();
                        module.LogDebug($"{logPrefix} Stopped processing advertisement");
                    }
                }

            }
            catch (TaskCanceledException)
            {
                module.LogDebug($"{logPrefix} Watcher cancelled, discarding advertisement");
                return;
            }
        }

        private async Task<IEnumerable<GattDeviceService>?> FindHeartrateCharacteristic(BluetoothLEDevice currentDevice, string advertisementMac, string logPrefix, string deviceMacSetting, bool missingCharacteristicUnknown)
        {
            IEnumerable<GattDeviceService>? cleanupServices = null;
            module.LogDebug($"{logPrefix} Finding HeratRate service");
            GattDeviceServicesResult? servicesResult = null;
            servicesResult = await _ah.WaitAsync(currentDevice.GetGattServicesForUuidAsync(GattServiceUuids.HeartRate, BluetoothCacheMode.Uncached), AsyncTask.GetHeartRateService, _watcherStopper);
            var hasHeartrateService = servicesResult != null && servicesResult.Services.Count > 0;
            _ddm.SetHasHeartrateService(advertisementMac, hasHeartrateService);
            if (hasHeartrateService)
            {
                cleanupServices = servicesResult.Services;
                var firstService = cleanupServices.First();
                if (missingCharacteristicUnknown)
                {
                    module.LogDebug($"{logPrefix} Found heartrate service");
                }
                GattCharacteristicsResult? characteristicsResult = await _ah.WaitAsync(firstService.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.HeartRateMeasurement, BluetoothCacheMode.Uncached), AsyncTask.GetHeartRateCharacteristic, _watcherStopper);
                module.LogDebug($"{logPrefix} Finding HeartRateMeasurement characteristic");
                var hasHeartrateCharacteristic = characteristicsResult != null && characteristicsResult.Characteristics.Count > 0;
                _ddm.SetHasHeartrateCharacteristic(advertisementMac, hasHeartrateCharacteristic);
                if (hasHeartrateCharacteristic)
                {
                    _ddm.ConnectedDeviceMac = advertisementMac;
                    _ddm.Refresh();
                    await _ddm.HandleHeartRateCharacteristicFound(logPrefix, firstService, characteristicsResult.Characteristics[0], _watcherStopper);
                    if (_ddm.HeartRateService != null)
                    {
                        cleanupServices = servicesResult.Services.Where(s => s != _ddm.HeartRateService);
                    }
                }
                else if (missingCharacteristicUnknown)
                {
                    module.LogDebug($"{logPrefix} No heartrate characteristics found");
                }
            }
            else if (missingCharacteristicUnknown)
            {
                module.LogDebug($"{logPrefix} No heartrate service found");
            }

            return cleanupServices;
        }

        private void HandleConnected()
        {
            _scanAttempts = 0;
            _ddm.UpdateConnestionStatus(DeviceDataManager.PossibleConnectionStates.Connected);
            OnConnected?.Invoke();
        }
        private void HandleDisconnected()
        {
            OnDisconnected?.Invoke();
            Reset();
            if (_scanAttempts < 7)
            {
                _scanAttempts++;
            }
            double waitTimeMilliseconds = Math.Pow(2, _scanAttempts) * 100;
            module.LogDebug($"Waiting for {waitTimeMilliseconds / 1e3d}s before scanning again…");
            Task.Delay(TimeSpan.FromMilliseconds(waitTimeMilliseconds)).ContinueWith(async _ =>
            {
                try
                {
                    await StartWatcher();
                }
                catch (Exception ex)
                {
                    module.LogException("Failed to restart watcher after disconnect", ex);
                }
            });
        }

        private void HandleHeartRateCharacteristicValueChange(byte updateData)
        {
            OnHeartrateUpdate?.Invoke(updateData);
        }
    }
}
