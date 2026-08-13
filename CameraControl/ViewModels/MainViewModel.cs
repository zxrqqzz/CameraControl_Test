using GalaSoft.MvvmLight.Command;
using MvCameraControl;
using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Input;

namespace CameraControl.ViewModels
{
    internal class MainViewModel 
    {
        /// <summary>
        /// 日志存储
        /// </summary>
        public LogViewModel LogVM { get; } = App.LogVM;

        // 相机对象
        IDevice? _camera = null;
        private IntPtr _displayHandle;   // PictureBox 的句柄

        // 命令绑定
        public ICommand SingleCaptureCommand { get; }
        public ICommand ContinuousCaptureCommand { get; }

        public MainViewModel()
        {
            SDKSystem.Initialize();//初始化

            SingleCaptureCommand = new RelayCommand(SingleCapture);
            ContinuousCaptureCommand = new RelayCommand(ContinuousCapture);

            DeviceCamera();

        }




        /// <summary>
        /// 相机设备枚举和创建
        /// </summary>
        public void DeviceCamera()
        {
            string deviceIp = "192.168.1.10"; //相机IP
            string netExportIP = "192.168.1.100"; //pc网卡IP

            //枚举设备，获取设备信息
            List<IDeviceInfo> devInfoList = null;
            int ret = DeviceEnumerator.EnumDevicesEx(
                 DeviceTLayerType.MvGigEDevice | DeviceTLayerType.MvUsbDevice, "", out devInfoList);
            if (ret == MvError.MV_OK)
            {
                /*处理XXX厂商的相机*/
                App.LogVM.AddLog($"枚举设备成功，设备数量: {devInfoList.Count}");
                foreach (var dev in devInfoList)
                    App.LogVM.AddLog($"型号: {dev.ModelName}, 厂商: {dev.ManufacturerName}, 序列号: {dev.SerialNumber}");
            }

            //创建相机对象
            try
            {
                _camera = DeviceFactory.CreateDeviceByIp(deviceIp, netExportIP);//

                // 3. 打开设备
                int retOpen = _camera.Open();
                if (retOpen != MvError.MV_OK)//打开相机失败
                {
                    LogVM.AddLog($"打开相机失败: {ret:X}");
                    _camera.Dispose();
                    _camera = null;
                    return;
                }

                LogVM.AddLog($"相机 {_camera} 打开成功");
            }
            catch (Exception e)
            {
                App.LogVM.AddLog($"打开相机失败: {e.Message}");
                throw;
            }
        }



        /// <summary>
        /// 单次采集
        /// </summary>
        private void SingleCapture()
        {
            

        }
        /// <summary>
        /// 连续采集
        /// </summary>
        private void ContinuousCapture()
        {

        }

        

        /// <summary>
        /// 关闭SDK，释放资源
        /// </summary>
        public void Dispose()
        {
            SDKSystem.Finalize();
        }

    }
}
