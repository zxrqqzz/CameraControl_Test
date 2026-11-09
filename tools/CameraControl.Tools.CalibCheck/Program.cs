using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CameraControl.Domain.Analysis;
using CameraControl.Domain.Calibration;
using OpenCvSharp;

namespace CameraControl.Tools.CalibCheck;

/// <summary>
/// 真实数据标定验证工具。
///
/// 分工：
/// - **角点检测**交给 OpenCV（`FindChessboardCorners` + `CornerSubPix`，工业级实现）；
/// - **内参求解**用本项目自己的 `ZhangCalibrator`（这才是要验证的对象）。
///
/// 这样才能把"求解算法是否正确"和"检测实现是否鲁棒"两件事分开评估。
/// </summary>
internal static class Program
{
    private static readonly int[,] CandidateDims =
    {
        { 12, 11 }, { 11, 12 },   // Caltech 示例常见尺寸
        { 7, 7 }, { 8, 8 }, { 9, 6 }, { 6, 9 },
        { 10, 7 }, { 7, 10 }, { 11, 8 }, { 8, 11 },
        { 9, 9 }, { 10, 10 }, { 13, 12 }, { 12, 13 },
        { 9, 7 }, { 7, 9 }, { 11, 7 }, { 7, 11 }
    };

    private sealed record ImageResult(string Name, int? Order, Point2[]? Corners, string? Error);

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        string directory = GetValue(args, "--dir") ?? ".";
        double squareMm = double.TryParse(GetValue(args, "--square-mm"), out double s) ? s : 30.0;
        bool verbose = args.Contains("--verbose", StringComparer.Ordinal);

        int forcedCols = int.TryParse(GetValue(args, "--grid-cols"), out int fc) ? fc : 0;
        int forcedRows = int.TryParse(GetValue(args, "--grid-rows"), out int fr) ? fr : 0;

        if (!Directory.Exists(directory))
        {
            Console.Error.WriteLine($"目录不存在：{directory}");
            return 2;
        }

        string[] files = [.. Directory
            .EnumerateFiles(directory)
            .Where(f => f.EndsWith(".tif", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)];

        if (files.Length == 0)
        {
            Console.Error.WriteLine("目录里没有可读图像。");
            return 2;
        }

        // 解码一次，缓存起来（后面可能要试多个尺寸）
        var images = new List<(string Name, GrayImage Image)>();

        foreach (string file in files)
        {
            GrayImage? gray = LoadGray(file);
            if (gray is null) continue;
            images.Add((Path.GetFileName(file), gray));
        }

        Console.WriteLine($"图像目录：{directory}");
        Console.WriteLine($"图像数量：{images.Count}    方格边长：{squareMm} mm");
        Console.WriteLine($"检测方式：OpenCV {Cv2.GetVersionString()}");
        Console.WriteLine();

        // ── 确定棋盘尺寸 ──────────────────────────────────
        int cols = forcedCols;
        int rows = forcedRows;

        if (cols < 4 || rows < 4)
        {
            Console.WriteLine("=== 自动探测棋盘尺寸 ===");
            var scan = new List<(int Rows, int Cols, int Hits)>();

            for (int i = 0; i < CandidateDims.GetLength(0); i++)
            {
                int c = CandidateDims[i, 0];
                int r = CandidateDims[i, 1];
                int hits = images.Count(img => DetectChessboard(img.Image, c, r) is not null);
                scan.Add((r, c, hits));
            }

            foreach ((int Rows, int Cols, int Hits) item in scan
                         .Where(t => t.Hits > 0)
                         .OrderByDescending(t => t.Hits)
                         .Take(5))
            {
                Console.WriteLine($"  {item.Rows}行×{item.Cols}列 内角点：命中 {item.Hits}/{images.Count} 张");
            }

            var best = scan.OrderByDescending(t => t.Hits).First();
            cols = best.Cols;
            rows = best.Rows;

            Console.WriteLine($"  选定：{rows}行 × {cols}列（命中 {best.Hits}/{images.Count}）");
            Console.WriteLine();
        }

        // ── 逐张检测 ──────────────────────────────────────
        var results = new List<ImageResult>(images.Count);

        foreach ((string name, GrayImage image) in images)
        {
            Point2f[]? corners = DetectChessboard(image, cols, rows);

            if (corners is null)
            {
                results.Add(new ImageResult(name, null, null, "未检测到完整棋盘"));
                if (verbose) Console.WriteLine($"  {name,-14} 失败");
                continue;
            }

            Point2[] points = [.. corners.Select(c => new Domain.Calibration.Point2(c.X, c.Y))];
            results.Add(new ImageResult(name, null, points, null));

            if (verbose) Console.WriteLine($"  {name,-14} 角点 {points.Length}  OK");
        }

        int okCount = results.Count(r => r.Corners is not null);
        Console.WriteLine($"检测成功：{okCount}/{images.Count}（{rows}行×{cols}列）");

        if (okCount < 3)
        {
            Console.Error.WriteLine("成功视角不足 3 个，无法标定。");
            return 3;
        }

        // ── 标定 ──────────────────────────────────────────
        var spec = new ChessboardSpec(cols, rows, squareMm);

        // OpenCV 返回的行优先顺序与本项目约定一致：(row, col) 行优先
        var views = results
            .Where(r => r.Corners is not null)
            .Select(r => new CalibrationView(spec.ObjectPoints(), r.Corners!))
            .ToList();

        GrayImage firstImage = images[0].Image;

        var closedForm = new ZhangCalibrator();
        IntrinsicsSolveResult closed = closedForm.Solve(views, spec, firstImage.Width, firstImage.Height);

        var calibrator = new BundleAdjustmentCalibrator();
        IntrinsicsSolveResult result = calibrator.Solve(views, spec, firstImage.Width, firstImage.Height);

        Console.WriteLine();
        Console.WriteLine("=== 对比：闭合解 vs 畸变精化（真实数据）===");

        if (closed.Ok && closed.Intrinsics is not null)
        {
            CameraIntrinsics ck = closed.Intrinsics;
            Console.WriteLine($"  闭合解   ：fx={ck.Fx:F2} fy={ck.Fy:F2} cx={ck.Cx:F2} cy={ck.Cy:F2}  " +
                              $"单应RMSE={closed.HomographyRmsePx:F3}px");
        }
        else
        {
            Console.WriteLine($"  闭合解   ：失败（{closed.Error}）");
        }

        Console.WriteLine();
        Console.WriteLine("=== 标定结果（真实数据 / 含畸变 + 非线性精化）===");

        if (!result.Ok)
        {
            Console.Error.WriteLine($"失败：{result.Error}");
            foreach (string d in result.Diagnostics ?? []) Console.Error.WriteLine($"  · {d}");
            return 5;
        }

        CameraIntrinsics k = result.Intrinsics!;
        Console.WriteLine($"  fx = {k.Fx:F3}");
        Console.WriteLine($"  fy = {k.Fy:F3}   (fy/fx = {k.Fy / k.Fx:F5})");
        Console.WriteLine($"  cx = {k.Cx:F3}");
        Console.WriteLine($"  cy = {k.Cy:F3}");
        Console.WriteLine($"  图像中心 = ({firstImage.Width / 2.0:F1}, {firstImage.Height / 2.0:F1})");
        Console.WriteLine($"  单应 RMSE = {result.HomographyRmsePx:F3} px（闭合解阶段）");
        Console.WriteLine($"  重投影 RMSE = {result.ReprojectionRmsePx:F3} px（完整模型）");

        if (result.Distortion is not null)
            Console.WriteLine($"  畸变 = {result.Distortion}");

        if (closed.Ok && closed.HomographyRmsePx > 0 && result.ReprojectionRmsePx > 0)
        {
            double drop = (1 - (result.ReprojectionRmsePx / closed.HomographyRmsePx)) * 100;
            Console.WriteLine($"  误差下降 {drop:F1}%（{closed.HomographyRmsePx:F3} → {result.ReprojectionRmsePx:F3} px）");
        }

        Console.WriteLine($"  视角数 = {views.Count}，每视角角点 = {spec.Count}");

        foreach (string d in result.Diagnostics ?? []) Console.WriteLine($"  · {d}");

        // ── 权威参考：用 OpenCV 自己的标定（同一批角点）──────────
        if (args.Contains("--reference", StringComparer.Ordinal))
        {
            RunOpenCvReference(views, firstImage, spec);
        }

        // ── 稳定性：奇偶分半 ──────────────────────────────
        Console.WriteLine();
        Console.WriteLine("=== 稳定性检验（奇偶分半独立标定）===");

        ReportHalf("偶数视角", [.. views.Where((_, i) => i % 2 == 0)]);
        ReportHalf("奇数视角", [.. views.Where((_, i) => i % 2 == 1)]);

        return 0;

        void ReportHalf(string label, List<CalibrationView> subset)
        {
            if (subset.Count < 3)
            {
                Console.WriteLine($"  {label}：视角不足（{subset.Count}）");
                return;
            }

            IntrinsicsSolveResult half = calibrator.Solve(subset, spec, firstImage.Width, firstImage.Height);

            if (!half.Ok)
            {
                Console.WriteLine($"  {label}：失败（{half.Error}）");
                return;
            }

            CameraIntrinsics hk = half.Intrinsics!;
            double dx = Math.Abs(hk.Fx - k.Fx) / k.Fx * 100;

            Console.WriteLine(
                $"  {label}（{subset.Count} 视角）：fx={hk.Fx:F2} fy={hk.Fy:F2} " +
                $"cx={hk.Cx:F1} cy={hk.Cy:F1} RMSE={half.HomographyRmsePx:F3}px  " +
                $"与全集 fx 差异 {dx:F2}%");
        }
    }

    /// <summary>用 OpenCV 检测棋盘内角点并做亚像素精化。</summary>
    private static Point2f[]? DetectChessboard(GrayImage image, int cols, int rows)
    {
        try
        {
            using var mat = Mat.FromPixelData(image.Height, image.Width, MatType.CV_8UC1, image.Pixels);

            const ChessboardFlags flags =
                ChessboardFlags.AdaptiveThresh | ChessboardFlags.NormalizeImage | ChessboardFlags.FastCheck;

            if (Cv2.FindChessboardCorners(mat, new OpenCvSharp.Size(cols, rows), out Point2f[] corners, flags)
                && corners.Length == cols * rows)
            {
                Cv2.CornerSubPix(
                    mat,
                    corners,
                    new OpenCvSharp.Size(5, 5),
                    new OpenCvSharp.Size(-1, -1),
                    new TermCriteria(CriteriaTypes.Eps | CriteriaTypes.Count, 30, 0.01));

                return corners;
            }

            // 第二选择：SB（segment-based）检测器，对模糊/低对比/强畸变更鲁棒
            if (Cv2.FindChessboardCornersSB(
                    mat,
                    new OpenCvSharp.Size(cols, rows),
                    out Point2f[] sbCorners,
                    ChessboardFlags.None)
                && sbCorners.Length == cols * rows)
            {
                return sbCorners;
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>用 WPF 解码器读图并转成 8 位灰度（不产生临时文件）。</summary>
    private static GrayImage? LoadGray(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);

            BitmapDecoder decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.OnLoad);

            BitmapSource source = decoder.Frames[0];
            var converted = new FormatConvertedBitmap(source, PixelFormats.Gray8, null, 0);

            int width = converted.PixelWidth;
            int height = converted.PixelHeight;
            int stride = width;

            var buffer = new byte[stride * height];
            converted.CopyPixels(buffer, stride, 0);

            return new GrayImage(width, height, buffer);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  解码失败 {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    private static string? GetValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
                return args[i + 1];
        }

        return null;
}

    /// <summary>
    /// 用 OpenCV 的 calibrateCamera 在同一批角点上跑一遍，作为权威参考。
    ///
    /// 意义：OpenCV 是行业标准实现，数据完全一致，因此差异只来自算法本身。
    /// 它给出的 fx / distCoeffs / RMS 就是"正确实现应该达到的水平"。
    /// </summary>
    private static void RunOpenCvReference(
        List<CalibrationView> views,
        GrayImage image,
        ChessboardSpec spec)
    {
        Console.WriteLine();
        Console.WriteLine("=== 参考实现：OpenCV calibrateCamera（同一批角点）===");

        var objectPoints = new List<Point3f[]>(views.Count);
        var imagePoints = new List<Point2f[]>(views.Count);

        foreach (CalibrationView view in views)
        {
            objectPoints.Add([.. view.ObjectPoints.Select(p => new Point3f((float)p.X, (float)p.Y, (float)p.Z))]);
            imagePoints.Add([.. view.ImagePoints.Select(p => new Point2f((float)p.X, (float)p.Y))]);
        }

        var cameraMatrix = new double[3, 3];
        var distCoeffs = new double[5];

        double rms = Cv2.CalibrateCamera(
            objectPoints,
            imagePoints,
            new OpenCvSharp.Size(image.Width, image.Height),
            cameraMatrix,
            distCoeffs,
            out _,
            out _,
            CalibrationFlags.None,
            new TermCriteria(CriteriaTypes.Eps | CriteriaTypes.Count, 100, 1e-9));

        Console.WriteLine($"  fx = {cameraMatrix[0, 0]:F3}");
        Console.WriteLine($"  fy = {cameraMatrix[1, 1]:F3}");
        Console.WriteLine($"  cx = {cameraMatrix[0, 2]:F3}");
        Console.WriteLine($"  cy = {cameraMatrix[1, 2]:F3}");
        Console.WriteLine($"  畸变 = k1={distCoeffs[0]:F6} k2={distCoeffs[1]:F6} " +
                          $"p1={distCoeffs[2]:F6} p2={distCoeffs[3]:F6} k3={distCoeffs[4]:F6}");
        Console.WriteLine($"  重投影 RMS = {rms:F4} px  ← 参考水平");
        Console.WriteLine($"  视角数 = {views.Count}，每视角角点 = {spec.Count}");
    }
}
