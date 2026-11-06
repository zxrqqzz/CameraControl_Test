using System.Text.Json;
using CameraControl.Application.Capture;
using CameraControl.Composition;
using CameraControl.Domain.Analysis;
using CameraControl.Domain.Cameras;
using CameraControl.Domain.Ports;
using Microsoft.Extensions.DependencyInjection;

namespace CameraControl.Tools.BurstAnalyzer;

/// <summary>
/// 离线连拍分析工具（P0 交付物）。
///
/// 目的：用真实工位数据回答一个问题——
/// "用相机自身做振动判据，到底可不可行？"
///
/// 它不进运行时路径。输出一份 JSON 报告：基线噪声底 + 衰减曲线 + 经验初值；
/// 若提供融合精度目标则给出可行性结论，否则如实输出 Undetermined。
///
/// 隐私：图像只在内存中参与计算，除了报告 JSON 不产生任何文件。
/// </summary>
internal static class Program
{
    private sealed record Options
    {
        public string? Camera { get; init; }
        public int Frames { get; init; } = 40;
        public int IntervalMs { get; init; }
        public string Mode { get; init; } = "baseline";
        public int RoiSize { get; init; }
        public int SearchRadius { get; init; } = 12;
        public double? FusionAccuracyMm { get; init; }
        public double? ScaleMmPerPx { get; init; }
        public string? Out { get; init; }
    }

    private sealed record Sample(int Index, double AtMs, double AmplitudePx, double Score);

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (!TryParse(args, out Options options))
        {
            PrintUsage();
            return 2;
        }

        using ServiceProvider services = ServiceRegistration.Build();
        var provider = services.GetRequiredService<ICameraProvider>();
        var clock = services.GetRequiredService<IClock>();
        var registry = services.GetRequiredService<ICameraSessionRegistry>();

        // ── 选相机 ────────────────────────────────────────────
        IReadOnlyList<CameraDescriptor> devices = await provider.DiscoverAsync(CancellationToken.None);
        if (devices.Count == 0)
        {
            Console.WriteLine("没有发现任何相机。");
            return 3;
        }

        CameraDescriptor? target = SelectCamera(devices, options.Camera);
        if (target is null)
        {
            Console.WriteLine($"未找到匹配 '{options.Camera}' 的相机。可用：");
            foreach (CameraDescriptor d in devices)
                Console.WriteLine($"  {d.Id}  {d.DisplayName}  红外={d.IsInfraredOnly}");
            return 3;
        }

        Console.WriteLine($"相机：{target.DisplayName}（{target.Id}）");
        Console.WriteLine($"模式：{options.Mode}  帧数：{options.Frames}  间隔：{options.IntervalMs}ms");

        // ── 采集 ──────────────────────────────────────────────
        ICameraSession session = await registry.OpenAsync(target.Id, CancellationToken.None);

        var frames = new List<(Domain.Capture.Frame Frame, double AtMs)>();
        DateTimeOffset startedAt = clock.UtcNow;

        try
        {
            for (int i = 0; i < options.Frames; i++)
            {
                Domain.Capture.Frame frame = await session.CaptureSingleAsync(null, CancellationToken.None);
                frames.Add((frame, (clock.UtcNow - startedAt).TotalMilliseconds));

                if (options.IntervalMs > 0)
                    await Task.Delay(options.IntervalMs);
            }
        }
        finally
        {
            await registry.CloseAsync(target.Id);
        }

        if (frames.Count < 3)
        {
            Console.WriteLine($"采集到的帧数不足（{frames.Count}），无法分析。");
            return 4;
        }

        // ── 分析 ──────────────────────────────────────────────
        Domain.Capture.FrameImage first = frames[0].Frame.Image;
        int roiSize = options.RoiSize > 0
            ? Math.Min(options.RoiSize, Math.Min(first.Width, first.Height))
            : Roi.CenterSquare(first.Width, first.Height, 0.25).Width;

        var roi = new Roi((first.Width - roiSize) / 2, (first.Height - roiSize) / 2, roiSize, roiSize);
        GrayImage reference = ToGray(frames[0].Frame.Image);

        var samples = new List<Sample>(frames.Count - 1);
        for (int i = 1; i < frames.Count; i++)
        {
            GrayImage current = ToGray(frames[i].Frame.Image);
            OffsetMeasurement m = VibrationAnalyzer.Measure(reference, current, roi, options.SearchRadius);

            if (double.IsNaN(m.Amplitude)) continue;
            samples.Add(new Sample(i, frames[i].AtMs, m.Amplitude, m.Score));
        }

        if (samples.Count == 0)
        {
            Console.WriteLine("所有帧的相关性都低于阈值（可能是纯色画面或缺纹理），无法测量位移。");
            return 5;
        }

        double[] amplitudes = [.. samples.Select(s => s.AmplitudePx).OrderBy(a => a)];
        double median = Percentile(amplitudes, 0.5);
        double p90 = Percentile(amplitudes, 0.9);
        double peak = amplitudes[^1];
        double meanIntervalMs = MeanInterval(samples);

        string verdict = ResolveVerdict(options, p90, out string verdictReason);

        // ── 报告 ──────────────────────────────────────────────
        var report = new
        {
            generatedAtUtc = clock.UtcNow,
            camera = new
            {
                id = target.Id.Value,
                name = target.DisplayName,
                kind = target.Kind.ToString(),
                width = first.Width,
                height = first.Height,
                pixelFormat = first.PixelFormat
            },
            capture = new
            {
                mode = options.Mode,
                requestedFrames = options.Frames,
                capturedFrames = frames.Count,
                measuredIntervalMs = Math.Round(meanIntervalMs, 2),
                roi = new { roi.X, roi.Y, roi.Width, roi.Height },
                searchRadius = options.SearchRadius
            },
            statistics = new
            {
                medianPx = Round(median),
                p90Px = Round(p90),
                peakPx = Round(peak),
                normalizedMedian = options.ScaleMmPerPx is double scaleA ? Round(median * scaleA) : (double?)null,
                normalizedP90 = options.ScaleMmPerPx is double scaleB ? Round(p90 * scaleB) : (double?)null,
                unitNote = options.ScaleMmPerPx is null
                    ? "未提供 --scale-mm-per-px，结果为像素单位"
                    : "已按像素当量换算为 mm"
            },
            recommended = new
            {
                maxAmplitudePx = Round(Math.Max(p90 * 2, 0.01)),
                stableDurationMs = Math.Round(meanIntervalMs * 3, 1),
                validated = false,
                note = "经验初值（噪声底×2），未用融合精度目标校验"
            },
            verdict,
            verdictReason,
            samples = samples.Select(s => new
            {
                index = s.Index,
                atMs = Math.Round(s.AtMs, 1),
                amplitudePx = Round(s.AmplitudePx),
                score = Round(s.Score)
            })
        };

        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });

        if (!string.IsNullOrWhiteSpace(options.Out))
        {
            string path = Path.GetFullPath(options.Out);
            await File.WriteAllTextAsync(path, json);
            Console.WriteLine($"报告已写入：{path}");
        }
        else
        {
            Console.WriteLine(json);
        }

        Console.WriteLine();
        Console.WriteLine($"噪声底(中位) = {median:F3} px    P90 = {p90:F3} px    峰值 = {peak:F3} px");
        Console.WriteLine($"建议 maxAmplitude = {Math.Max(p90 * 2, 0.01):F3} px，stableDuration ≈ {meanIntervalMs * 3:F0} ms");
        Console.WriteLine($"结论：{verdict}（{verdictReason}）");

        return 0;
    }

    private static string ResolveVerdict(Options options, double p90Px, out string reason)
    {
        if (options.FusionAccuracyMm is not double accuracy || options.ScaleMmPerPx is not double scale)
        {
            reason = "未提供融合精度目标(--fusion-accuracy-mm)或像素当量(--scale-mm-per-px)，只输出数据，不做可行性结论";
            return "Undetermined";
        }

        double noiseMm = p90Px * scale;
        if (noiseMm <= accuracy / 2)
        {
            reason = $"噪声底 {noiseMm:F4}mm ≤ 精度目标 {accuracy:F4}mm 的一半，视觉判据可行";
            return "Viable";
        }

        if (noiseMm <= accuracy)
        {
            reason = $"噪声底 {noiseMm:F4}mm 接近精度目标 {accuracy:F4}mm，勉强可用（建议加大 ROI 或多帧平均）";
            return "Marginal";
        }

        reason = $"噪声底 {noiseMm:F4}mm 超过精度目标 {accuracy:F4}mm，视觉判据不可行，需换方案（镜头/减震/外部传感器）";
        return "NotViable";
    }

    private static CameraDescriptor? SelectCamera(IReadOnlyList<CameraDescriptor> devices, string? selector)
    {
        if (!string.IsNullOrWhiteSpace(selector))
        {
            return devices.FirstOrDefault(d =>
                       string.Equals(d.Id.Value, selector, StringComparison.OrdinalIgnoreCase))
                   ?? devices.FirstOrDefault(d =>
                       d.DisplayName.Contains(selector, StringComparison.OrdinalIgnoreCase));
        }

        return devices.FirstOrDefault(d => d.CanCapture);
    }

    private static GrayImage ToGray(Domain.Capture.FrameImage image) =>
        GrayImage.FromFrame(
            image.Width,
            image.Height,
            image.Stride,
            image.PixelFormat,
            image.Pixels.Span);

    private static double MeanInterval(List<Sample> samples)
    {
        if (samples.Count < 2) return 0;

        double total = samples[^1].AtMs - samples[0].AtMs;
        return total <= 0 ? 0 : total / (samples.Count - 1);
    }

    private static double Percentile(double[] sortedAscending, double fraction)
    {
        if (sortedAscending.Length == 0) return 0;

        double position = (sortedAscending.Length - 1) * fraction;
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);

        if (lower == upper) return sortedAscending[lower];

        double weight = position - lower;
        return (sortedAscending[lower] * (1 - weight)) + (sortedAscending[upper] * weight);
    }

    private static double Round(double value) => Math.Round(value, 4);

    private static bool TryParse(string[] args, out Options options)
    {
        string? camera = null;
        int frames = 40;
        int interval = 0;
        string mode = "baseline";
        int roi = 0;
        int search = 12;
        double? accuracy = null;
        double? scale = null;
        string? output = null;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];

            if (a is "--help" or "-h")
            {
                options = new Options();
                return false;
            }

            string Next()
            {
                if (i + 1 >= args.Length) throw new FormatException($"参数 {a} 缺少值。");
                return args[++i];
            }

            try
            {
                switch (a)
                {
                    case "--camera": camera = Next(); break;
                    case "--frames": frames = int.Parse(Next()); break;
                    case "--interval-ms": interval = int.Parse(Next()); break;
                    case "--mode": mode = Next(); break;
                    case "--roi": roi = int.Parse(Next()); break;
                    case "--search-radius": search = int.Parse(Next()); break;
                    case "--fusion-accuracy-mm": accuracy = double.Parse(Next()); break;
                    case "--scale-mm-per-px": scale = double.Parse(Next()); break;
                    case "--out": output = Next(); break;
                    default:
                        Console.WriteLine($"未知参数：{a}");
                        options = new Options();
                        return false;
                }
            }
            catch (FormatException)
            {
                options = new Options();
                return false;
            }
        }

        options = new Options
        {
            Camera = camera,
            Frames = Math.Clamp(frames, 3, 500),
            IntervalMs = Math.Max(0, interval),
            Mode = mode,
            RoiSize = roi,
            SearchRadius = Math.Max(2, search),
            FusionAccuracyMm = accuracy,
            ScaleMmPerPx = scale,
            Out = output
        };
        return true;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
        BurstAnalyzer — 离线连拍振动分析（P0 工具，不进运行时）

        用法：
          BurstAnalyzer [选项]

        选项：
          --camera <id|名称片段>        选择相机；省略则用第一台可采集相机
          --frames <n>                 连拍帧数（默认 40，上限 500）
          --interval-ms <n>            帧间隔毫秒（默认 0，即尽可能快）
          --mode <baseline|settling>   采集场景（默认 baseline）
          --roi <px>                   ROI 边长（默认取图像短边 1/4）
          --search-radius <px>         位移搜索半径（默认 12）
          --scale-mm-per-px <v>        像素当量，用于换算 mm
          --fusion-accuracy-mm <v>     融合精度目标；不给则结论为 Undetermined
          --out <path>                 报告输出路径（省略则打印到控制台）

        说明：
          图像只在内存中参与计算，除报告 JSON 外不产生任何文件。
          baseline 模式：机械臂完全静止时采集，测噪声底。
          settling 模式：在采集开始时扰动机械臂，看衰减到何时稳定。
        """);
    }
}
