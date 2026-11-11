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
        string temp = path + ".tmp";

        lock (_sync)
        {
            string json = JsonSerializer.Serialize(value, Options);

            // 重试：新建的 .tmp 偶尔会被杀软/索引器/备份软件短暂占用，
            // 表现为 UnauthorizedAccessException 或 IOException。
            // 一次失败就报错会让用户以为"配置坏了"，其实稍等就能写进去。
            const int maxAttempts = 4;
            Exception? lastError = null;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    // 目录可能被外部删掉，每次写入前确保存在
                    Directory.CreateDirectory(_directory);

                    File.WriteAllText(temp, json);

                    // overwrite:true 比 File.Replace 宽容（后者在某些文件系统上会抛 Access denied）
                    File.Move(temp, path, overwrite: true);

                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    lastError = ex;

                    TryDelete(temp);

                    if (attempt < maxAttempts)
                        Thread.Sleep(30 * attempt);
                }
            }

            throw new SettingsPersistenceException(
                path,
                maxAttempts,
                lastError!);
        }
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
    public SettingsPersistenceException(string path, int attempts, Exception inner)
        : base(
            $"配置写入失败（已尝试 {attempts} 次）：{path}\n" +
            $"原始错误：{inner.GetType().Name} / {inner.Message}\n" +
            "常见原因：安全软件锁定、文件被占用、目录权限不足。可稍后重试；若持续失败，请检查该目录权限。",
            inner)
    {
        Path = path;
        Attempts = attempts;
    }

    /// <summary>写入目标路径。</summary>
    public string Path { get; }

    /// <summary>尝试次数。</summary>
    public int Attempts { get; }
}