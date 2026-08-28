namespace CameraControl.Models
{
    public class CameraDevice : DeviceBase
    {
        public override DeviceType DeviceType => DeviceType.Camera;

        private string _vendor = string.Empty;
        public string Vendor
        {
            get => _vendor;
            set { _vendor = value; OnPropertyChanged(); OnPropertyChanged(nameof(Display)); }
        }

        private string _model = string.Empty;
        public string Model
        {
            get => _model;
            set { _model = value; OnPropertyChanged(); OnPropertyChanged(nameof(Display)); }
        }

        private string _serialNumber = string.Empty;
        public string SerialNumber
        {
            get => _serialNumber;
            set { _serialNumber = value; OnPropertyChanged(); OnPropertyChanged(nameof(Display)); }
        }

        private string _ip = string.Empty;
        public string Ip
        {
            get => _ip;
            set { _ip = value; OnPropertyChanged(); OnPropertyChanged(nameof(Display)); }
        }

        private bool _isConnected;
        public bool IsConnected
        {
            get => _isConnected;
            set { _isConnected = value; OnPropertyChanged(); OnPropertyChanged(nameof(Display)); }
        }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        public override string Display =>
            $"{Name} ({Vendor} {Model})" + (string.IsNullOrEmpty(Ip) ? "" : $" [{Ip}]")
            + (IsConnected ? " ✓" : "");

        public override string ToString() => Display;
    }
}
