using System.IO;
using JapaneseTranscriptionPrototype.Models;
using NAudio.Wave;

namespace JapaneseTranscriptionPrototype.Services;

public sealed class AudioCaptureService : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly string _workingDirectory = Path.Combine(
        Path.GetTempPath(),
        "JapaneseTranscriptionPrototype",
        Guid.NewGuid().ToString("N"));
    private IWaveIn? _capture;
    private WaveFileWriter? _writer;
    private TaskCompletionSource<Exception?>? _stopCompletion;
    private Exception? _captureError;
    private string? _recordingPath;
    private bool _isRecording;

    public string? RecordingPath => _recordingPath;

    public bool IsRecording => _isRecording;

    public TimeSpan GetRecordingDuration()
    {
        if (_recordingPath is null || !File.Exists(_recordingPath))
        {
            return TimeSpan.Zero;
        }

        using var reader = new WaveFileReader(_recordingPath);
        return reader.TotalTime;
    }

    public void StartRecording(AudioInputDeviceOption input)
    {
        if (_isRecording)
        {
            throw new InvalidOperationException("请先停止当前录音。");
        }

        DeleteCurrentRecording();
        Directory.CreateDirectory(_workingDirectory);
        _recordingPath = Path.Combine(_workingDirectory, $"{Guid.NewGuid():N}.wav");

        try
        {
            _capture = AudioDeviceService.CreateCapture(input);
            _writer = new WaveFileWriter(_recordingPath, _capture.WaveFormat);
            _stopCompletion = new TaskCompletionSource<Exception?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _captureError = null;
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _capture.StartRecording();
            _isRecording = true;
        }
        catch
        {
            ReleaseCapture();
            DeleteCurrentRecording();
            throw;
        }
    }

    public async Task StopRecordingAsync()
    {
        IWaveIn? capture;
        Task<Exception?>? stopped;
        lock (_gate)
        {
            capture = _capture;
            stopped = _stopCompletion?.Task;
        }

        if (capture is null || stopped is null)
        {
            return;
        }

        try
        {
            capture.StopRecording();
            var error = await stopped.WaitAsync(TimeSpan.FromSeconds(10));
            if (error is not null)
            {
                throw new InvalidOperationException($"录音未能正常结束：{error.Message}", error);
            }
        }
        catch
        {
            DeleteCurrentRecording();
            throw;
        }
    }

    public void SaveRecording(string destinationPath)
    {
        if (_recordingPath is null || !File.Exists(_recordingPath) || _isRecording)
        {
            throw new InvalidOperationException("没有可保存的已完成录音。");
        }

        File.Copy(_recordingPath, destinationPath, overwrite: true);
    }

    public void DeleteCurrentRecording()
    {
        if (_recordingPath is not null && File.Exists(_recordingPath))
        {
            File.Delete(_recordingPath);
        }

        _recordingPath = null;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs eventArgs)
    {
        lock (_gate)
        {
            try
            {
                _writer?.Write(eventArgs.Buffer, 0, eventArgs.BytesRecorded);
            }
            catch (Exception exception)
            {
                _captureError = exception;
                _ = Task.Run(() =>
                {
                    try
                    {
                        _capture?.StopRecording();
                    }
                    catch (Exception stopException)
                    {
                        _captureError ??= stopException;
                    }
                });
            }
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs eventArgs)
    {
        Exception? error = eventArgs.Exception ?? _captureError;
        TaskCompletionSource<Exception?>? completion;
        lock (_gate)
        {
            completion = _stopCompletion;
            try
            {
                _writer?.Dispose();
            }
            catch (Exception exception)
            {
                error ??= exception;
            }

            _writer = null;
            try
            {
                _capture?.Dispose();
            }
            catch (Exception exception)
            {
                error ??= exception;
            }

            _capture = null;
            _isRecording = false;
        }

        completion?.TrySetResult(error);
    }

    private void ReleaseCapture()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
            _capture?.Dispose();
            _capture = null;
            _isRecording = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_isRecording)
            {
                await StopRecordingAsync();
            }
        }
        finally
        {
            DeleteCurrentRecording();
            if (Directory.Exists(_workingDirectory))
            {
                Directory.Delete(_workingDirectory, recursive: true);
            }
        }
    }
}
