using System.Text.Json;
using CameraControl.Application.Auditing;

namespace CameraControl.Infrastructure.Auditing;

/// <summary>
/// 追加式审计日志（JSON Lines）。
///
/// 选 JSONL 而不是单个 JSON 数组：追加写不会因为进程崩溃损坏既有记录。
/// </summary>
public sealed class FileAuditLog : IAuditLog
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    private readonly string _path;
    private readonly object _sync = new();

    public FileAuditLog(string? directory = null)
    {
        string dir = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CameraControl",
            "audit");

        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "audit.jsonl");
    }

    public void Write(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        string line = JsonSerializer.Serialize(entry, Options);
        lock (_sync)
        {
            File.AppendAllText(_path, line + Environment.NewLine);
        }
    }

    public IReadOnlyList<AuditEntry> Recent(int count)
    {
        if (count <= 0) return [];

        lock (_sync)
        {
            if (!File.Exists(_path)) return [];

            // P0 数据量小，整读后取尾部；数据量上来后改成按行倒读
            string[] lines = File.ReadAllLines(_path);
            var entries = new List<AuditEntry>(Math.Min(count, lines.Length));

            for (int i = lines.Length - 1; i >= 0 && entries.Count < count; i--)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                try
                {
                    AuditEntry? entry = JsonSerializer.Deserialize<AuditEntry>(lines[i], Options);
                    if (entry is not null) entries.Add(entry);
                }
                catch (JsonException)
                {
                    // 跳过损坏行，不影响其它记录
                }
            }

            return entries;
        }
    }
}
