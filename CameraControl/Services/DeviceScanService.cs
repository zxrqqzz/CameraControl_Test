using CameraControl.Models;
using MvCameraControl;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CameraControl.Services
{
    /// <summary>扫描范围/选项</summary>
    public sealed class ScanOptions
    {
        /// <summary>扫描海康 MVS SDK 设备(GigE Vision / USB3 Vision / GenTL 采集卡)</summary>
        public bool EnableHikSdk { get; set; } = true;

        /// <summary>扫描本机 UVC 摄像头(含笔记本自带摄像头)</summary>
        public bool EnableUvc { get; set; } = true;

        /// <summary>局域网被动检测(读 ARP 表，零网络流量)</summary>
        public bool EnableLanPassive { get; set; } = true;

        /// <summary>局域网 ONVIF 主动发现(会发送一条标准组播报文)</summary>
        public bool EnableLanOnvif { get; set; }

        /// <summary>ONVIF 等待回包的时长(毫秒)</summary>
        public int OnvifTimeoutMs { get; set; } = 3000;

        /// <summary>从保存的设置里读取</summary>
        public static ScanOptions FromSettings(ScanSettings settings) => new ScanOptions
        {
            EnableHikSdk = settings.EnableHikSdk,
            EnableUvc = settings.EnableUvc,
            EnableLanPassive = settings.EnableLanPassive,
            EnableLanOnvif = settings.EnableLanOnvif,
            OnvifTimeoutMs = settings.OnvifTimeoutMs <= 0 ? 3000 : settings.OnvifTimeoutMs
        };

        /// <summary>写回设置对象</summary>
        public void ApplyTo(ScanSettings settings)
        {
            settings.EnableHikSdk = EnableHikSdk;
            settings.EnableUvc = EnableUvc;
            settings.EnableLanPassive = EnableLanPassive;
            settings.EnableLanOnvif = EnableLanOnvif;
            settings.OnvifTimeoutMs = OnvifTimeoutMs;
        }
    }

    /// <summary>一次扫描的完整结果</summary>
    public sealed class ScanReport
    {
        /// <summary>扫描到的候选设备(已按去重键合并)</summary>
        public List<ScanCandidate> Candidates { get; } = new List<ScanCandidate>();

        /// <summary>本机识别到的网段</summary>
        public List<string> LocalSubnets { get; } = new List<string>();

        /// <summary>本机 ARP 邻居表条目数(用来说明"扫了多少条")</summary>
        public int ArpEntryCount { get; set; }

        /// <summary>过程消息(按时间顺序)</summary>
        public List<string> Messages { get; } = new List<string>();

        /// <summary>扫描耗时</summary>
        public TimeSpan Elapsed { get; set; }
    }

    /// <summary>
    /// 设备扫描编排器: 按勾选的扫描范围依次跑各路扫描器，合并结果并按去重键去重。
    ///
    /// 安全边界(实现约束, 不要放宽):
    /// - 海康 SDK: 只枚举，不 Open 设备、不取流。
    /// - UVC: 只读注册表，不打开摄像头。
    /// - 局域网: 默认只有被动 ARP 读取(零报文)；ONVIF 主动发现必须由用户显式勾选，
    ///   且只解析设备自己广播的元数据，不认证、不访问服务地址、不请求图像。
    /// </summary>
    public sealed class DeviceScanService
    {
        /// <summary>执行一次扫描</summary>
        public async Task<ScanReport> ScanAsync(
            ScanOptions options,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var report = new ScanReport();
            DateTime startedAt = DateTime.UtcNow;

            void Report(string message)
            {
                report.Messages.Add(message);
                progress?.Report(message);
            }

            try
            {
                // 1) 海康 MVS SDK
                if (options.EnableHikSdk)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Report("正在扫描海康 SDK 设备…");

                    List<HikDeviceEnumerator.Entry> hikEntries = await Task.Run(() =>
                    {
                        var collected = new List<HikDeviceEnumerator.Entry>();
                        int initRet = HikSdkRuntime.Acquire();
                        if (initRet != MvError.MV_OK)
                        {
                            Report($"海康 SDK 初始化失败: 0x{initRet:X}，已跳过该扫描项");
                            return collected;
                        }

                        try
                        {
                            collected.AddRange(HikDeviceEnumerator.Enumerate(Report));
                        }
                        finally
                        {
                            HikSdkRuntime.Release();
                        }
                        return collected;
                    }, cancellationToken).ConfigureAwait(false);

                    foreach (HikDeviceEnumerator.Entry entry in hikEntries)
                        AddCandidate(report, HikDeviceEnumerator.ToCandidate(entry));

                    Report($"海康 SDK: 发现 {hikEntries.Count} 个设备");
                }

                // 2) 本机 UVC 摄像头
                if (options.EnableUvc)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Report("正在扫描本机 UVC 摄像头…");

                    List<ScanCandidate> uvc = await Task.Run(
                        () => UvcDeviceScanner.Scan(Report), cancellationToken).ConfigureAwait(false);

                    foreach (ScanCandidate candidate in uvc)
                        AddCandidate(report, candidate);

                    Report($"UVC 摄像头: 发现 {uvc.Count} 个");
                }

                // 3) 局域网被动检测
                if (options.EnableLanPassive)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Report("正在读取本机 ARP 邻居表(不发送任何报文)…");

                    List<ScanCandidate> passive = await Task.Run(() =>
                    {
                        List<ScanCandidate> found = LanDeviceScanner.ScanPassive(
                            out int arpCount, out List<string> subnets, Report);
                        report.ArpEntryCount = arpCount;
                        report.LocalSubnets.Clear();
                        report.LocalSubnets.AddRange(subnets);
                        return found;
                    }, cancellationToken).ConfigureAwait(false);

                    foreach (ScanCandidate candidate in passive)
                        AddCandidate(report, candidate);

                    Report($"局域网被动检测: 命中 {passive.Count} 个疑似相机");
                }

                // 4) 局域网 ONVIF 主动发现(默认关闭)
                if (options.EnableLanOnvif)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Report("正在发送 ONVIF 发现报文(只读取设备广播的型号信息)…");

                    List<ScanCandidate> onvif = await LanDeviceScanner
                        .ScanOnvifAsync(options.OnvifTimeoutMs, cancellationToken, Report)
                        .ConfigureAwait(false);

                    foreach (ScanCandidate candidate in onvif)
                        AddCandidate(report, candidate);

                    Report($"ONVIF 发现: 响应 {onvif.Count} 个");
                }
            }
            catch (OperationCanceledException)
            {
                Report("扫描已取消。");
                throw;
            }

            report.Elapsed = DateTime.UtcNow - startedAt;
            Report($"扫描完成，共 {report.Candidates.Count} 个候选设备，用时 {report.Elapsed.TotalSeconds:F1} 秒。");
            return report;
        }

        /// <summary>按去重键合并，同一个物理设备只保留一条(优先保留信息更全的)</summary>
        private static void AddCandidate(ScanReport report, ScanCandidate candidate)
        {
            string key = candidate.DedupeKey;
            if (key.Length == 0)
            {
                report.Candidates.Add(candidate);
                return;
            }

            foreach (ScanCandidate existing in report.Candidates)
            {
                if (!string.Equals(existing.DedupeKey, key, StringComparison.Ordinal)) continue;

                // 同一个设备被两种方式发现时，用信息更全的那条覆盖，但保留"可连接"的那条来源
                if (Score(candidate) > Score(existing))
                {
                    existing.Name = candidate.Name;
                    existing.Vendor = candidate.Vendor;
                    existing.Model = candidate.Model;
                    existing.SerialNumber = candidate.SerialNumber;
                    existing.Ip = candidate.Ip;
                    existing.Mac = candidate.Mac;
                    existing.DevicePath = candidate.DevicePath;
                    existing.InterfaceText = candidate.InterfaceText;
                    existing.Source = candidate.Source;
                }
                return;
            }

            report.Candidates.Add(candidate);
        }

        /// <summary>信息完整度打分，用于同键合并时择优</summary>
        private static int Score(ScanCandidate c)
        {
            int score = 0;
            if (c.Source.Connectable()) score += 100;   // 能连的优先
            if (!string.IsNullOrWhiteSpace(c.Vendor)) score += 10;
            if (!string.IsNullOrWhiteSpace(c.Model)) score += 10;
            if (!string.IsNullOrWhiteSpace(c.SerialNumber)) score += 5;
            if (!string.IsNullOrWhiteSpace(c.Ip)) score += 3;
            if (!string.IsNullOrWhiteSpace(c.Mac)) score += 2;
            return score;
        }
    }
}
