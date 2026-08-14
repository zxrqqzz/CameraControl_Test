using CameraControl.Models;
using CameraControl.Services;
using GalaSoft.MvvmLight.Command;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace CameraControl.ViewModels
{
    /// <summary>通讯管理 ViewModel(添加/删除/连接/断开 TCP客户端、TCP服务端设备)</summary>
    public class CommunicationViewModel : INotifyPropertyChanged
    {
        private readonly CommunicationService _service;
        private bool _isTcpClient = true;
        private string _newName = "TCP设备";
        private string _newIp = "192.168.1.50";
        private string _newPortText = "6000";

        /// <summary>设备列表</summary>
        public ObservableCollection<CommunicationDevice> Devices => _service.Devices;

        public bool IsTcpClient
        {
            get => _isTcpClient;
            set { _isTcpClient = value; OnPropertyChanged(); }
        }

        public bool IsTcpServer
        {
            get => !_isTcpClient;
            set { _isTcpClient = !value; OnPropertyChanged(); OnPropertyChanged(nameof(IsTcpClient)); }
        }

        public string NewName
        {
            get => _newName;
            set { _newName = value; OnPropertyChanged(); }
        }

        public string NewIp
        {
            get => _newIp;
            set { _newIp = value; OnPropertyChanged(); }
        }

        public string NewPortText
        {
            get => _newPortText;
            set { _newPortText = value; OnPropertyChanged(); }
        }

        public ICommand AddDeviceCommand { get; }
        public ICommand RemoveDeviceCommand { get; }
        public ICommand ConnectCommand { get; }
        public ICommand DisconnectCommand { get; }

        public CommunicationViewModel(CommunicationService service)
        {
            _service = service;
            AddDeviceCommand = new RelayCommand(AddDevice);
            RemoveDeviceCommand = new RelayCommand<CommunicationDevice>(RemoveDevice);
            ConnectCommand = new RelayCommand<CommunicationDevice>(Connect);
            DisconnectCommand = new RelayCommand<CommunicationDevice>(Disconnect);
        }

        private void AddDevice()
        {
            if (!int.TryParse(NewPortText, out int port) || port < 1 || port > 65535)
            {
                App.LogVM.AddLog("端口无效，请输入1~65535之间的数字。");
                return;
            }

            string name = string.IsNullOrWhiteSpace(NewName) ? (IsTcpClient ? "TCP客户端" : "TCP服务端") : NewName;

            CommunicationDevice device = IsTcpClient
                ? new TcpClientDevice { Name = name, Ip = NewIp, Port = port }
                : new TcpServerDevice { Name = name, Ip = "0.0.0.0", Port = port };

            _service.Register(device);
        }

        private void RemoveDevice(CommunicationDevice device) => _service.Remove(device);

        private void Connect(CommunicationDevice device)
        {
            try { device.Connect(); }
            catch (Exception e) { App.LogVM.AddLog("连接失败: " + e.Message); }
        }

        private void Disconnect(CommunicationDevice device)
        {
            try { device.Disconnect(); }
            catch (Exception e) { App.LogVM.AddLog("断开异常: " + e.Message); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}