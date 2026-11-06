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

        Directory.CreateDirectory(_directory);
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
            File.WriteAllText(temp, json);

            if (File.Exists(path))
                File.Replace(temp, path, destinationBackupFileName: null);
            else
                File.Move(temp, path);
        }
    }

    private string PathFor(string key) => Path.Combine(_directory, key + ".json");
}
