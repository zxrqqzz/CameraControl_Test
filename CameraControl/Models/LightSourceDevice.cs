namespace CameraControl.Models
{
    public class LightSourceDevice : DeviceBase
    {
        public override DeviceType DeviceType => DeviceType.LightSource;

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

        private int _brightness;
        public int Brightness
        {
            get => _brightness;
            set { _brightness = value; OnPropertyChanged(); }
        }

        private bool _isOn;
        public bool IsOn
        {
            get => _isOn;
            set { _isOn = value; OnPropertyChanged(); OnPropertyChanged(nameof(Display)); }
        }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        public override string Display =>
            $"{Name} ({Vendor} {Model})" + (IsOn ? " [ON]" : " [OFF]");

        public override string ToString() => Display;
    }
}
