using System.IO;
using System.Text;
using System.Text.Json;

namespace JapaneseTranscriptionPrototype.Services;

public sealed record CaptionHistorySettings(bool Enabled, string FilePath)
{
    public static CaptionHistorySettings Default { get; } = new(false, string.Empty);
}

public sealed class CaptionHistorySettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _settingsPath;

    public CaptionHistorySettingsStore(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JapaneseTranscriptionPrototype",
            "caption-history-settings.json");
    }

    public async Task<CaptionHistorySettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_settingsPath))
            return CaptionHistorySettings.Default;

        await using var stream = new FileStream(
            _settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        return await JsonSerializer.DeserializeAsync<CaptionHistorySettings>(
                   stream, JsonOptions, cancellationToken)
               ?? CaptionHistorySettings.Default;
    }

    public async Task SaveAsync(
        CaptionHistorySettings settings,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(_settingsPath))
                        ?? throw new InvalidOperationException("无法确定历史设置目录。");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $"{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                             useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}

public sealed class CaptionHistoryFile
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private const string CsvHeader = "record_type,timestamp,session_id,japanese,chinese";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _filePath;
    private string? _sessionId;
    public bool IsSessionActive => _sessionId is not null;

    public async Task StartSessionAsync(
        string filePath,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("请先选择字幕历史文件。", nameof(filePath));

        var fullPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullPath)
                        ?? throw new InvalidOperationException("无法确定字幕历史文件夹。");
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"字幕历史文件夹不存在：{directory}");
        if (Directory.Exists(fullPath))
            throw new IOException("字幕历史文件路径指向文件夹，请选择一个 CSV 文件名。");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_sessionId is not null)
                throw new InvalidOperationException("字幕历史会话已经开启。");

            var sessionId = Guid.NewGuid().ToString("N");
            await AppendRowAsync(
                fullPath,
                CsvHeader,
                CsvRow("session_start", timestamp, sessionId, string.Empty, string.Empty),
                cancellationToken,
                addHeaderIfEmpty: true,
                separateFromExistingContent: true);
            _filePath = fullPath;
            _sessionId = sessionId;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AppendCaptionAsync(
        CaptionResult caption,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caption);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var filePath = _filePath ?? throw new InvalidOperationException("字幕历史会话尚未开启。");
            var sessionId = _sessionId ?? throw new InvalidOperationException("字幕历史会话尚未开启。");
            await AppendRowAsync(
                filePath,
                null,
                CsvRow("caption", caption.Timestamp, sessionId, caption.Japanese, caption.Chinese),
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task EndSessionAsync(
        DateTimeOffset timestamp,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_filePath is null || _sessionId is null)
                return;

            var filePath = _filePath;
            var sessionId = _sessionId;
            _filePath = null;
            _sessionId = null;
            await AppendRowAsync(
                filePath,
                null,
                CsvRow("session_end", timestamp, sessionId, string.Empty, string.Empty),
                cancellationToken);
            _filePath = null;
            _sessionId = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task AppendRowAsync(
        string filePath,
        string? header,
        string row,
        CancellationToken cancellationToken,
        bool addHeaderIfEmpty = false,
        bool separateFromExistingContent = false)
    {
        await using var stream = new FileStream(
            filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 4096, useAsync: true);
        var empty = stream.Length == 0;
        if (separateFromExistingContent && !empty)
        {
            stream.Position = stream.Length - 1;
            if (stream.ReadByte() != '\n')
            {
                stream.Position = stream.Length;
                await using var separator = new StreamWriter(stream, Utf8WithoutBom, leaveOpen: true);
                await separator.WriteLineAsync(ReadOnlyMemory<char>.Empty, cancellationToken);
                await separator.FlushAsync(cancellationToken);
            }
        }

        stream.Position = stream.Length;
        await using var writer = new StreamWriter(stream, Utf8WithoutBom);
        if (addHeaderIfEmpty && empty && header is not null)
            await writer.WriteLineAsync(header.AsMemory(), cancellationToken);
        await writer.WriteLineAsync(row.AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
    }

    private static string CsvRow(
        string type,
        DateTimeOffset timestamp,
        string sessionId,
        string japanese,
        string chinese) =>
        string.Join(',',
            CsvField(type),
            CsvField(timestamp.ToString("O")),
            CsvField(sessionId),
            CsvField(japanese),
            CsvField(chinese));

    private static string CsvField(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) < 0
            ? value
            : $"\"{value.Replace("\"", "\"\"")}\"";
}

public sealed class TimelineFollowState
{
    public bool IsFollowingTail { get; private set; } = true;

    public void Reset() => IsFollowingTail = true;

    public void OnScroll(double verticalOffset, double scrollableHeight, bool extentChanged)
    {
        if (extentChanged)
            return;

        IsFollowingTail = verticalOffset >= Math.Max(0, scrollableHeight - 8);
    }
}
