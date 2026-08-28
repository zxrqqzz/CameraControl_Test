using CameraControl.Models;
using System.Windows;

namespace CameraControl.Views
{
    public partial class AddDeviceWindow : Window
    {
        public DeviceBase? Result { get; private set; }

        public AddDeviceWindow()
        {
            InitializeComponent();
            RadCamera.Checked += (_, _) => UpdateType();
            RadLight.Checked += (_, _) => UpdateType();
            UpdateType();
        }

        private void UpdateType()
        {
            bool isCamera = RadCamera.IsChecked == true;
            var vis = isCamera ? Visibility.Visible : Visibility.Collapsed;
            LblIp.Visibility = vis;
            TxtIp.Visibility = vis;
            Title = isCamera ? "添加相机" : "添加光源";
        }

        private void OnOkClick(object sender, RoutedEventArgs e)
        {
            string name = TxtName.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                MessageBox.Show(this, "请输入设备名称。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (RadCamera.IsChecked == true)
            {
                Result = new CameraDevice
                {
                    Name = name,
                    Vendor = TxtVendor.Text.Trim(),
                    Model = TxtModel.Text.Trim(),
                    Ip = TxtIp.Text.Trim()
                };
            }
            else
            {
                Result = new LightSourceDevice
                {
                    Name = name,
                    Vendor = TxtVendor.Text.Trim(),
                    Model = TxtModel.Text.Trim()
                };
            }

            DialogResult = true;
        }
    }
}
