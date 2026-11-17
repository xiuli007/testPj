using System.IO;
using System.Threading.Channels;
using JapaneseTranscriptionPrototype.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace JapaneseTranscriptionPrototype.Services;

public sealed record CaptionResult(string Japanese, string Chinese, double Seconds)
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
}

public sealed record CaptionSettings(string Python, string Worker, string AsrModel, string TranslationModel);

public sealed class LiveCaptionService : IAsyncDisposable
{
    private readonly Channel<float[]> _segments = Channel.CreateBounded<float[]>(new BoundedChannelOptions(2)
    {
        FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true
    });
    private readonly CancellationTokenSource _stop = new();
    private readonly LocalModelService _models = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "JapaneseTranscriptionPrototype", Guid.NewGuid().ToString("N"));
    private WasapiCapture? _capture;
    private BufferedWaveProvider? _buffered;
    private ISampleProvider? _resampled;
    private AudioSegmenter? _segmenter;
    private Task? _consumer;
    private TaskCompletionSource? _captureStopped;
    private bool _stopping;
    private bool _captureStarted;
    private readonly object _audioGate = new();
    private DateTime _lastAudio = DateTime.UtcNow;
    private Task? _idleFlush;
    public event Func<CaptionResult, Task>? Caption;
    public event Action<string>? JapaneseRecognized;
    public event Action<string>? Status;
    public event Action<string>? Failed;
    public void Cancel() => _stop.Cancel();

    public async Task StartAsync(AudioInputDeviceOption device, CaptionSettings settings)
    {
        _models.Status += message => Status?.Invoke(message);
        _models.JapaneseRecognized += text => JapaneseRecognized?.Invoke(text);
        await _models.StartAsync(
            settings.Python, settings.Worker, settings.AsrModel, settings.TranslationModel, _stop.Token);
        _stop.Token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_directory);
        _capture = AudioDeviceService.CreateCapture(device);
        _buffered = new BufferedWaveProvider(_capture.WaveFormat)
        {
            ReadFully = false, BufferDuration = TimeSpan.FromSeconds(2), DiscardOnBufferOverflow = false
        };
        ISampleProvider source = _buffered.ToSampleProvider();
        if (source.WaveFormat.Channels == 2)
            source = new StereoToMonoSampleProvider(source) { LeftVolume = 0.5f, RightVolume = 0.5f };
        else if (source.WaveFormat.Channels != 1)
            throw new InvalidDataException("请选择单声道或立体声输出；当前设备为多声道，请在 Windows 中改为立体声。");
        _resampled = new WdlResamplingSampleProvider(source, AudioSegmenter.SampleRate);
        _segmenter = new AudioSegmenter();
        _segmenter.SegmentLimitReached += () =>
            Status?.Invoke("连续音频已达 20 秒上限，已强制分段；可能截断句子。");
        _segmenter.SegmentReady += samples =>
        {
            if (!_segments.Writer.TryWrite(samples))
                Status?.Invoke("处理跟不上播放速度：已丢弃一段新音频以限制排队（最多两段）。");
        };
        _captureStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _capture.DataAvailable += CaptureData;
        _capture.RecordingStopped += (_, args) =>
        {
            if (args.Exception is not null) Fail($"音频设备断开或采集失败：{args.Exception.Message}");
            else if (!_stopping) Fail("音频采集意外停止。请检查设备后重新开始。");
            _captureStopped.TrySetResult();
        };
        _capture.StartRecording();
        _captureStarted = true;
        _consumer = Task.Run(() => ConsumeAsync(settings));
        _idleFlush = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(400));
                while (await timer.WaitForNextTickAsync(_stop.Token))
                    lock (_audioGate)
                        if (DateTime.UtcNow - _lastAudio > TimeSpan.FromSeconds(1))
                            _segmenter.Flush();
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        });
    }

    private void CaptureData(object? sender, WaveInEventArgs args)
    {
        if (_stop.IsCancellationRequested) return;
        try
        {
            lock (_audioGate)
            {
                _lastAudio = DateTime.UtcNow;
                _buffered!.AddSamples(args.Buffer, 0, args.BytesRecorded);
                var samples = new float[4096];
                int count;
                while ((count = _resampled!.Read(samples, 0, samples.Length)) > 0)
                    for (var index = 0; index < count; index++) _segmenter!.Add(samples[index]);
            }
        }
        catch (Exception exception) { Fail($"采集处理失败：{exception.Message}"); }
    }

    private async Task ConsumeAsync(CaptionSettings settings)
    {
        try
        {
            await foreach (var samples in _segments.Reader.ReadAllAsync(_stop.Token))
            {
                var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.wav");
                try
                {
                    WaveFileWriter.CreateWaveFile16(
                        path, new ArraySampleProvider(samples, AudioSegmenter.SampleRate));
                    var result = await _models.TranscribeAndTranslateAsync(path, _stop.Token);
                    _stop.Token.ThrowIfCancellationRequested();
                    if (!result.HasTranslation)
                    {
                        Status?.Invoke(result.StatusMessage);
                        continue;
                    }
                    if (Caption is { } captionHandlers)
                    {
                        var caption = new CaptionResult(
                            result.Japanese, result.Chinese, result.AsrSeconds + result.TranslationSeconds);
                        foreach (Func<CaptionResult, Task> handler in captionHandlers.GetInvocationList())
                            await handler(caption);
                    }
                }
                finally { if (File.Exists(path)) File.Delete(path); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception exception) { Fail($"识别/翻译失败：{exception.Message}"); }
    }

    private void Fail(string message)
    {
        _stop.Cancel();
        Failed?.Invoke(message);
    }

    public async ValueTask DisposeAsync()
    {
        _stopping = true;
        _stop.Cancel();
        _segments.Writer.TryComplete();
        try
        {
            if (_capture is not null && _captureStarted)
            {
                _capture.StopRecording();
                if (_captureStopped is not null)
                    await _captureStopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            if (_consumer is not null) await _consumer.WaitAsync(TimeSpan.FromSeconds(35));
            if (_idleFlush is not null) await _idleFlush.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            _capture?.Dispose();
            await _models.DisposeAsync();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
            _stop.Dispose();
        }
    }

    private sealed class ArraySampleProvider(float[] samples, int sampleRate) : ISampleProvider
    {
        private int _position;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            var available = Math.Min(count, samples.Length - _position);
            Array.Copy(samples, _position, buffer, offset, available);
            _position += available;
            return available;
        }
    }
}
