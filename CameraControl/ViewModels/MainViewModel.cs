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
        public LogViewModel LogVM { get; } = App.LogVM;   // 引用全局单例
        IDevice? device =null;

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
            //枚举设备，获取设备信息
            List<IDeviceInfo> devInfoList = null;
            int ret = DeviceEnumerator.EnumDevicesEx(
                 DeviceTLayerType.MvGigEDevice | DeviceTLayerType.MvUsbDevice,"", out devInfoList);
            if (ret == MvError.MV_OK)
            {
                /*处理XXX厂商的相机*/
                App.LogVM.AddLog($"枚举设备成功，设备数量: {devInfoList.Count}");
                foreach (var dev in devInfoList)
                    App.LogVM.AddLog($"型号: {dev.ModelName}, 厂商: {dev.ManufacturerName}, 序列号: {dev.SerialNumber}");
            }

            string deviceIp = "169.254.217.149"; //相机IP
            string netExportIP = "169.254.50.1"; //pc网卡IP
            

            //创建相机对象
            try
            {
                device = DeviceFactory.CreateDeviceByIp(deviceIp, netExportIP);//
            }
            catch (Exception e)
            {
                App.LogVM.AddLog($"创建相机对象失败: {e.Message}");
                throw;
            }

            int retOpen = device.Open();
            if (ret != MvError.MV_OK)
            {
                App.LogVM.AddLog($"打开相机失败: {retOpen}");
            }
            else
            {
                App.LogVM.AddLog($"打开相机成功: {deviceIp}");
                //TODO：配置相机参数等操作
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
