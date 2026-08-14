using CameraControl.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace CameraControl.Services
{
    /// <summary>
    /// 通讯管理服务(类似海康VM通讯管理)
    /// 维护设备列表，转发设备接收到的数据，并向指定设备/来源回传数据。
    /// </summary>
    public class CommunicationService : IDisposable
    {
        private readonly object _sync = new object();
        private int _nextId = 1;
        private bool _restoring;

        /// <summary>设备列表</summary>
        public ObservableCollection<CommunicationDevice> Devices { get; } = new ObservableCollection<CommunicationDevice>();

        /// <summary>设备收到数据事件(device, data, channel)</summary>
        public event Action<CommunicationDevice, string, object?>? DataReceived;

        /// <summary>注册设备(加入列表并订阅数据)</summary>
        public void Register(CommunicationDevice device)
        {
            lock (_sync)
            {
                device.Id = _nextId++;
                device.DataReceived += OnDeviceDataReceived;
                Devices.Add(device);
            }
            App.LogVM.AddLog($"已添加设备: {device.Name} ({device.TypeText} {device.Ip}:{device.Port})");
            TrySave();
        }

        /// <summary>移除设备</summary>
        public void Remove(CommunicationDevice device)
        {
            lock (_sync)
            {
                if (!Devices.Contains(device)) return;
                device.DataReceived -= OnDeviceDataReceived;
                try { device.Disconnect(); } catch { }
                Devices.Remove(device);
            }
            App.LogVM.AddLog($"已删除设备: {device.Name}");
            TrySave();
        }

        /// <summary>启动时从保存的设置恢复设备列表</summary>
        public void RestoreFromSettings(IEnumerable<DeviceSettings> devices)
        {
            _restoring = true;
            try
            {
                foreach (var ds in devices)
                {
                    if (string.IsNullOrWhiteSpace(ds.Name)) continue;
                    CommunicationDevice device = ds.Type == CommunicationType.TcpServer
                        ? new TcpServerDevice { Name = ds.Name, Ip = ds.Ip, Port = ds.Port }
                        : new TcpClientDevice { Name = ds.Name, Ip = ds.Ip, Port = ds.Port };
                    Register(device);
                }
            }
            finally { _restoring = false; }
        }

        /// <summary>设备或触发配置变化时自动保存</summary>
        private void TrySave()
        {
            if (!_restoring) App.Settings?.SaveAll();
        }

        /// <summary>向指定设备发送数据(channel为接收来源，实现"从哪个设备传入就从哪个设备传出")</summary>
        public void Send(CommunicationDevice device, string text, object? channel = null)
        {
            device.Send(text, channel);
        }

        private void OnDeviceDataReceived(CommunicationDevice device, string data, object? channel)
        {
            App.LogVM.AddLog($"[通讯] 设备{device.Name} 收到: {data}");
            DataReceived?.Invoke(device, data, channel);
        }

        /// <summary>释放所有设备资源(保留列表以便退出时保存设置)</summary>
        public void Dispose()
        {
            lock (_sync)
            {
                foreach (var d in Devices.ToArray())
                {
                    d.DataReceived -= OnDeviceDataReceived;
                    try { d.Dispose(); } catch { }
                }
            }
        }
    }
}