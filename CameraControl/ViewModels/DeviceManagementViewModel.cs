using CameraControl.Models;
using CameraControl.Services;
using GalaSoft.MvvmLight.Command;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace CameraControl.ViewModels
{
    /// <summary>
    /// 设备管理 ViewModel。
    ///
    /// 职责:
    /// - 维护相机/光源台账(可持久化到 settings.json)
    /// - 按关键字(名称/厂商/型号/IP/序列号/来源)检索，按类型(全部/相机/光源)分类
    /// - 接收"扫描"结果: 去重后批量加入设备列表
    /// - 暴露当前选中的相机，供主界面决定连接哪一台
    /// </summary>
    public class DeviceManagementViewModel : INotifyPropertyChanged
    {
        private readonly object _sync = new object();
        private bool _restoring;
        private bool _suppressSave;

        /// <summary>扫描选项(勾选状态来自设置，扫描界面直接改这个对象)</summary>
        public ScanOptions Scanning { get; private set; } = new ScanOptions();

        /// <summary>全部设备(相机 + 光源)</summary>
        public ObservableCollection<DeviceBase> Devices { get; } = new();

        /// <summary>按分类 + 关键字过滤后的设备列表(绑定到界面)</summary>
        public ObservableCollection<DeviceBase> FilteredDevices { get; } = new();

        // ────────────────────────────────────────────────────────────────
        // 检索与分类
        // ────────────────────────────────────────────────────────────────

        private string _searchText = string.Empty;
        /// <summary>检索关键字: 同时匹配名称、厂商、型号、IP、序列号、来源</summary>
        public string SearchText
        {
            get => _searchText;
            set
            {
                string text = value ?? string.Empty;
                if (_searchText == text) return;
                _searchText = text;
                OnPropertyChanged();
                RefreshFiltered();
            }
        }

        private DeviceListCategory _category = DeviceListCategory.All;

        /// <summary>
        /// 侧边栏分类。三个 bool 属性只用于 RadioButton 双向绑定:
        /// 被取消的那个写入 false 时直接忽略，真正生效的是被选中的那个写入 true。
        /// </summary>
        public bool ShowAllList
        {
            get => _category == DeviceListCategory.All;
            set { if (value) SetCategory(DeviceListCategory.All); }
        }

        /// <summary>侧边栏: 只看相机</summary>
        public bool ShowCameraList
        {
            get => _category == DeviceListCategory.Camera;
            set { if (value) SetCategory(DeviceListCategory.Camera); }
        }

        /// <summary>侧边栏: 只看光源</summary>
        public bool ShowLightSourceList
        {
            get => _category == DeviceListCategory.LightSource;
            set { if (value) SetCategory(DeviceListCategory.LightSource); }
        }

        private void SetCategory(DeviceListCategory category)
        {
            if (_category == category) return;
            _category = category;
            OnPropertyChanged(nameof(ShowAllList));
            OnPropertyChanged(nameof(ShowCameraList));
            OnPropertyChanged(nameof(ShowLightSourceList));
            RefreshFiltered();
        }

        // ────────────────────────────────────────────────────────────────
        // 选中设备
        // ────────────────────────────────────────────────────────────────

        private DeviceBase? _selectedDevice;
        public DeviceBase? SelectedDevice
        {
            get => _selectedDevice;
            set
            {
                if (ReferenceEquals(_selectedDevice, value)) return;
                _selectedDevice = value;
                OnPropertyChanged();
                RaiseSelectedDeviceProperties();
            }
        }

        /// <summary>当前选中的相机(光源或未选中时为 null)</summary>
        public CameraDevice? SelectedCamera => SelectedDevice as CameraDevice;

        /// <summary>是否选中了相机</summary>
        public bool IsCameraSelected => SelectedDevice is CameraDevice;

        /// <summary>是否选中了光源</summary>
        public bool IsLightSourceSelected => SelectedDevice is LightSourceDevice;

        /// <summary>是否选中了任意设备</summary>
        public bool IsDeviceSelected => SelectedDevice != null;

        /// <summary>单次通知所有依赖选中设备的属性</summary>
        private void RaiseSelectedDeviceProperties()
        {
            OnPropertyChanged(nameof(SelectedCamera));
            OnPropertyChanged(nameof(IsCameraSelected));
            OnPropertyChanged(nameof(IsLightSourceSelected));
            OnPropertyChanged(nameof(IsDeviceSelected));
            OnPropertyChanged(nameof(CameraVendor));
            OnPropertyChanged(nameof(CameraModel));
            OnPropertyChanged(nameof(CameraIp));
            OnPropertyChanged(nameof(CameraSerialNumber));
            OnPropertyChanged(nameof(CameraMacText));
            OnPropertyChanged(nameof(CameraInterfaceText));
            OnPropertyChanged(nameof(CameraSourceText));
            OnPropertyChanged(nameof(CameraIsDetectedOnly));
            OnPropertyChanged(nameof(ShowPrivacySection));
            OnPropertyChanged(nameof(CameraAllowImagePersist));
            OnPropertyChanged(nameof(CameraPrivacyHint));
            OnPropertyChanged(nameof(CameraIsPrivacyProtected));
            OnPropertyChanged(nameof(LightSourceVendor));
            OnPropertyChanged(nameof(LightSourceModel));
            OnPropertyChanged(nameof(LightSourceBrightness));
        }

        // 右侧编辑区: 相机
        public string CameraVendor
        {
            get => SelectedCamera?.Vendor ?? string.Empty;
            set
            {
                if (SelectedCamera is CameraDevice cam) { cam.Vendor = value; OnPropertyChanged(); RefreshFiltered(); }
            }
        }

        public string CameraModel
        {
            get => SelectedCamera?.Model ?? string.Empty;
            set
            {
                if (SelectedCamera is CameraDevice cam) { cam.Model = value; OnPropertyChanged(); RefreshFiltered(); }
            }
        }

        public string CameraIp
        {
            get => SelectedCamera?.Ip ?? string.Empty;
            set
            {
                if (SelectedCamera is CameraDevice cam) { cam.Ip = value; OnPropertyChanged(); RefreshFiltered(); }
            }
        }

        public string CameraSerialNumber
        {
            get => SelectedCamera?.SerialNumber ?? string.Empty;
            set
            {
                if (SelectedCamera is CameraDevice cam) { cam.SerialNumber = value; OnPropertyChanged(); RefreshFiltered(); }
            }
        }

        /// <summary>MAC 地址(只读展示，扫描得到)</summary>
        public string CameraMacText => SelectedCamera is CameraDevice cam && !string.IsNullOrWhiteSpace(cam.Mac)
            ? cam.Mac
            : "—";

        /// <summary>接入方式(只读展示，扫描得到)</summary>
        public string CameraInterfaceText => SelectedCamera is CameraDevice cam && !string.IsNullOrWhiteSpace(cam.InterfaceText)
            ? cam.InterfaceText
            : "—";

        /// <summary>来源(只读展示)</summary>
        public string CameraSourceText => SelectedCamera?.SourceText ?? "—";

        /// <summary>是否只是局域网检测到的设备(不可连接)</summary>
        public bool CameraIsDetectedOnly => SelectedCamera?.IsDetectedOnly == true;

        // ────────────────────────────────────────────────────────────────
        // 隐私保护 (UVC / 笔记本自带摄像头)
        // ────────────────────────────────────────────────────────────────

        /// <summary>是否显示"图像保存"设置区块(只对 UVC / 自带摄像头有意义)</summary>
        public bool ShowPrivacySection => SelectedCamera?.Source == DeviceSource.Uvc;

        /// <summary>
        /// 是否允许把该设备的图像保存到磁盘。
        /// 默认跟随 <see cref="ImagePersistenceGuard"/>：UVC/自带摄像头禁止。
        /// </summary>
        public bool CameraAllowImagePersist
        {
            get => ImagePersistenceGuard.IsPersistAllowed(SelectedCamera);
            set
            {
                if (SelectedCamera is not CameraDevice cam) return;
                if (cam.AllowImagePersist == value) return;

                cam.AllowImagePersist = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CameraAllowImagePersist));
                OnPropertyChanged(nameof(CameraPrivacyHint));
                OnPropertyChanged(nameof(CameraIsPrivacyProtected));

                App.LogVM.AddLog(value
                    ? $"[隐私] 「{cam.Name}」已允许保存图像 —— 此时无法保证画面彻底消失。"
                    : $"[隐私] 「{cam.Name}」已恢复为仅内存缓存，图像不写磁盘。");

                Save();
            }
        }

        /// <summary>给界面看的隐私策略说明</summary>
        public string CameraPrivacyHint => ImagePersistenceGuard.DescribePolicy(SelectedCamera);

        /// <summary>当前选中相机是否受隐私保护(图像不得落盘)</summary>
        public bool CameraIsPrivacyProtected => ImagePersistenceGuard.IsPrivacyProtected(SelectedCamera);

        // 右侧编辑区: 光源
        public string LightSourceVendor
        {
            get => (SelectedDevice as LightSourceDevice)?.Vendor ?? string.Empty;
            set
            {
                if (SelectedDevice is LightSourceDevice ls) { ls.Vendor = value; OnPropertyChanged(); RefreshFiltered(); }
            }
        }

        public string LightSourceModel
        {
            get => (SelectedDevice as LightSourceDevice)?.Model ?? string.Empty;
            set
            {
                if (SelectedDevice is LightSourceDevice ls) { ls.Model = value; OnPropertyChanged(); RefreshFiltered(); }
            }
        }

        public int LightSourceBrightness
        {
            get => (SelectedDevice as LightSourceDevice)?.Brightness ?? 0;
            set
            {
                if (SelectedDevice is LightSourceDevice ls) { ls.Brightness = value; OnPropertyChanged(); }
            }
        }

        // ────────────────────────────────────────────────────────────────
        // 命令
        // ────────────────────────────────────────────────────────────────

        public ICommand RemoveDeviceCommand { get; }
        public ICommand RenameDeviceCommand { get; }
        public ICommand ClearSearchCommand { get; }

        /// <summary>请求扫描(由窗口打开扫描界面)</summary>
        public event Action? ScanRequested;

        /// <summary>请求重命名(由窗口弹出重命名对话框)</summary>
        public event Action<DeviceBase>? RenameRequested;

        public DeviceManagementViewModel()
        {
            RemoveDeviceCommand = new RelayCommand<DeviceBase>(RemoveDevice);
            RenameDeviceCommand = new RelayCommand<DeviceBase>(RenameDevice);
            ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty);
        }

        /// <summary>
        /// 发起扫描。真正的扫描界面由 View 层打开(VM 不碰窗口)。
        /// </summary>
        public void RequestScan() => ScanRequested?.Invoke();

        private void RenameDevice(DeviceBase? device)
        {
            if (device == null) return;
            RenameRequested?.Invoke(device);
        }

        // ────────────────────────────────────────────────────────────────
        // 增删
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// 添加一个设备。相机按去重键查重，已存在则跳过(返回 false)。
        /// </summary>
        public bool AddDevice(DeviceBase device)
        {
            if (device == null) return false;

            if (device is CameraDevice camera && IsDuplicate(camera))
            {
                App.LogVM.AddLog($"设备已存在，已跳过: {camera.Display}");
                return false;
            }

            lock (_sync)
            {
                device.PropertyChanged += OnDevicePropertyChanged;
                Devices.Add(device);
            }

            RefreshFiltered();
            Save();
            App.LogVM.AddLog($"已添加设备: {device.Display}");
            return true;
        }

        /// <summary>
        /// 批量添加扫描结果。返回实际添加的设备数量。
        /// </summary>
        public int AddScannedDevices(IEnumerable<ScanCandidate> candidates)
        {
            int added = 0;
            int skipped = 0;

            foreach (ScanCandidate candidate in candidates)
            {
                if (!candidate.IsSelected) continue;

                CameraDevice camera = candidate.ToCameraDevice();
                if (AddDevice(camera)) added++;
                else skipped++;
            }

            if (added > 0 || skipped > 0)
                App.LogVM.AddLog($"扫描结果: 新增 {added} 个，跳过重复 {skipped} 个。");

            // 只有一个设备时顺手选中，省得再点一次
            if (added == 1 && SelectedDevice == null)
                SelectedDevice = Devices.LastOrDefault(d => d is CameraDevice);

            return added;
        }

        /// <summary>相机是否已在列表里(按去重键判断)</summary>
        public bool IsDuplicate(CameraDevice camera)
        {
            string key = camera.DedupeKey;
            if (key.Length == 0) return false;

            foreach (DeviceBase device in Devices)
            {
                if (device is CameraDevice existing &&
                    string.Equals(existing.DedupeKey, key, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>把扫描结果里"已存在"的设备标出来，界面上禁止勾选</summary>
        public void MarkAlreadyAdded(IEnumerable<ScanCandidate> candidates)
        {
            foreach (ScanCandidate candidate in candidates)
            {
                string key = candidate.DedupeKey;
                if (key.Length == 0) continue;

                candidate.IsAlreadyAdded = Devices
                    .OfType<CameraDevice>()
                    .Any(existing => string.Equals(existing.DedupeKey, key, StringComparison.Ordinal));
            }
        }

        private void RemoveDevice(DeviceBase? device)
        {
            if (device == null) return;

            lock (_sync)
            {
                device.PropertyChanged -= OnDevicePropertyChanged;
                Devices.Remove(device);
            }

            if (ReferenceEquals(SelectedDevice, device)) SelectedDevice = null;

            RefreshFiltered();
            Save();
            App.LogVM.AddLog($"已删除设备: {device.Display}");
        }

        /// <summary>设备属性变化时，检索结果可能需要重新过滤</summary>
        private void OnDevicePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(_searchText)) return;
            if (e.PropertyName is nameof(DeviceBase.Name) or nameof(DeviceBase.Display))
                RefreshFiltered();
        }

        // ────────────────────────────────────────────────────────────────
        // 过滤
        // ────────────────────────────────────────────────────────────────

        /// <summary>重建过滤后的列表，并尽量保留当前选中项</summary>
        private void RefreshFiltered()
        {
            DeviceBase? previousSelection = _selectedDevice;

            FilteredDevices.Clear();
            foreach (DeviceBase device in Devices)
            {
                if (!MatchesCategory(device)) continue;
                if (!MatchesSearch(device)) continue;
                FilteredDevices.Add(device);
            }

            if (previousSelection != null)
            {
                DeviceBase? stillVisible = FilteredDevices.FirstOrDefault(d => ReferenceEquals(d, previousSelection));
                if (stillVisible == null)
                {
                    SelectedDevice = null;
                }
                else if (!ReferenceEquals(stillVisible, previousSelection))
                {
                    SelectedDevice = stillVisible;
                }
            }

            OnPropertyChanged(nameof(FilteredDevices));
        }

        private bool MatchesCategory(DeviceBase device) => _category switch
        {
            DeviceListCategory.Camera => device is CameraDevice,
            DeviceListCategory.LightSource => device is LightSourceDevice,
            _ => true
        };

        /// <summary>关键字匹配: 名称 / 厂商 / 型号 / IP / 序列号 / 来源，任意命中即可</summary>
        private bool MatchesSearch(DeviceBase device)
        {
            if (string.IsNullOrWhiteSpace(_searchText)) return true;
            string keyword = _searchText.Trim();

            foreach (string field in SearchFields(device))
            {
                if (string.IsNullOrWhiteSpace(field)) continue;
                if (field.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private static IEnumerable<string> SearchFields(DeviceBase device)
        {
            yield return device.Name;
            yield return device.Display;

            if (device is CameraDevice camera)
            {
                yield return camera.Vendor;
                yield return camera.Model;
                yield return camera.Ip;
                yield return camera.SerialNumber;
                yield return camera.Mac;
                yield return camera.InterfaceText;
                yield return camera.SourceText;
            }
            else if (device is LightSourceDevice light)
            {
                yield return light.Vendor;
                yield return light.Model;
            }
        }

        // ────────────────────────────────────────────────────────────────
        // 持久化
        // ────────────────────────────────────────────────────────────────

        /// <summary>从保存的设置恢复相机列表与扫描选项(不触发再次保存)</summary>
        public void RestoreFromSettings(IEnumerable<CameraDeviceSettings> cameras, ScanSettings? scanSettings = null)
        {
            if (scanSettings != null)
                Scanning = ScanOptions.FromSettings(scanSettings);

            _restoring = true;
            try
            {
                foreach (CameraDeviceSettings settings in cameras)
                {
                    if (string.IsNullOrWhiteSpace(settings.Name)) continue;

                    var camera = new CameraDevice
                    {
                        Name = settings.Name,
                        Vendor = settings.Vendor,
                        Model = settings.Model,
                        SerialNumber = settings.SerialNumber,
                        Ip = settings.Ip,
                        Mac = settings.Mac,
                        DevicePath = settings.DevicePath,
                        InterfaceText = settings.InterfaceText,
                        Source = settings.Source,
                        IsDetectedOnly = settings.IsDetectedOnly,
                        IsInfraredOnly = settings.IsInfraredOnly,
                        AllowImagePersist = settings.AllowImagePersist
                    };

                    camera.PropertyChanged += OnDevicePropertyChanged;
                    Devices.Add(camera);
                }
            }
            finally
            {
                _restoring = false;
            }

            RefreshFiltered();

            if (Devices.Count > 0)
                App.LogVM.AddLog($"已从设置恢复 {Devices.Count} 个设备。");
        }

        /// <summary>导出扫描选项(供 AppSettingsService 保存)</summary>
        public ScanSettings BuildScanSettings()
        {
            var settings = new ScanSettings();
            Scanning.ApplyTo(settings);
            return settings;
        }

        /// <summary>导出成可持久化的设置(供 AppSettingsService 保存)</summary>
        public List<CameraDeviceSettings> BuildSettings()
        {
            var result = new List<CameraDeviceSettings>();
            foreach (DeviceBase device in Devices)
            {
                if (device is not CameraDevice camera) continue;

                result.Add(new CameraDeviceSettings
                {
                    Name = camera.Name,
                    Vendor = camera.Vendor,
                    Model = camera.Model,
                    SerialNumber = camera.SerialNumber,
                    Ip = camera.Ip,
                    Mac = camera.Mac,
                    DevicePath = camera.DevicePath,
                    InterfaceText = camera.InterfaceText,
                    Source = camera.Source,
                    IsDetectedOnly = camera.IsDetectedOnly,
                    IsInfraredOnly = camera.IsInfraredOnly,
                    AllowImagePersist = camera.AllowImagePersist
                });
            }
            return result;
        }

        /// <summary>配置变化时落盘(恢复过程中不保存，避免启动时反复写文件)</summary>
        private void Save()
        {
            if (_restoring || _suppressSave) return;
            App.Settings?.SaveAll();
        }

        /// <summary>批量导入期间临时禁止保存，导入完成后由调用方统一触发一次</summary>
        public IDisposable BeginBatchUpdate()
        {
            _suppressSave = true;
            return new BatchScope(this);
        }

        private sealed class BatchScope : IDisposable
        {
            private readonly DeviceManagementViewModel _owner;
            private bool _disposed;

            public BatchScope(DeviceManagementViewModel owner) => _owner = owner;

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _owner._suppressSave = false;
                _owner.Save();
            }
        }

        // ────────────────────────────────────────────────────────────────

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>设备管理侧边栏的分类</summary>
    public enum DeviceListCategory
    {
        /// <summary>全部</summary>
        All,
        /// <summary>相机</summary>
        Camera,
        /// <summary>光源</summary>
        LightSource
    }
}
