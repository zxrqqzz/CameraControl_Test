using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
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

    /// <summary>
    /// TCP客户端设备(主动连接远端服务端)
    /// 支持自动连接/自动重连与心跳检测：Connect后持续保持连接，
    /// 仅通过手动 Disconnect 才真正彻底断开。
    /// </summary>
    public class TcpClientDevice : CommunicationDevice
    {
        private readonly object _lock = new object();
        private TcpClient? _client;
        private NetworkStream? _stream;
        private CancellationTokenSource? _cts;
        private Task? _runTask;
        private volatile bool _connected;
        private volatile bool _autoConnect;

        /// <summary>自动重连间隔(毫秒)</summary>
        public int RetryIntervalMs { get; set; } = 3000;

        /// <summary>是否启用心跳检测</summary>
        public bool EnableHeartbeat { get; set; } = true;

        /// <summary>心跳发送间隔(毫秒)</summary>
        public int HeartbeatIntervalMs { get; set; } = 5000;

        /// <summary>心跳超时(毫秒)，超过该时间未收到任何数据视为掉线并自动重连</summary>
        public int HeartbeatTimeoutMs { get; set; } = 30000;

        /// <summary>心跳内容(发送给服务端)</summary>
        public string HeartbeatData { get; set; } = "PING";

        public override CommunicationType Type => CommunicationType.TcpClient;
        public override bool IsConnected => _connected;
        public override string StatusText => _connected ? "已连接" : (_autoConnect ? "连接中(自动重连)" : "未连接");

        /// <summary>
        /// 开始自动连接：仅负责连接，若已连接则不做任何操作(不会断开)。
        /// 连接失败或意外断开后会自动重连，直到手动调用 Disconnect。
        /// </summary>
        public override void Connect()
        {
            lock (_lock)
            {
                if (_connected)
                {
                    App.LogVM.AddLog($"[通讯] TCP客户端 {Name} 已连接，无需重复连接");
                    return;
                }
                _autoConnect = true;
                if (_runTask == null || _runTask.IsCompleted || _cts == null || _cts.IsCancellationRequested)
                {
                    _cts?.Dispose();
                    _cts = new CancellationTokenSource();
                    _runTask = RunLoopAsync(_cts.Token);
                }
            }
            NotifyStatus();
        }

        /// <summary>手动彻底断开连接(同时停止自动重连)</summary>
        public override void Disconnect()
        {
            lock (_lock)
            {
                _autoConnect = false;
                _cts?.Cancel();
            }
            CloseSocket();
            NotifyStatus();
        }

        /// <summary>自动连接/重连主循环，直到手动断开</summary>
        private async Task RunLoopAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (!_connected)
                        await TryConnectAsync(token);

                    if (_connected)
                    {
                        await ReceiveAndHeartbeatLoopAsync(token);
                        if (token.IsCancellationRequested) break;
                        App.LogVM.AddLog($"[通讯] TCP客户端 {Name} 连接断开，{RetryIntervalMs / 1000.0:F0}秒后自动重连...");
                    }

                    if (token.IsCancellationRequested) break;
                    try { await Task.Delay(RetryIntervalMs, token); }
                    catch (OperationCanceledException) { break; }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                App.LogVM.AddLog($"[通讯] TCP客户端 {Name} 自动连接循环异常: {e.Message}");
            }
            finally
            {
                CloseSocket();
                lock (_lock)
                {
                    // 仅清理本循环自己创建的 CTS，避免误关新启动的循环
                    if (_cts != null && _cts.Token == token)
                    {
                        _cts.Dispose();
                        _cts = null;
                        _runTask = null;
                    }
                }
            }
        }

        private async Task TryConnectAsync(CancellationToken token)
        {
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(Ip, Port, token);
            }
            catch (OperationCanceledException)
            {
                try { client.Dispose(); } catch { }
                throw;
            }
            catch (Exception e)
            {
                try { client.Dispose(); } catch { }
                App.LogVM.AddLog($"[通讯] TCP客户端 {Name} 连接失败: {e.Message}");
                return;
            }

            lock (_lock)
            {
                _client = client;
                _stream = client.GetStream();
                _connected = true;
            }
            App.LogVM.AddLog($"[通讯] TCP客户端 {Name} 已连接 {Ip}:{Port}");
            NotifyStatus();
        }

        /// <summary>接收数据循环，同时按间隔发送心跳并检测连接是否存活</summary>
        private async Task ReceiveAndHeartbeatLoopAsync(CancellationToken token)
        {
            var buffer = new byte[4096];
            bool sendHeartbeat = EnableHeartbeat && !string.IsNullOrEmpty(HeartbeatData);
            DateTime lastReceived = DateTime.UtcNow;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    var stream = _stream;
                    if (stream == null) return;

                    var readTask = stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token).AsTask();
                    var delayTask = Task.Delay(HeartbeatIntervalMs, token);

                    Task completed = await Task.WhenAny(readTask, delayTask);
                    if (completed == delayTask)
                    {
                        if (sendHeartbeat)
                        {
                            // 超过心跳超时仍未收到任何数据，判定连接已失效
                            if ((DateTime.UtcNow - lastReceived).TotalMilliseconds >= HeartbeatTimeoutMs)
                                throw new IOException("心跳超时(长时间未收到数据)，判定连接已断开");
                            try { await SendHeartbeatAsync(token); }
                            catch (Exception e) { throw new IOException("心跳发送失败: " + e.Message); }
                        }
                        continue;
                    }

                    int n = await readTask;
                    if (n <= 0) throw new IOException("远端已断开连接");
                    lastReceived = DateTime.UtcNow;
                    string text = Encoding.UTF8.GetString(buffer, 0, n);
                    RaiseDataReceived(text, null);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                App.LogVM.AddLog($"[通讯] TCP客户端 {Name} 连接断开: {e.Message}");
            }
            finally
            {
                CloseSocket();
            }
        }

        /// <summary>发送心跳(不写日志，避免刷屏)</summary>
        private async Task SendHeartbeatAsync(CancellationToken token)
        {
            var stream = _stream;
            if (stream == null || !_connected)
                throw new InvalidOperationException($"{Name} 未连接，无法发送心跳");
            byte[] data = Encoding.UTF8.GetBytes(HeartbeatData);
            await stream.WriteAsync(data.AsMemory(), token);
            await stream.FlushAsync(token);
        }

        public override void Send(string text, object? channel = null)
        {
            var stream = _stream;
            if (stream == null || !_connected)
                throw new InvalidOperationException($"{Name} 未连接，无法发送");
            byte[] data = Encoding.UTF8.GetBytes(text);
            stream.Write(data, 0, data.Length);
            stream.Flush();
            App.LogVM.AddLog($"[通讯] TCP客户端 {Name} 发送: {text}");
        }

        private void CloseSocket()
        {
            TcpClient? client;
            lock (_lock)
            {
                client = _client;
                _client = null;
                _stream = null;
                _connected = false;
            }
            try { client?.Close(); } catch { }
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
            if (_listening)
            {
                App.LogVM.AddLog($"[通讯] TCP服务端 {Name} 已在监听端口 {Port}");
                return;
            }
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