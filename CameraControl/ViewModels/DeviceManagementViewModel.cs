using CameraControl.Models;
using GalaSoft.MvvmLight.Command;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace CameraControl.ViewModels
{
    public class DeviceManagementViewModel : INotifyPropertyChanged
    {
        public ObservableCollection<DeviceBase> Devices { get; } = new();

        // 当前选中的设备
        private DeviceBase? _selectedDevice;
        public DeviceBase? SelectedDevice
        {
            get => _selectedDevice;
            set
            {
                _selectedDevice = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsCameraSelected));
                OnPropertyChanged(nameof(IsLightSourceSelected));
                OnPropertyChanged(nameof(IsDeviceSelected));
                OnPropertyChanged(nameof(CameraVendor));
                OnPropertyChanged(nameof(CameraModel));
                OnPropertyChanged(nameof(CameraIp));
                OnPropertyChanged(nameof(CameraSerialNumber));
                OnPropertyChanged(nameof(LightSourceVendor));
                OnPropertyChanged(nameof(LightSourceModel));
                OnPropertyChanged(nameof(LightSourceBrightness));
            }
        }

        // 侧边栏分类选中
        private bool _showCameraList = true;
        public bool ShowCameraList
        {
            get => _showCameraList;
            set { _showCameraList = value; OnPropertyChanged(); OnPropertyChanged(nameof(FilteredDevices)); }
        }

        private bool _showLightSourceList;
        public bool ShowLightSourceList
        {
            get => _showLightSourceList;
            set { _showLightSourceList = value; OnPropertyChanged(); OnPropertyChanged(nameof(FilteredDevices)); }
        }

        // 选中设备的参数（用于右侧编辑区）
        public bool IsCameraSelected => SelectedDevice is CameraDevice;
        public bool IsLightSourceSelected => SelectedDevice is LightSourceDevice;
        public bool IsDeviceSelected => SelectedDevice != null;

        public string CameraVendor
        {
            get => (SelectedDevice as CameraDevice)?.Vendor ?? string.Empty;
            set
            {
                if (SelectedDevice is CameraDevice cam) { cam.Vendor = value; OnPropertyChanged(); }
            }
        }

        public string CameraModel
        {
            get => (SelectedDevice as CameraDevice)?.Model ?? string.Empty;
            set
            {
                if (SelectedDevice is CameraDevice cam) { cam.Model = value; OnPropertyChanged(); }
            }
        }

        public string CameraIp
        {
            get => (SelectedDevice as CameraDevice)?.Ip ?? string.Empty;
            set
            {
                if (SelectedDevice is CameraDevice cam) { cam.Ip = value; OnPropertyChanged(); }
            }
        }

        public string CameraSerialNumber
        {
            get => (SelectedDevice as CameraDevice)?.SerialNumber ?? string.Empty;
            set
            {
                if (SelectedDevice is CameraDevice cam) { cam.SerialNumber = value; OnPropertyChanged(); }
            }
        }

        public string LightSourceVendor
        {
            get => (SelectedDevice as LightSourceDevice)?.Vendor ?? string.Empty;
            set
            {
                if (SelectedDevice is LightSourceDevice ls) { ls.Vendor = value; OnPropertyChanged(); }
            }
        }

        public string LightSourceModel
        {
            get => (SelectedDevice as LightSourceDevice)?.Model ?? string.Empty;
            set
            {
                if (SelectedDevice is LightSourceDevice ls) { ls.Model = value; OnPropertyChanged(); }
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

        // 根据侧边栏选中过滤设备列表
        public ObservableCollection<DeviceBase> FilteredDevices
        {
            get
            {
                var result = new ObservableCollection<DeviceBase>();
                foreach (var d in Devices)
                {
                    if (ShowCameraList && d is CameraDevice) result.Add(d);
                    else if (ShowLightSourceList && d is LightSourceDevice) result.Add(d);
                }
                return result;
            }
        }

        // 命令
        public ICommand RemoveDeviceCommand { get; }
        public ICommand RenameDeviceCommand { get; }

        public DeviceManagementViewModel()
        {
            RemoveDeviceCommand = new RelayCommand<DeviceBase>(RemoveDevice);
            RenameDeviceCommand = new RelayCommand<DeviceBase>(RenameDevice);
        }

        public event Action<DeviceBase>? RenameRequested;

        private void RenameDevice(DeviceBase? device)
        {
            if (device == null) return;
            RenameRequested?.Invoke(device);
        }

        public void AddDevice(DeviceBase device)
        {
            Devices.Add(device);
            RefreshFiltered();
            App.LogVM.AddLog($"已添加设备: {device.Display}");
        }

        private void RemoveDevice(DeviceBase? device)
        {
            if (device == null) return;
            Devices.Remove(device);
            if (SelectedDevice == device) SelectedDevice = null;
            RefreshFiltered();
            App.LogVM.AddLog($"已删除设备: {device.Display}");
        }

        private void RefreshFiltered()
        {
            OnPropertyChanged(nameof(FilteredDevices));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
