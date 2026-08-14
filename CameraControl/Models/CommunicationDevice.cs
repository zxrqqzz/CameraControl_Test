using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CameraControl.Models
{
    /// <summary>通讯设备类型</summary>
    public enum CommunicationType
    {
        TcpClient,
        TcpServer
    }

    /// <summary>
    /// 通讯设备基类(类似海康VM通讯管理)
    /// 数据接收后触发 DataReceived，发送时带接收来源(channel)，实现"从哪个设备传入就从哪个设备传出"。
    /// </summary>
    public abstract class CommunicationDevice : INotifyPropertyChanged, IDisposable
    {
        /// <summary>设备序号</summary>
        public int Id { get; set; }

        /// <summary>设备名称</summary>
        public string Name { get; set; } = "";

        /// <summary>设备类型</summary>
        public abstract CommunicationType Type { get; }

        /// <summary>远端/监听 IP</summary>
        public string Ip { get; set; } = "";

        /// <summary>端口</summary>
        public int Port { get; set; }

        /// <summary>是否已连接/监听</summary>
        public abstract bool IsConnected { get; }

        /// <summary>状态文本</summary>
        public abstract string StatusText { get; }

        /// <summary>类型文本</summary>
        public string TypeText => Type == CommunicationType.TcpClient ? "TCP客户端" : "TCP服务端";

        /// <summary>列表显示文本</summary>
        public string Display => $"[{Id}] {Name}  ({TypeText})  {Ip}:{Port}  {StatusText}";

        /// <summary>收到数据事件(device, data, channel)</summary>
        public event Action<CommunicationDevice, string, object?>? DataReceived;

        protected void RaiseDataReceived(string data, object? channel) =>
            DataReceived?.Invoke(this, data, channel);

        /// <summary>建立连接/开始监听</summary>
        public abstract void Connect();

        /// <summary>断开连接/停止监听</summary>
        public abstract void Disconnect();

        /// <summary>发送文本(默认发往接收来源channel，未指定则发往全部/唯一连接)</summary>
        public abstract void Send(string text, object? channel = null);

        /// <summary>释放资源</summary>
        public abstract void Dispose();

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>TCP客户端设备(主动连接远端服务端)</summary>
    public class TcpClientDevice : CommunicationDevice
    {
        private TcpClient? _client;
        private NetworkStream? _stream;
        private CancellationTokenSource? _cts;
        private bool _connected;

        public override CommunicationType Type => CommunicationType.TcpClient;
        public override bool IsConnected => _connected;
        public override string StatusText => _connected ? "已连接" : "未连接";

        public override void Connect()
        {
            Disconnect();
            try
            {
                _client = new TcpClient();
                _client.Connect(Ip, Port);
                _stream = _client.GetStream();
                _connected = true;
                _cts = new CancellationTokenSource();
                _ = ReceiveLoopAsync(_cts.Token);
                App.LogVM.AddLog($"[通讯] TCP客户端 {Name} 已连接 {Ip}:{Port}");
            }
            catch (Exception e)
            {
                Disconnect();
                App.LogVM.AddLog($"[通讯] TCP客户端 {Name} 连接失败: {e.Message}");
                throw;
            }
            NotifyStatus();
        }

        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            var buffer = new byte[4096];
            try
            {
                while (!token.IsCancellationRequested)
                {
                    int n = await _stream!.ReadAsync(buffer.AsMemory(0, buffer.Length), token);
                    if (n <= 0) break;
                    string text = Encoding.UTF8.GetString(buffer, 0, n);
                    RaiseDataReceived(text, null);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                App.LogVM.AddLog($"[通讯] TCP客户端 {Name} 接收异常: {e.Message}");
            }
            finally
            {
                _connected = false;
                NotifyStatus();
            }
        }

        public override void Send(string text, object? channel = null)
        {
            if (_stream == null || !_connected)
                throw new InvalidOperationException($"{Name} 未连接，无法发送");
            byte[] data = Encoding.UTF8.GetBytes(text);
            _stream.Write(data, 0, data.Length);
            _stream.Flush();
            App.LogVM.AddLog($"[通讯] TCP客户端 {Name} 发送: {text}");
        }

        public override void Disconnect()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            try { _stream?.Dispose(); } catch { }
            try { _client?.Close(); } catch { }
            _stream = null;
            _client = null;
            _connected = false;
            NotifyStatus();
        }

        public override void Dispose() => Disconnect();

        private void NotifyStatus()
        {
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(Display));
        }
    }

    /// <summary>TCP服务端设备(监听端口，支持多客户端，按来源回传)</summary>
    public class TcpServerDevice : CommunicationDevice
    {
        private TcpListener? _listener;
        private readonly List<TcpClient> _clients = new List<TcpClient>();
        private CancellationTokenSource? _cts;
        private bool _listening;

        public override CommunicationType Type => CommunicationType.TcpServer;
        public override bool IsConnected => _listening;
        public override string StatusText => _listening ? $"监听中({ClientCount}客户端)" : "未监听";

        private int ClientCount
        {
            get { lock (_clients) return _clients.Count; }
        }

        public override void Connect()
        {
            Disconnect();
            try
            {
                _cts = new CancellationTokenSource();
                _listener = new TcpListener(IPAddress.Any, Port);
                _listener.Start();
                _listening = true;
                _ = AcceptLoopAsync(_cts.Token);
                App.LogVM.AddLog($"[通讯] TCP服务端 {Name} 开始监听端口 {Port}");
            }
            catch (Exception e)
            {
                Disconnect();
                App.LogVM.AddLog($"[通讯] TCP服务端 {Name} 启动失败: {e.Message}");
                throw;
            }
            NotifyStatus();
        }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var client = await _listener!.AcceptTcpClientAsync(token);
                    lock (_clients) _clients.Add(client);
                    NotifyStatus();
                    _ = ClientLoopAsync(client, token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                App.LogVM.AddLog($"[通讯] TCP服务端 {Name} 监听异常: {e.Message}");
            }
        }

        private async Task ClientLoopAsync(TcpClient client, CancellationToken token)
        {
            var stream = client.GetStream();
            var buffer = new byte[4096];
            try
            {
                while (!token.IsCancellationRequested)
                {
                    int n = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token);
                    if (n <= 0) break;
                    string text = Encoding.UTF8.GetString(buffer, 0, n);
                    RaiseDataReceived(text, client);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                App.LogVM.AddLog($"[通讯] TCP服务端 {Name} 客户端接收异常: {e.Message}");
            }
            finally
            {
                lock (_clients) _clients.Remove(client);
                try { client.Dispose(); } catch { }
                NotifyStatus();
            }
        }

        public override void Send(string text, object? channel = null)
        {
            if (!_listening) throw new InvalidOperationException($"{Name} 未监听，无法发送");
            byte[] data = Encoding.UTF8.GetBytes(text);

            if (channel is TcpClient target)
            {
                try
                {
                    var s = target.GetStream();
                    s.Write(data, 0, data.Length);
                    s.Flush();
                }
                catch (Exception e)
                {
                    App.LogVM.AddLog($"[通讯] TCP服务端 {Name} 发送失败: {e.Message}");
                    throw;
                }
            }
            else
            {
                lock (_clients)
                {
                    foreach (var c in _clients.ToArray())
                    {
                        try
                        {
                            var s = c.GetStream();
                            s.Write(data, 0, data.Length);
                            s.Flush();
                        }
                        catch { }
                    }
                }
            }
            App.LogVM.AddLog($"[通讯] TCP服务端 {Name} 发送: {text}");
        }

        public override void Disconnect()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            try { _listener?.Stop(); } catch { }
            _listener = null;
            lock (_clients)
            {
                foreach (var c in _clients.ToArray())
                {
                    try { c.Dispose(); } catch { }
                }
                _clients.Clear();
            }
            _listening = false;
            NotifyStatus();
        }

        public override void Dispose() => Disconnect();

        private void NotifyStatus()
        {
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(Display));
        }
    }
}