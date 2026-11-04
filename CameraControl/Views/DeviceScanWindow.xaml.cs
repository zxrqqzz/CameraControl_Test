using CameraControl.ViewModels;
using System;
using System.Windows;

namespace CameraControl.Views
{
    /// <summary>设备扫描窗口</summary>
    public partial class DeviceScanWindow : Window
    {
        private readonly DeviceScanViewModel _vm;

        public DeviceScanWindow()
        {
            InitializeComponent();
            _vm = new DeviceScanViewModel();
            DataContext = _vm;

            _vm.DevicesAdded += OnDevicesAdded;
            Closed += (_, _) => _vm.DevicesAdded -= OnDevicesAdded;
        }

        /// <summary>有设备被加入列表后关闭窗口，把焦点交回设备管理</summary>
        private void OnDevicesAdded(int count)
        {
            if (count <= 0) return;

            try
            {
                // 只有用 ShowDialog 打开时才能设置 DialogResult
                DialogResult = true;
            }
            catch (InvalidOperationException)
            {
                Close();
            }
        }
    }
}
