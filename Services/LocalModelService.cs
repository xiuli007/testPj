using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace JapaneseTranscriptionPrototype.Services;

public enum LocalModelOutcome { Translated, NoSpeech, Unrecognized, TooShort, NonJapanese }

public sealed record LocalAudioDiagnostics(
    double DurationSeconds, double Rms, double Peak, double ActiveRatio, string Language)
{
    public override string ToString() =>
        $"16kHz / {DurationSeconds:0.00}s / RMS {Rms:0.0000} / peak {Peak:0.0000} / active {ActiveRatio:P1} / lang {Language}";
}

public sealed record LocalModelResult(
    string Japanese, string Chinese, double AsrSeconds, double TranslationSeconds, long WorkingSetMegabytes,
    LocalModelOutcome Outcome, LocalAudioDiagnostics Diagnostics)
{
    public double TotalSeconds => AsrSeconds + TranslationSeconds;
    public bool HasTranslation => Outcome == LocalModelOutcome.Translated;
    public string StatusMessage => (Outcome switch
    {
        LocalModelOutcome.NoSpeech => "本段能量较低，未识别出语音；等待下一段（能量门限不等于判定静音）。",
        LocalModelOutcome.Unrecognized => "本段有声音但未识别出文本，可能为噪声或句子截断；继续等待，不判定为静音。",
        LocalModelOutcome.TooShort => "本段不足 0.5 秒，未送入识别或翻译；等待下一段。",
        LocalModelOutcome.NonJapanese => "模型标记本段为非日语，未送入日→中翻译；等待日语语音。",
        _ => "本段本地识别和翻译完成。"
    }) + $"（{Diagnostics}）";

    public static LocalModelResult FromResponse(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error))
            throw new InvalidOperationException(error.GetString());
        var outcome = root.GetProperty("outcome").GetString() switch
        {
            "translated" => LocalModelOutcome.Translated,
            "no_speech" => LocalModelOutcome.NoSpeech,
            "unrecognized" => LocalModelOutcome.Unrecognized,
            "too_short" => LocalModelOutcome.TooShort,
            "non_japanese" => LocalModelOutcome.NonJapanese,
            _ => throw new InvalidDataException("本地模型返回未知状态；不会将异常当成静音。")
        };
        var japanese = root.GetProperty("japanese").GetString() ?? "";
        var chinese = root.GetProperty("chinese").GetString() ?? "";
        if (outcome == LocalModelOutcome.Translated &&
            (string.IsNullOrWhiteSpace(japanese) || string.IsNullOrWhiteSpace(chinese)))
            throw new InvalidDataException("本地识别或翻译返回空文本；不会使用原文替代译文。");
        if (outcome != LocalModelOutcome.Translated && (japanese.Length != 0 || chinese.Length != 0))
            throw new InvalidDataException("本地模型跳过状态包含字幕，拒绝显示不一致结果。");
        var diagnostics = root.GetProperty("diagnostics");
        if (diagnostics.GetProperty("sample_rate").GetInt32() != 16000)
            throw new InvalidDataException("本地模型诊断采样率不是 16 kHz。");
        return new LocalModelResult(
            japanese, chinese,
            root.GetProperty("asr_seconds").GetDouble(),
            root.GetProperty("mt_seconds").GetDouble(),
            root.GetProperty("working_set_mb").GetInt64(), outcome,
            new LocalAudioDiagnostics(
                diagnostics.GetProperty("duration_seconds").GetDouble(),
                diagnostics.GetProperty("rms").GetDouble(),
                diagnostics.GetProperty("peak").GetDouble(),
                diagnostics.GetProperty("active_ratio").GetDouble(),
                diagnostics.GetProperty("language").GetString() ?? "unreported"));
    }
}

public sealed class LocalModelService : IAsyncDisposable
{
    private Process? _process;
    private Task<string>? _stderr;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    public string? ReadySummary { get; private set; }
    public event Action<string>? Status;
    public event Action<string>? JapaneseRecognized;

    public async Task StartAsync(
        string python,
        string worker,
        string asrModel,
        string translationModel,
        CancellationToken token)
    {
        if (!File.Exists(python) || !File.Exists(worker) ||
            !File.Exists(Path.Combine(asrModel, "conv_frontend.onnx")) ||
            !File.Exists(Path.Combine(asrModel, "encoder.int8.onnx")) ||
            !File.Exists(Path.Combine(asrModel, "decoder.int8.onnx")) ||
            !File.Exists(Path.Combine(translationModel, "model.bin")))
        {
            throw new FileNotFoundException("找不到指定的 Qwen3-ASR/LMT 模型文件、工作进程或隔离 Python。");
        }

        var info = new ProcessStartInfo(python)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        info.Environment["PYTHONUTF8"] = "1";
        info.Environment["HF_HUB_OFFLINE"] = "1";
        info.Environment["TRANSFORMERS_OFFLINE"] = "1";
        info.ArgumentList.Add("-u");
        info.ArgumentList.Add(worker);
        info.ArgumentList.Add(asrModel);
        info.ArgumentList.Add(translationModel);
        _process = Process.Start(info) ?? throw new InvalidOperationException("无法启动本地模型工作进程。");
        _stderr = _process.StandardError.ReadToEndAsync();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(180));
        try
        {
            while (true)
            {
                using var message = await ReadAsync(timeout.Token);
                var root = message.RootElement;
                if (root.TryGetProperty("status", out var status))
                {
                    Status?.Invoke(status.GetString() ?? "正在加载本地模型…");
                    continue;
                }
                if (root.TryGetProperty("error", out var error))
                    throw new InvalidOperationException(error.GetString());
                if (!root.TryGetProperty("ready", out var ready) || !ready.GetBoolean())
                    throw new InvalidDataException("本地模型工作进程返回了无法识别的初始化响应。");

                var asrLoad = root.GetProperty("asr_load_seconds").GetDouble();
                var mtLoad = root.GetProperty("mt_load_seconds").GetDouble();
                var workingSet = root.GetProperty("working_set_mb").GetInt64();
                ReadySummary =
                    $"Qwen3-ASR {asrLoad:0.0}s + LMT {mtLoad:0.0}s；工作集约 {workingSet} MB";
                Status?.Invoke($"本地模型已就绪（{ReadySummary}）。");
                return;
            }
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async Task<LocalModelResult> TranscribeAndTranslateAsync(string audioPath, CancellationToken token)
    {
        if (_process is null || _process.HasExited)
            throw new InvalidOperationException("本地模型工作进程未运行。");

        await _requestLock.WaitAsync(token);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(120));
            using var registration = timeout.Token.Register(KillProcess);
            var request = JsonSerializer.Serialize(new { op = "transcribe_translate", audio_path = audioPath });
            await _process.StandardInput.WriteLineAsync(request.AsMemory(), timeout.Token);
            await _process.StandardInput.FlushAsync(timeout.Token);
            using var response = await ReadAsync(timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();

            if (response.RootElement.TryGetProperty("japanese", out var japaneseText) &&
                !string.IsNullOrWhiteSpace(japaneseText.GetString()))
                JapaneseRecognized?.Invoke(japaneseText.GetString()!);
            return LocalModelResult.FromResponse(response.RootElement);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    private async Task<JsonDocument> ReadAsync(CancellationToken token)
    {
        var line = await _process!.StandardOutput.ReadLineAsync(token);
        if (line is null)
        {
            if (!_process.HasExited)
                await _process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(5));
            var detail = _stderr is null ? "" : await _stderr.WaitAsync(TimeSpan.FromSeconds(2));
            throw new InvalidOperationException(
                $"本地模型进程意外退出（代码 {_process.ExitCode}）：{detail[..Math.Min(detail.Length, 1200)]}");
        }
        var document = JsonDocument.Parse(line);
        return document;
    }

    private void KillProcess()
    {
        var process = _process;
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) when (process.HasExited) { }
    }

    public async ValueTask DisposeAsync()
    {
        var process = _process;
        if (process is null) return;
        try
        {
            KillProcess();
            if (!process.HasExited)
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (_stderr is not null)
                await _stderr.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            process.Dispose();
            _process = null;
            _requestLock.Dispose();
        }
    }
}
