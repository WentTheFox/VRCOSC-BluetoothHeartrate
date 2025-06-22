using System.Windows;
using System.Windows.Media;

namespace BluetoothHeartrateModule
{
    public class DeviceData
    {
        public string MacAddress { get; }

        public string Name { get; set; } = "";
        internal bool NoHeartrateService { get; set; } = false;
        internal bool NoHeartrateCharacteristic { get; set; } = false;

        public string Manufacturer { get; }
        public bool ShowManufacturer { get; }
        public bool IsVirtual { get; }

        public SolidColorBrush StatusColor { get; private set; } = DefaultBrush;
        public Visibility StatusDisplay => StatusColor == DefaultBrush ? Visibility.Collapsed : Visibility.Visible;
        public Visibility NoHeartrateServiceDisplay => NoHeartrateService ? Visibility.Visible : Visibility.Collapsed;
        public Visibility NoHeartrateCharacteristicDisplay => NoHeartrateCharacteristic ? Visibility.Visible : Visibility.Collapsed;
        public DateTime LastAdvertisementDateTime = DateTime.Now;

        const double InactiveAfterSeconds = 60 * 5;
        public static SolidColorBrush ConnectedBrush = new(Color.FromRgb(160, 255, 160));
        public static SolidColorBrush ProcessingBrush = new(Color.FromRgb(200, 128, 64));
        public static SolidColorBrush ScanningBrush = new(Color.FromRgb(160, 160, 255));
        public static SolidColorBrush DefaultBrush = new(Color.FromRgb(0, 0, 0));
        public static SolidColorBrush DefaultLightBrush = new(Color.FromRgb(255, 255, 255));

        private DeviceDataManager _mgr;

        static DeviceData()
        {
            // Freeze all static brushes to make them thread-safe
            ConnectedBrush.Freeze();
            ProcessingBrush.Freeze();
            ScanningBrush.Freeze();
            DefaultBrush.Freeze();
            DefaultLightBrush.Freeze();
        }

        public string Label
        {
            get
            {
                List<string> parts = new();
                if (Name != string.Empty)
                {
                    parts.Add(Name);
                }
                if (MacAddress != string.Empty)
                {
                    parts.Add(parts.Count == 0 ? MacAddress : $"({MacAddress})");
                }
                if (parts.Count == 0)
                {
                    return "Unknown device";
                }
                return String.Join(" ", parts);
            }
        }
        public DeviceData(string mac, DeviceDataManager mgr, bool isVirtual = false)
        {
            this._mgr = mgr;
            MacAddress = mac;
            IsVirtual = isVirtual;

            var macPrefix = MacAddress != string.Empty ? MacAddress.Substring(0, 8) : "";
            this.Manufacturer = this._mgr.PrefixData.ContainsKey(macPrefix) ? this._mgr.PrefixData[macPrefix] : string.Empty;
            this.ShowManufacturer = this.Manufacturer != string.Empty;
        }

        public double GetSecondsSinceLastAdvertisement()
        {
            long lastAdvertisementTimestamp = ((DateTimeOffset)LastAdvertisementDateTime).ToUnixTimeSeconds();
            long currentTimestamp = ((DateTimeOffset)DateTime.Now).ToUnixTimeSeconds();
            return Convert.ToDouble(currentTimestamp - lastAdvertisementTimestamp);
        }

        public bool GetIsInactive()
        {
            return GetSecondsSinceLastAdvertisement() < InactiveAfterSeconds;
        }
        public void UpdateStatusColor()
        {
            if (IsVirtual || GetIsConnected())
            {
                StatusColor = DefaultBrush;
                return;
            }

            double fadeProgress = Math.Min(1f, GetSecondsSinceLastAdvertisement() / InactiveAfterSeconds);
            var newBrush = new SolidColorBrush(Color.FromRgb(
                Convert.ToByte(128 * fadeProgress),
                Convert.ToByte(128 * fadeProgress),
                Convert.ToByte(255 - (128 * fadeProgress))
            ));
            newBrush.Freeze();
            StatusColor = newBrush;
        }

        private bool GetIsConnected()
        {
            return MacAddress == _mgr.ConnectedDeviceMac;
        }

        private bool GetIsProcessing()
        {
            return MacAddress == _mgr.ProcessingDeviceMac;
        }
    }
}
