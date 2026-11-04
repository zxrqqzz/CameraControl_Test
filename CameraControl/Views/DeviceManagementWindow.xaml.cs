using CameraControl.Models;
using CameraControl.ViewModels;
using System;
using System.Windows;

namespace CameraControl.Views
{
    public partial class DeviceManagementWindow : Window
    {
        public DeviceManagementWindow()
        {
            InitializeComponent();
            DataContext = App.DeviceManagement;

            App.DeviceManagement.RenameRequested += OnRenameRequested;
            App.DeviceManagement.ScanRequested += OnScanRequested;

            Closed += (_, _) =>
            {
                App.DeviceManagement.RenameRequested -= OnRenameRequested;
                App.DeviceManagement.ScanRequested -= OnScanRequested;
            };
        }

        /// <summary>手工添加设备</summary>
        private void OnAddDeviceClick(object sender, RoutedEventArgs e)
        {
            var dlg = new AddDeviceWindow { Owner = this };
            if (dlg.ShowDialog() == true && dlg.Result != null)
            {
                App.DeviceManagement.AddDevice(dlg.Result);
            }
        }

        /// <summary>扫描自动发现设备</summary>
        private void OnScanClick(object sender, RoutedEventArgs e)
        {
            App.DeviceManagement.RequestScan();
        }

        private void OnScanRequested()
        {
            try
            {
                var dlg = new DeviceScanWindow { Owner = this };
                dlg.ShowDialog();
            }
            catch (Exception e)
            {
                // 扫描窗口打不开时不要让整个程序跟着崩
                App.LogVM.AddLog("[扫描] 无法打开扫描窗口: " + e.Message);
                MessageBox.Show(this,
                    "无法打开扫描窗口：\n" + e.Message,
                    "扫描失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OnRenameRequested(DeviceBase device)
        {
            var dlg = new RenameDeviceWindow(device) { Owner = this };
            dlg.ShowDialog();
        }
    }
}
