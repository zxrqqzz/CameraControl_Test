using CameraControl.Services;
using CameraControl.ViewModels;
using System.Windows;

namespace CameraControl.Views
{
    public partial class CameraParameterWindow : Window
    {
        private readonly CameraParameterViewModel _vm;

        public CameraParameterWindow(HikCameraService cameraService)
        {
            InitializeComponent();
            _vm = new CameraParameterViewModel(new CameraParameterService(() => cameraService.Parameters));
            DataContext = _vm;
            Loaded += (_, _) => _vm.LoadParameters();
        }
    }
}
