using CameraControl.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace CameraControl.Services
{
    /// <summary>
    /// 局域网相机的"只检测、不接触"扫描。
    ///
    /// 设计原则(按使用要求):
    /// 1. <b>被动模式(默认)</b> — 只读本机 ARP/邻居表并用厂商 OUI 判断，<b>不发送任何网络报文</b>，
    ///    完全无痕，不会让别人的相机察觉被扫描。
    /// 2. <b>主动模式(需显式勾选)</b> — 发一条标准 ONVIF WS-Discovery Probe 组播，
    ///    只读取设备自己广播回来的厂商/型号，<b>不做认证、不登录、不请求 RTSP、不取图像</b>。
    /// 3. 无论哪种模式，结果都标记为"仅检测"，本程序不会去连接这些设备。
    /// </summary>
    public static class LanDeviceScanner
    {
        /// <summary>ONVIF / WS-Discovery 组播地址与端口</summary>
        private const string OnvifMulticastAddress = "239.255.255.250";
        private const int OnvifPort = 3702;

        /// <summary>
        /// 已知相机/机器视觉厂商的 OUI(MAC 前三字节)。
        /// 这是启发式判断，不是权威数据库；新增厂商直接往这里加即可。
        /// </summary>
        private static readonly Dictionary<string, string> CameraOuis =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // 海康威视 Hikvision
                ["4C:BD:8F"] = "Hikvision",
                ["28:57:67"] = "Hikvision",
                ["44:19:B6"] = "Hikvision",
                ["BC:AD:28"] = "Hikvision",
                ["C0:56:E3"] = "Hikvision",
                // 大华 Dahua
                ["3C:EF:8C"] = "Dahua",
                ["90:02:A9"] = "Dahua",
                ["E0:50:8B"] = "Dahua",
                // 安讯士 Axis
                ["AC:CC:8E"] = "Axis",
                ["00:40:8C"] = "Axis"
            };

        // ────────────────────────────────────────────────────────────────
        // 被动模式: ARP 邻居表
        // ────────────────────────────────────────────────────────────────

        [StructLayout(LayoutKind.Sequential)]
        private struct MibIpNetRow
        {
            public uint Index;
            public uint PhysAddrLen;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public byte[] PhysAddr;
            public uint Addr;
            public uint Type;
        }

        private const int ErrorInsufficientBuffer = 122;

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern int GetIpNetTable(IntPtr pIpNetTable, ref uint pdwSize, bool bOrder);

        /// <summary>
        /// 被动扫描: 读本机 ARP 邻居表，挑出 MAC 命中相机厂商 OUI 的条目。
        /// 整个过程只读本机内核表，不碰网络。
        /// </summary>
        public static List<ScanCandidate> ScanPassive(
            out int arpEntryCount, out List<string> localSubnets, Action<string>? log = null)
        {
            var results = new List<ScanCandidate>();
            localSubnets = GetLocalSubnets();
            arpEntryCount = 0;

            List<(string Ip, string Mac)> entries = ReadArpTable(out string? error);
            if (error != null) log?.Invoke(error);

            arpEntryCount = entries.Count;
            log?.Invoke($"本机 ARP 邻居表共 {arpEntryCount} 条，本机网段: " +
                        (localSubnets.Count > 0 ? string.Join("、", localSubnets) : "(未识别)"));

            foreach ((string ip, string mac) in entries)
            {
                string oui = OuiOf(mac);
                if (oui.Length == 0 || !CameraOuis.TryGetValue(oui, out string? vendor)) continue;

                results.Add(new ScanCandidate
                {
                    Name = $"疑似{vendor}相机",
                    Vendor = vendor,
                    Model = string.Empty,
                    Ip = ip,
                    Mac = mac,
                    DevicePath = mac,
                    InterfaceText = "局域网 (ARP)",
                    Source = DeviceSource.LanPassive
                });
            }

            if (results.Count == 0)
                log?.Invoke("ARP 表中没有命中相机厂商 OUI 的设备。");

            return results;
        }

        /// <summary>读取本机 ARP/邻居表。失败时返回空表并通过 error 说明原因。</summary>
        private static List<(string Ip, string Mac)> ReadArpTable(out string? error)
        {
            error = null;
            var rows = new List<(string, string)>();

            IntPtr buffer = IntPtr.Zero;
            try
            {
                uint size = 0;
                int ret = GetIpNetTable(IntPtr.Zero, ref size, true);
                if (ret != ErrorInsufficientBuffer)
                {
                    if (ret != 0) error = $"读取 ARP 表失败(错误码 {ret})";
                    return rows;
                }

                buffer = Marshal.AllocHGlobal((int)size);
                ret = GetIpNetTable(buffer, ref size, true);
                if (ret != 0)
                {
                    error = $"读取 ARP 表失败(错误码 {ret})";
                    return rows;
                }

                int count = Marshal.ReadInt32(buffer);
                int rowSize = Marshal.SizeOf(typeof(MibIpNetRow));
                for (int i = 0; i < count; i++)
                {
                    IntPtr rowPtr = IntPtr.Add(buffer, 4 + i * rowSize);
                    var row = (MibIpNetRow)Marshal.PtrToStructure(rowPtr, typeof(MibIpNetRow))!;

                    // Type: 1=other 2=invalid 3=dynamic 4=static。只保留有效的
                    if (row.Type == 2) continue;
                    if (row.PhysAddr == null || row.PhysAddr.Length < 6) continue;
                    if (row.PhysAddrLen < 6) continue;

                    string mac = FormatMac(row.PhysAddr);
                    if (IsMulticastOrBroadcast(mac)) continue;

                    string ip = UIntToIp(row.Addr);
                    if (IsUnusableAddress(ip)) continue;

                    rows.Add((ip, mac));
                }
            }
            catch (Exception e)
            {
                error = "读取 ARP 表异常: " + e.Message;
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            }

            return rows;
        }

        // ────────────────────────────────────────────────────────────────
        // 主动模式: ONVIF WS-Discovery
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// ONVIF WS-Discovery 发现。只发送标准 Probe 组播并解析设备回传的厂商/型号/服务地址，
        /// 不做任何认证、不访问 XAddrs、不请求媒体流。
        /// </summary>
        public static async Task<List<ScanCandidate>> ScanOnvifAsync(
            int timeoutMs, CancellationToken cancellationToken, Action<string>? log = null)
        {
            var results = new List<ScanCandidate>();
            var seenEndpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string probe = BuildProbeMessage();
            byte[] payload = Encoding.UTF8.GetBytes(probe);

            using var client = new UdpClient(AddressFamily.InterNetwork);
            // 允许与其它 ONVIF 客户端共用本机端口
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            client.EnableBroadcast = true;
            client.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

            var group = IPEndPoint.Parse($"{OnvifMulticastAddress}:{OnvifPort}");
            try
            {
                client.JoinMulticastGroup(IPAddress.Parse(OnvifMulticastAddress));
            }
            catch (Exception e)
            {
                log?.Invoke("加入 ONVIF 组播组失败(可能被防火墙拦截): " + e.Message);
            }

            try
            {
                await client.SendAsync(payload, payload.Length, group).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                log?.Invoke("发送 ONVIF 发现报文失败: " + e.Message);
                return results;
            }

            log?.Invoke($"已发送 ONVIF 发现报文，等待 {timeoutMs} ms 回包…");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeoutMs);

            while (!timeoutCts.IsCancellationRequested)
            {
                try
                {
                    UdpReceiveResult received = await client.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false);
                    ScanCandidate? candidate = ParseProbeMatch(received.Buffer);
                    if (candidate == null) continue;

                    string endpointKey = candidate.Mac + "|" + candidate.Ip + "|" + candidate.Name;
                    if (!seenEndpoints.Add(endpointKey)) continue;

                    results.Add(candidate);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException)
                {
                    // ReceiveTimeout 到期，继续等到总超时
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception e)
                {
                    log?.Invoke("解析 ONVIF 回包异常: " + e.Message);
                }
            }

            log?.Invoke($"ONVIF 发现完成，共 {results.Count} 个响应。");
            return results;
        }

        private static string BuildProbeMessage()
        {
            string messageId = "uuid:" + Guid.NewGuid().ToString("D");
            return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<e:Envelope xmlns:e=""http://www.w3.org/2003/05/soap-envelope""
            xmlns:w=""http://schemas.xmlsoap.org/ws/2004/08/addressing""
            xmlns:d=""http://schemas.xmlsoap.org/ws/2005/04/discovery""
            xmlns:dn=""http://www.onvif.org/ver10/network/wsdl"">
  <e:Header>
    <w:MessageID>{messageId}</w:MessageID>
    <w:To e:mustUnderstand=""true"">urn:schemas-xmlsoap-org:ws:2005:04:discovery</w:To>
    <w:Action e:mustUnderstand=""true"">http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</w:Action>
  </e:Header>
  <e:Body>
    <d:Probe><d:Types>dn:NetworkVideoTransmitter</d:Types></d:Probe>
  </e:Body>
</e:Envelope>";
        }

        /// <summary>解析 ProbeMatch 回包。解析不出来就返回 null，绝不影响其它回包。</summary>
        private static ScanCandidate? ParseProbeMatch(byte[] buffer)
        {
            try
            {
                string text = Encoding.UTF8.GetString(buffer);
                XDocument doc = XDocument.Parse(text);

                XNamespace s = "http://www.w3.org/2003/05/soap-envelope";
                XNamespace d = "http://schemas.xmlsoap.org/ws/2005/04/discovery";

                XElement? match = doc.Descendants(d + "ProbeMatch").FirstOrDefault();
                if (match == null) return null;

                string scopes = (string?)match.Element(d + "Scopes") ?? string.Empty;
                string xAddrs = (string?)match.Element(d + "XAddrs") ?? string.Empty;

                string name = ExtractScope(scopes, "name") ?? string.Empty;
                string hardware = ExtractScope(scopes, "hardware") ?? string.Empty;
                string manufacturer = ExtractScope(scopes, "manufacturer") ?? string.Empty;
                string ip = ExtractHost(xAddrs);

                if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(ip)) return null;

                return new ScanCandidate
                {
                    Name = !string.IsNullOrWhiteSpace(name)
                        ? name
                        : !string.IsNullOrWhiteSpace(ip) ? ip : "ONVIF 相机",
                    Vendor = !string.IsNullOrWhiteSpace(manufacturer) ? manufacturer : string.Empty,
                    Model = hardware,
                    Ip = ip,
                    DevicePath = ip,
                    InterfaceText = "ONVIF (局域网)",
                    Source = DeviceSource.LanOnvif
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>从 scopes 串里取 onvif://www.onvif.org/xxx/yyy 中的 yyy</summary>
        private static string? ExtractScope(string scopes, string key)
        {
            if (string.IsNullOrWhiteSpace(scopes)) return null;
            foreach (string scope in scopes.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string marker = "/" + key + "/";
                int index = scope.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (index < 0) continue;
                string value = scope.Substring(index + marker.Length).Trim('/');
                return string.IsNullOrWhiteSpace(value) ? null : Uri.UnescapeDataString(value);
            }
            return null;
        }

        /// <summary>从 XAddrs(空格分隔的服务地址)里取第一个主机名/IP</summary>
        private static string ExtractHost(string xAddrs)
        {
            if (string.IsNullOrWhiteSpace(xAddrs)) return string.Empty;
            foreach (string addr in xAddrs.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!Uri.TryCreate(addr, UriKind.Absolute, out Uri? uri)) continue;
                if (IPAddress.TryParse(uri.Host, out IPAddress? ipAddr)) return ipAddr.ToString();
                return uri.Host;
            }
            return string.Empty;
        }

        // ────────────────────────────────────────────────────────────────
        // 工具方法与本机网段识别
        // ────────────────────────────────────────────────────────────────

        private static string OuiOf(string mac)
        {
            if (string.IsNullOrWhiteSpace(mac) || mac.Length < 8) return string.Empty;
            return mac.Substring(0, 8).Replace('-', ':').ToUpperInvariant();
        }

        private static string FormatMac(byte[] bytes)
        {
            var sb = new StringBuilder(17);
            for (int i = 0; i < 6; i++)
            {
                if (i > 0) sb.Append(':');
                sb.Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static string UIntToIp(uint value) => $"{(value >> 24) & 0xFF}.{(value >> 16) & 0xFF}.{(value >> 8) & 0xFF}.{value & 0xFF}";

        private static bool IsMulticastOrBroadcast(string mac) =>
            mac.StartsWith("01:", StringComparison.OrdinalIgnoreCase)
            || mac.StartsWith("FF:", StringComparison.OrdinalIgnoreCase)
            || mac == "00:00:00:00:00:00";

        private static bool IsUnusableAddress(string ip) =>
            string.IsNullOrWhiteSpace(ip)
            || ip == "0.0.0.0"
            || ip == "255.255.255.255"
            || ip.StartsWith("127.", StringComparison.Ordinal)
            || ip.StartsWith("224.", StringComparison.Ordinal)
            || ip.StartsWith("239.", StringComparison.Ordinal);

        /// <summary>列出本机所有已启用网卡的 IPv4 网段，用于在界面上说明"扫描了哪些网段"</summary>
        public static List<string> GetLocalSubnets()
        {
            var subnets = new List<string>();
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    foreach (UnicastIPAddressInformation info in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (info.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        byte[] addr = info.Address.GetAddressBytes();
                        byte[] mask = info.IPv4Mask.GetAddressBytes();
                        if (mask.Length != 4) continue;

                        int prefix = 0;
                        foreach (byte b in mask)
                        {
                            for (int bit = 7; bit >= 0; bit--)
                                if ((b & (1 << bit)) != 0) prefix++;
                        }

                        uint network = 0;
                        for (int i = 0; i < 4; i++)
                            network |= (uint)(addr[i] & mask[i]) << (24 - i * 8);

                        string networkText = $"{(network >> 24) & 0xFF}.{(network >> 16) & 0xFF}.{(network >> 8) & 0xFF}.{network & 0xFF}/{prefix}";
                        if (!subnets.Contains(networkText)) subnets.Add(networkText);
                    }
                }
            }
            catch
            {
                // 网卡枚举失败不影响扫描本身
            }
            return subnets;
        }
    }
}
