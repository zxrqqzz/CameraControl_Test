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

        private string _mac = string.Empty;
        /// <summary>MAC 地址(GigE 相机 / 局域网检测设备才有)</summary>
        public string Mac
        {
            get => _mac;
            set { _mac = value; OnPropertyChanged(); OnPropertyChanged(nameof(Display)); }
        }

        private string _devicePath = string.Empty;
        /// <summary>
        /// 接入标识: GigE 用序列号或 IP，USB3 用 SDK 的 DeviceGUID，
        /// UVC 用 Windows 设备实例路径。连接时靠它在枚举结果里找回设备。
        /// </summary>
        public string DevicePath
        {
            get => _devicePath;
            set { _devicePath = value; OnPropertyChanged(); }
        }

        private string _interfaceText = string.Empty;
        /// <summary>接口/接入方式文字，如 GigE Vision / USB3 Vision / UVC</summary>
        public string InterfaceText
        {
            get => _interfaceText;
            set { _interfaceText = value; OnPropertyChanged(); OnPropertyChanged(nameof(Display)); }
        }

        private DeviceSource _source = DeviceSource.Manual;
        /// <summary>设备的发现来源</summary>
        public DeviceSource Source
        {
            get => _source;
            set
            {
                _source = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Display));
                OnPropertyChanged(nameof(SourceText));
            }
        }

        private bool _isDetectedOnly;
        /// <summary>
        /// 仅检测到(局域网扫描结果)。这类设备只做识别展示，
        /// 本程序不会自动连接、认证或拉取图像。
        /// </summary>
        public bool IsDetectedOnly
        {
            get => _isDetectedOnly;
            set { _isDetectedOnly = value; OnPropertyChanged(); OnPropertyChanged(nameof(Display)); }
        }

        private bool _isInfraredOnly;
        /// <summary>
        /// 是否为红外(IR)摄像头。这类设备是笔记本用来做 Windows Hello 人脸解锁的，
        /// 输出是红外灰度图且常被系统独占，本程序只登记、不取流。
        /// </summary>
        public bool IsInfraredOnly
        {
            get => _isInfraredOnly;
            set
            {
                _isInfraredOnly = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Display));
                OnPropertyChanged(nameof(SubtitleText));
            }
        }

        private bool? _allowImagePersist;        /// <summary>
        /// 是否允许把该设备的图像保存到磁盘。
        /// <c>null</c> = 跟随默认策略(见 <c>ImagePersistenceGuard</c>)，UVC/自带摄像头默认禁止。
        /// 一旦用户显式开启，就不再受"画面彻底消失"的保证。
        /// </summary>
        public bool? AllowImagePersist
        {
            get => _allowImagePersist;
            set { _allowImagePersist = value; OnPropertyChanged(); }
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

        /// <summary>来源显示名</summary>
        public string SourceText => Source.Text();

        /// <summary>去重键(手工设备为空)</summary>
        public string DedupeKey =>
            DeviceDedupe.BuildKey(Source, SerialNumber, DevicePath, Mac, Ip, Name);

        /// <summary>是否允许连接取流</summary>
        public bool CanConnect => !IsDetectedOnly && Source.Connectable();
        public override string Display =>
            $"{Name} ({Vendor} {Model})"
            + (string.IsNullOrEmpty(Ip) ? "" : $" [{Ip}]")
            + (IsDetectedOnly ? " (仅检测)" : "")
            + (IsInfraredOnly ? " (红外·仅人脸)" : "")
            + (IsConnected ? " ✓" : "");

        /// <summary>列表第二行: 来源 · 接口 · 序列号</summary>
        public override string SubtitleText
        {
            get
            {
                var parts = new System.Collections.Generic.List<string>();
                if (Source != DeviceSource.Manual) parts.Add(SourceText);
                if (!string.IsNullOrWhiteSpace(InterfaceText)) parts.Add(InterfaceText);
                if (!string.IsNullOrWhiteSpace(SerialNumber)) parts.Add("SN " + SerialNumber);
                if (IsInfraredOnly) parts.Add("仅 Windows Hello 人脸解锁用，不取流");
                return string.Join(" · ", parts);
            }
        }

        public override string ToString() => Display;
    }
}
