using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CameraControl.Models
{
    /// <summary>
    /// 扫描到的一个候选设备(还没加入设备列表)。
    /// 扫描结果先展示给用户勾选，确认后才转成真正的 <see cref="CameraDevice"/>。
    /// </summary>
    public class ScanCandidate : INotifyPropertyChanged
    {
        private bool _isSelected = true;
        private bool _isAlreadyAdded;

        /// <summary>用户是否勾选了这个设备</summary>
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                OnPropertyChanged();
            }
        }

        /// <summary>该设备已经在设备列表里了(重复扫描时标记，不允许再次添加)</summary>
        public bool IsAlreadyAdded
        {
            get => _isAlreadyAdded;
            set
            {
                if (_isAlreadyAdded == value) return;
                _isAlreadyAdded = value;

                // 变成"已存在"时自动取消勾选，并显式通知界面刷新复选框
                if (value && _isSelected)
                {
                    _isSelected = false;
                    OnPropertyChanged(nameof(IsSelected));
                }

                OnPropertyChanged();
                OnPropertyChanged(nameof(CanSelect));
                OnPropertyChanged(nameof(StatusText));
            }
        }

        /// <summary>设备名称(添加进列表后作为 Name)</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>厂商</summary>
        public string Vendor { get; set; } = string.Empty;

        /// <summary>型号</summary>
        public string Model { get; set; } = string.Empty;

        /// <summary>序列号(海康设备/部分 UVC 设备可拿到)</summary>
        public string SerialNumber { get; set; } = string.Empty;

        /// <summary>IP 地址(GigE 相机 / 局域网检测到的设备)</summary>
        public string Ip { get; set; } = string.Empty;

        /// <summary>MAC 地址(GigE 相机 / 局域网检测)</summary>
        public string Mac { get; set; } = string.Empty;

        /// <summary>接入标识: GigE 为序列号或 IP，USB 设备为 DeviceGUID，UVC 为 Windows 设备实例路径</summary>
        public string DevicePath { get; set; } = string.Empty;

        /// <summary>接口/接入方式的可读文字，如 GigE Vision / USB3 Vision / UVC</summary>
        public string InterfaceText { get; set; } = string.Empty;

        /// <summary>发现来源</summary>
        public DeviceSource Source { get; set; } = DeviceSource.Manual;

        /// <summary>
        /// 是否为红外(IR)摄像头：Windows Hello 人脸解锁专用，输出红外灰度图，
        /// 本程序只登记、不取流。
        /// </summary>
        public bool IsInfraredOnly { get; set; }

        /// <summary>是否只是"检测到"，不能连接取流(局域网扫描结果)</summary>
        public bool IsDetectedOnly => Source.DetectedOnly();

        /// <summary>是否允许勾选添加</summary>
        public bool CanSelect => !IsAlreadyAdded;

        /// <summary>来源显示名</summary>
        public string SourceText => Source.Text();

        /// <summary>状态文案</summary>
        public string StatusText =>
            IsAlreadyAdded ? "已在列表中"
            : IsDetectedOnly ? "仅检测 · 不可连接"
            : IsInfraredOnly ? "红外 · 仅人脸解锁"
            : "可添加";

        /// <summary>去重键(手工来源为空)</summary>
        public string DedupeKey =>
            DeviceDedupe.BuildKey(Source, SerialNumber, DevicePath, Mac, Ip, Name);

        /// <summary>列表里展示的说明文字</summary>
        public string DetailText
        {
            get
            {
                var parts = new System.Collections.Generic.List<string>();
                if (!string.IsNullOrWhiteSpace(Ip)) parts.Add("IP " + Ip);
                if (!string.IsNullOrWhiteSpace(Mac)) parts.Add("MAC " + Mac);
                if (!string.IsNullOrWhiteSpace(SerialNumber)) parts.Add("SN " + SerialNumber);
                if (!string.IsNullOrWhiteSpace(InterfaceText)) parts.Add(InterfaceText);
                return string.Join(" · ", parts);
            }
        }

        /// <summary>转成可加入设备列表的相机对象</summary>
        public CameraDevice ToCameraDevice()
        {
            return new CameraDevice
            {
                Name = string.IsNullOrWhiteSpace(Name) ? FallbackName() : Name,
                Vendor = Vendor,
                Model = Model,
                SerialNumber = SerialNumber,
                Ip = Ip,
                Mac = Mac,
                DevicePath = DevicePath,
                InterfaceText = InterfaceText,
                Source = Source,
                IsDetectedOnly = IsDetectedOnly,
                IsInfraredOnly = IsInfraredOnly
            };
        }

        private string FallbackName()
        {
            if (!string.IsNullOrWhiteSpace(Model)) return Model;
            if (!string.IsNullOrWhiteSpace(Ip)) return Ip;
            if (!string.IsNullOrWhiteSpace(DevicePath)) return DevicePath;
            return "相机";
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
