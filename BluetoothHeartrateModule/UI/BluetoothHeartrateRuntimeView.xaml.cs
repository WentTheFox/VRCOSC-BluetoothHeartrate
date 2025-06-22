using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BluetoothHeartrateModule.UI
{
    partial class BluetoothHeartrateRuntimeView: UserControl
    {

        public BluetoothHeartrateModule Module { get; }
        public ObservableCollection<DeviceData> Devices { get; } = new();

        const string BluetoothOnImage = "bt-on";
        const string BluetoothOffImage = "bt-off";
        const string BluetoothAvailableStatusText = "Bluetooth is available";
        const string BluetoothUnavailableStatusText = "Bluetooth is currently unavailable";

        public bool DeviceReadOnly = true;
        public bool DeviceEditable = false;

        public BluetoothHeartrateRuntimeView(BluetoothHeartrateModule module)
        {
            this.Module = module;
            InitializeComponent();

            DataContext = this;
        }

        private void HandleDeviceListUpdate()
        {
            Dispatcher.Invoke(new Action(() =>
            {
                DeviceData[] updatedDevices = Module.DeviceDataManager.GetDevices();
                var selectedDeviceMac = Module.GetDeviceMacSetting();
                DeviceReadOnly = selectedDeviceMac != string.Empty;
                DeviceEditable = !DeviceReadOnly;
                var deviceCount = updatedDevices.Count();
                DeviceSelection.Watermark = $"{(deviceCount > 0 ? $"{deviceCount} " : "")}Device{(deviceCount != 1 ? "s" : "")}";
                Devices.Clear();

                var sortedDevices = new List<DeviceData>(updatedDevices);
                sortedDevices.Sort((a, b) =>
                {
                    if (a.GetIsInactive()) { return 1; }
                    if (b.GetIsInactive()) { return -1; }
                    return a.Label.CompareTo(b.Label);
                });
                bool selectedItemFound = false;
                foreach (var deviceData in sortedDevices)
                {
                    deviceData.UpdateStatusColor();
                    Devices.Add(deviceData);
                    if (!selectedItemFound && deviceData.MacAddress == selectedDeviceMac)
                    {
                        DeviceSelection.SelectedItem = deviceData;
                        selectedItemFound = true;
                    }
                }
                if (!selectedItemFound) {
                    var missingDevice = new DeviceData(selectedDeviceMac, Module.DeviceDataManager, true);
                    missingDevice.Name = selectedDeviceMac == string.Empty ? (string)DeviceSelection.Watermark : "Selected Device";
                    Devices.Add(missingDevice);
                    DeviceSelection.SelectedItem = missingDevice;
                }
            }));
        }

        private void HandleBluetoothAvailabilityChange()
        {
            Dispatcher.Invoke(new Action(() =>
            {
                if (BluetoothIcon == null)
                {
                    return;
                }
                var available = Module.DeviceDataManager.GetBluetoothAvailability();
                var targetResource = available ? BluetoothOnImage : BluetoothOffImage;
                var resourceUri = ResourceAccessor.Get($"img/{targetResource}.png");
                BluetoothIcon.Source = new BitmapImage(resourceUri);
                BluetoothAvailabilityTextBlock.Text = available ? BluetoothAvailableStatusText : BluetoothUnavailableStatusText;
            }));
        }
        private void HandleConnectionStatusChange()
        {
            Dispatcher.Invoke(new Action(() =>
            {
                var connectionStatus = Module.DeviceDataManager.GetConnectionStatus();
                var connectionStatusColor = GetConnectionStatusForegroundBrush(connectionStatus);
                ConnectionStatusCircle.Fill = connectionStatusColor;
                ConnectionStatusTextBlock.Text = GetConnectionStatusText(connectionStatus);
                ConnectionStatusTextBlock.Foreground = connectionStatusColor;
            }));
        }

        private string GetConnectionStatusText(DeviceDataManager.PossibleConnectionStates connectionStatus)
        {
            switch (connectionStatus)
            {
                case DeviceDataManager.PossibleConnectionStates.Connected:
                    return "Connected";
                case DeviceDataManager.PossibleConnectionStates.Idle:
                    return "Idle";
                case DeviceDataManager.PossibleConnectionStates.Scanning:
                    return "Scanning…";
                case DeviceDataManager.PossibleConnectionStates.Connecting:
                    return "Connecting…";
            }

            return "(status unknown)";
        }

        private Brush GetConnectionStatusForegroundBrush(DeviceDataManager.PossibleConnectionStates connectionStatus)
        {
            switch (connectionStatus)
            {
                case DeviceDataManager.PossibleConnectionStates.Connected:
                    return DeviceData.ConnectedBrush;
                case DeviceDataManager.PossibleConnectionStates.Scanning:
                    return DeviceData.ScanningBrush;
                case DeviceDataManager.PossibleConnectionStates.Connecting:
                    return DeviceData.ProcessingBrush;
            }

            return DeviceData.DefaultLightBrush;
        }

        private void DeviceSelection_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var comboBox = (ComboBox)sender;
            var selectedValue = (DeviceData)comboBox.SelectedValue;

            if (selectedValue != null)
            {
                Module.SetDeviceMacSetting(selectedValue.MacAddress);
            }
        }

        private void Reset_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            Module.ClearDeviceMacSetting();
        }


        private void OnLoad(object sender, RoutedEventArgs e)
        {
            this.Module.DeviceDataManager.OnDeviceListUpdate += HandleDeviceListUpdate;
            this.Module.DeviceDataManager.OnBluetoothAvailabilityChange += HandleBluetoothAvailabilityChange;
            this.Module.DeviceDataManager.OnConnectionStatusChange += HandleConnectionStatusChange;
            HandleDeviceListUpdate();
            HandleBluetoothAvailabilityChange();
            HandleConnectionStatusChange();
        }
    }
}
