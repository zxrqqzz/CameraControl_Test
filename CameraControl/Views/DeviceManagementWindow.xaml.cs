using CameraControl.Models;
using CameraControl.ViewModels;
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
            Closed += (_, _) => App.DeviceManagement.RenameRequested -= OnRenameRequested;
        }

        private void OnAddDeviceClick(object sender, RoutedEventArgs e)
        {
            var dlg = new AddDeviceWindow { Owner = this };
            if (dlg.ShowDialog() == true && dlg.Result != null)
            {
                App.DeviceManagement.AddDevice(dlg.Result);
            }
        }

        private void OnRenameRequested(DeviceBase device)
        {
            var dlg = new RenameDeviceWindow(device) { Owner = this };
            dlg.ShowDialog();
        }
    }
}
