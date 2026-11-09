using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CameraControl.Domain.Calibration;

namespace CameraControl.Infrastructure.Storage;

/// <summary>
/// 标定结果的 JSON 文件存储（一个相机一个文件）。
///
/// 文件名用相机 id 的 SHA-256 前 16 位——因为 id 里含反斜杠与 &amp;，
/// 直接当文件名在 Windows 上会出错；真实 id 仍完整保存在文件内容里。
/// </summary>
public sealed class JsonCalibrationStore : ICalibrationStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true
    };

    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonCalibrationStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        _directory = Path.Combine(dataDirectory, "calibration");
        Directory.CreateDirectory(_directory);
    }

    public async Task<CalibrationRecord?> GetAsync(string cameraId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cameraId);

        string path = PathFor(cameraId);
        if (!File.Exists(path)) return null;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using FileStream stream = File.OpenRead(path);

            CalibrationRecord? record = await JsonSerializer
                .DeserializeAsync<CalibrationRecord>(stream, Options, cancellationToken)
                .ConfigureAwait(false);

            return record;
        }
        catch (JsonException)
        {
            // 文件损坏：当作未标定，不覆盖也不抛
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(CalibrationRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        string path = PathFor(record.CameraId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // 先写临时文件再替换，避免断电留下半截 JSON
            string temporary = path + ".tmp";

            await using (FileStream stream = File.Create(temporary))
            {
                await JsonSerializer
                    .SerializeAsync(stream, record, Options, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<CalibrationRecord>> ListAsync(CancellationToken cancellationToken)
    {
        var records = new List<CalibrationRecord>();

        foreach (string file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await using FileStream stream = File.OpenRead(file);

                CalibrationRecord? record = await JsonSerializer
                    .DeserializeAsync<CalibrationRecord>(stream, Options, cancellationToken)
                    .ConfigureAwait(false);

                if (record is not null) records.Add(record);
            }
            catch (JsonException)
            {
                // 跳过损坏文件
            }
        }

        return [.. records.OrderBy(r => r.CameraId, StringComparer.OrdinalIgnoreCase)];
    }

    public Task<bool> RemoveAsync(string cameraId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cameraId);

        string path = PathFor(cameraId);

        if (!File.Exists(path)) return Task.FromResult(false);

        File.Delete(path);
        return Task.FromResult(true);
    }

    private string PathFor(string cameraId)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(cameraId));
        string name = Convert.ToHexString(hash)[..16].ToLowerInvariant();

        return Path.Combine(_directory, name + ".json");
    }
}
