using System.Text.Json;
using CameraControl.Domain.Ports;

namespace CameraControl.Infrastructure;

/// <summary>系统时钟。</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>
/// 基于 JSON 文件的设置存储。
///
/// 写入策略：先写临时文件，再原子替换，避免掉电产生半截文件。
/// 读取失败（损坏/格式变更）返回 false，由调用方回退默认值。
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true
    };

    private readonly string _directory;
    private readonly object _sync = new();

    public JsonSettingsStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CameraControl");

        // 构造阶段不因目录不可用而崩：启动时目录可能被占用/无权限，
        // 真到写入会重试并给出可诊断的错误，比"程序起不来"更好排查。
        try
        {
            Directory.CreateDirectory(_directory);
        }
        catch (Exception)
        {
            // 留给 Save 处理
        }
    }

    public bool TryLoad<T>(string key, out T? value)
    {
        value = default;
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        string path = PathFor(key);
        if (!File.Exists(path)) return false;

        try
        {
            lock (_sync)
            {
                string json = File.ReadAllText(path);
                value = JsonSerializer.Deserialize<T>(json, Options);
                return value is not null;
            }
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public void Save<T>(string key, T value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        string path = PathFor(key);

        lock (_sync)
        {
            string json = JsonSerializer.Serialize(value, Options);

            // 三级兜底。为什么不用单一策略：
            // 实测在用户机器上"临时文件+替换"连续 4 次都被拒绝（UnauthorizedAccess），
            // 但手动写同名文件却成功——说明拦截与写法/文件名/时机有关。
            // 多试几种写法，能把"偶发被拦"和"真写不进去"区分开。
            (string Name, Action Run)[] strategies =
            [
                ("临时文件+原子替换", () =>
                {
                    // 唯一名字：避免与上次残留或其它进程持有的固定 .tmp 冲突
                    string temp = $"{path}.{Guid.NewGuid():N}"[..(path.Length + 9)] + ".tmp";
                    File.WriteAllText(temp, json);
                    File.Move(temp, path, overwrite: true);
                }),
                ("固定临时文件", () =>
                {
                    string temp = path + ".tmp";
                    File.WriteAllText(temp, json);
                    File.Move(temp, path, overwrite: true);
                }),
                ("直接覆盖目标文件", () => File.WriteAllText(path, json))
            ];

            var failures = new List<string>();
            Exception? lastError = null;

            foreach ((string name, Action run) in strategies)
            {
                for (int attempt = 1; attempt <= 2; attempt++)
                {
                    try
                    {
                        Directory.CreateDirectory(_directory);
                        run();
                        return;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        lastError = ex;

                        // 这是同步存储 API（ISettingsStore.Save 为同步方法），且整段写入处在 lock (_sync) 内，
                        // 没有可 await 的位置——改成 Task.Delay 就得把接口与调用链一起异步化。
                        // 这里只让出 40ms 做重试退避，属可接受的同步等待，故保留 Thread.Sleep。
                        if (attempt == 1) Thread.Sleep(40);
                    }
                }

                failures.Add($"{name}: {lastError?.GetType().Name} / {lastError?.Message}");
            }

            throw new SettingsPersistenceException(
                path,
                attempts: strategies.Length * 2,
                lastError!,
                failures,
                Probe());
        }
    }

    /// <summary>
    /// 落盘失败时的现场诊断。
    ///
    /// 目的：区分"整个目录写不了"（权限/完整性级别问题）与
    /// "只有某个文件名被拦"（安全软件规则），这两种的处置完全不同。
    /// </summary>
    private string Probe()
    {
        var report = new System.Text.StringBuilder();

        report.Append("目录存在=").Append(Directory.Exists(_directory));

        try
        {
            string probeFile = Path.Combine(_directory, $"probe-{Guid.NewGuid():N}.txt");
            File.WriteAllText(probeFile, "probe");
            File.Delete(probeFile);
            report.Append("；随机名可写=是");
        }
        catch (Exception ex)
        {
            report.Append("；随机名可写=否(").Append(ex.GetType().Name).Append(')');
        }

        try
        {
            string probeTmp = Path.Combine(_directory, $"probe-{Guid.NewGuid():N}.json.tmp");
            File.WriteAllText(probeTmp, "probe");
            File.Delete(probeTmp);
            report.Append("；.tmp 后缀可写=是");
        }
        catch (Exception ex)
        {
            report.Append("；.tmp 后缀可写=否(").Append(ex.GetType().Name).Append(')');
        }

        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            report.Append("；用户=").Append(identity.Name);

            bool elevated = new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

            report.Append(elevated ? "（管理员）" : "（普通用户）");
        }
        catch
        {
            // 拿不到身份信息不影响主流程
        }

        return report.ToString();
    }

    /// <summary>清理残留的临时文件；失败无所谓，下次还会再建。</summary>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 清理失败不掩盖真实错误
        }
    }

    private string PathFor(string key) => Path.Combine(_directory, key + ".json");
}
/// <summary>
/// 设置落盘失败。
///
/// 带上路径、尝试次数与内层错误：用户看到"Access denied"时，
/// 至少能知道是哪个文件、试了几次、原始异常类型是什么。
/// </summary>
public sealed class SettingsPersistenceException : Exception
{
    public SettingsPersistenceException(
        string path,
        int attempts,
        Exception inner,
        IReadOnlyList<string>? strategies = null,
        string? probe = null)
        : base(BuildMessage(path, attempts, inner, strategies, probe), inner)
    {
        Path = path;
        Attempts = attempts;
        Strategies = strategies ?? [];
        Probe = probe;
    }

    /// <summary>写入目标路径。</summary>
    public string Path { get; }

    /// <summary>尝试次数。</summary>
    public int Attempts { get; }

    /// <summary>每种写入策略的失败摘要。</summary>
    public IReadOnlyList<string> Strategies { get; }

    /// <summary>现场诊断（目录能否写、后缀是否被拦、进程身份）。</summary>
    public string? Probe { get; }

    private static string BuildMessage(
        string path,
        int attempts,
        Exception inner,
        IReadOnlyList<string>? strategies,
        string? probe)
    {
        var text = new System.Text.StringBuilder();

        text.AppendLine($"配置写入失败（已尝试 {attempts} 次）：{path}");
        text.AppendLine($"原始错误：{inner.GetType().Name} / {inner.Message}");

        if (strategies is { Count: > 0 })
        {
            text.AppendLine("各策略结果：");
            foreach (string item in strategies) text.AppendLine("  · " + item);
        }

        if (!string.IsNullOrWhiteSpace(probe))
            text.AppendLine($"现场诊断：{probe}");

        text.Append(
            "判断：若「随机名可写=是」而配置仍失败，多半是安全软件按文件名/进程拦截；" +
            "若「随机名可写=否」，是目录权限或进程完整性级别问题。");

        return text.ToString();
    }
}