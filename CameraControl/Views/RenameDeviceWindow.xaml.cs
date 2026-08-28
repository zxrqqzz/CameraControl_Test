using CameraControl.Models;
using System.Windows;

namespace CameraControl.Views
{
    public partial class RenameDeviceWindow : Window
    {
        private readonly DeviceBase _device;

        public RenameDeviceWindow(DeviceBase device)
        {
            InitializeComponent();
            _device = device;
            TxtName.Text = device.Name;
            TxtName.Focus();
            TxtName.SelectAll();
        }

        private void OnOkClick(object sender, RoutedEventArgs e)
        {
            string name = TxtName.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                MessageBox.Show(this, "名称不能为空。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _device.Name = name;
            DialogResult = true;
        }
    }
}
