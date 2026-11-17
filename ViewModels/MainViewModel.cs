using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using JapaneseTranscriptionPrototype.Commands;
using JapaneseTranscriptionPrototype.Models;
using JapaneseTranscriptionPrototype.Services;
using Microsoft.Win32;

namespace JapaneseTranscriptionPrototype.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly AudioCaptureService _audioCaptureService = new();
    private readonly CaptionHistorySettingsStore _captionHistorySettings = new();
    private readonly CaptionHistoryFile _captionHistoryFile = new();
    private readonly Queue<CaptionResult> _unpersistedCaptions = new();
    private CancellationTokenSource? _transcriptionCancellation;
    private TaskCompletionSource? _transcriptionFinished;
    private AudioInputDeviceOption? _selectedAudioInput;
    private string _referenceText = string.Empty;
    private string _transcriptionText = string.Empty;
    private string _status = "就绪。选择输入源并开始本地录音。";
    private string _audioDuration = "—";
    private string _processingTime = "—";
    private string _realTimeCoefficient = "—";
    private string _characterErrorRate = "—";
    private bool _isRecording;
    private bool _isTranscribing;
    private bool _hasRecording;
    private TimeSpan _recordingDuration;
    private LiveCaptionService? _live;
    private TaskCompletionSource? _liveStarted;
    private bool _liveBusy;
    private bool _showOverlay;
    private string _latestJapanese = "等待日语语音…";
    private string _latestChinese = "等待本地翻译…";
    private string _captionStatus = "未开始。所有识别/翻译都在本机运行。";
    private string _pythonPath;
    private string _asrModelPath;
    private string _translationModelPath;
    private bool _devicesLoading;
    private bool _devicesInitialized;
    private string _historyFilePath = string.Empty;
    private string _historyStatus =
        "跨会话历史未启用；字幕仅保留在当前窗口。选择 CSV 文件并开启追加保存，可保留完整历史。";
    private bool _historySavingEnabled;
    private bool _historySessionActive;

    public MainViewModel()
    {
        AudioInputs = [];
        _captionStatus = "正在加载音频设备；窗口已可交互。";
        SelectedAudioInput = AudioInputs.FirstOrDefault(device => device.IsLoopback) ?? AudioInputs.FirstOrDefault();

        RecordCommand = new AsyncRelayCommand(StartRecordingAsync, () => CanStartRecording);
        StopCommand = new AsyncRelayCommand(StopRecordingAsync, () => IsRecording);
        SaveRecordingCommand = new RelayCommand(SaveRecording, () => HasRecording && !IsRecording);
        TranscribeCommand = new AsyncRelayCommand(TranscribeAsync, () => CanTranscribe);
        var root = FindProjectRoot();
        _pythonPath = ResolvePythonPath(root);
        _asrModelPath = @"D:\AI\models\asr-qwen3";
        _translationModelPath = @"D:\AI\models\mt-lmt-high";
        WorkerPath = Path.Combine(root, "local_model_worker.py");
        StartLiveCommand = new AsyncRelayCommand(StartLiveAsync,
            () => SelectedAudioInput is not null && _live is null && !LiveBusy &&
                  !IsRecording && !IsTranscribing && !_devicesLoading);
        StopLiveCommand = new AsyncRelayCommand(() => StopLiveAsync(),
            () => _live is not null || _transcriptionCancellation is not null);
        TestFileCommand = new AsyncRelayCommand(TestFileAsync, () => !LiveBusy && !IsRecording && !IsTranscribing);
        ChooseHistoryFileCommand = new AsyncRelayCommand(() => ChooseHistoryFileAsync(enableAfterSelect: false));
        ToggleHistorySavingCommand = new AsyncRelayCommand(ToggleHistorySavingAsync);
        RefreshDevicesCommand = new AsyncRelayCommand(RefreshDevicesAsync,
            () => _live is null && !LiveBusy && !IsRecording && !IsTranscribing && !_devicesLoading);
        foreach (var command in new[]
                 {
                     RecordCommand, StopCommand, TranscribeCommand, StartLiveCommand,
                     StopLiveCommand, TestFileCommand, RefreshDevicesCommand,
                     ChooseHistoryFileCommand, ToggleHistorySavingCommand
                 })
        {
            command.ExecutionFailed += exception =>
            {
                var message = $"异步操作失败：{exception.Message}";
                Status = message;
                CaptionStatus = message;
            };
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<AudioInputDeviceOption> AudioInputs { get; }

    public AudioInputDeviceOption? SelectedAudioInput
    {
        get => _selectedAudioInput;
        set
        {
            if (SetProperty(ref _selectedAudioInput, value))
            {
                NotifyCommands();
            }
        }
    }

    public string ReferenceText
    {
        get => _referenceText;
        set
        {
            if (SetProperty(ref _referenceText, value))
            {
                UpdateCharacterErrorRate();
            }
        }
    }

    public string TranscriptionText
    {
        get => _transcriptionText;
        private set
        {
            if (SetProperty(ref _transcriptionText, value))
            {
                UpdateCharacterErrorRate();
            }
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string AudioDuration
    {
        get => _audioDuration;
        private set => SetProperty(ref _audioDuration, value);
    }

    public string ProcessingTime
    {
        get => _processingTime;
        private set => SetProperty(ref _processingTime, value);
    }

    public string RealTimeCoefficient
    {
        get => _realTimeCoefficient;
        private set => SetProperty(ref _realTimeCoefficient, value);
    }

    public string CharacterErrorRateText
    {
        get => _characterErrorRate;
        private set => SetProperty(ref _characterErrorRate, value);
    }

    public bool IsRecording
    {
        get => _isRecording;
        private set
        {
            if (SetProperty(ref _isRecording, value))
            {
                NotifyCommands();
            }
        }
    }

    public bool IsTranscribing
    {
        get => _isTranscribing;
        private set
        {
            if (SetProperty(ref _isTranscribing, value))
            {
                NotifyCommands();
            }
        }
    }

    public bool HasRecording
    {
        get => _hasRecording;
        private set
        {
            if (SetProperty(ref _hasRecording, value))
            {
                NotifyCommands();
            }
        }
    }

    public AsyncRelayCommand RecordCommand { get; }

    public AsyncRelayCommand StopCommand { get; }

    public RelayCommand SaveRecordingCommand { get; }

    public AsyncRelayCommand TranscribeCommand { get; }

    public AsyncRelayCommand StartLiveCommand { get; }
    public AsyncRelayCommand StopLiveCommand { get; }
    public AsyncRelayCommand TestFileCommand { get; }
    public AsyncRelayCommand ChooseHistoryFileCommand { get; }
    public AsyncRelayCommand ToggleHistorySavingCommand { get; }
    public AsyncRelayCommand RefreshDevicesCommand { get; }
    public string WorkerPath { get; }
    public ObservableCollection<CaptionResult> Captions { get; } = [];
    public string PythonPath { get => _pythonPath; set => SetProperty(ref _pythonPath, value); }
    public string AsrModelPath { get => _asrModelPath; set => SetProperty(ref _asrModelPath, value); }
    public string TranslationModelPath { get => _translationModelPath; set => SetProperty(ref _translationModelPath, value); }
    public string HistoryFilePath
    {
        get => _historyFilePath;
        private set
        {
            if (SetProperty(ref _historyFilePath, value))
                OnPropertyChanged(nameof(HistoryActionText));
        }
    }
    public string HistoryStatus { get => _historyStatus; private set => SetProperty(ref _historyStatus, value); }
    public int UnpersistedCaptionCount => _unpersistedCaptions.Count;
    public bool HistorySavingEnabled
    {
        get => _historySavingEnabled;
        private set
        {
            if (SetProperty(ref _historySavingEnabled, value))
                OnPropertyChanged(nameof(HistoryActionText));
        }
    }
    public string HistoryActionText => HistorySavingEnabled
        ? "暂停追加保存"
        : string.IsNullOrWhiteSpace(HistoryFilePath) ? "选择文件并开启保存" : "开启追加保存";
    public string LatestJapanese { get => _latestJapanese; private set => SetProperty(ref _latestJapanese, value); }
    public string LatestChinese { get => _latestChinese; private set => SetProperty(ref _latestChinese, value); }
    public string CaptionStatus { get => _captionStatus; private set => SetProperty(ref _captionStatus, value); }
    public bool ShowOverlay { get => _showOverlay; set => SetProperty(ref _showOverlay, value); }
    public bool LiveBusy
    {
        get => _liveBusy;
        private set { if (SetProperty(ref _liveBusy, value)) { OnPropertyChanged(nameof(CanUseManualTools)); NotifyCommands(); } }
    }
    public bool CanEditDevices => !LiveBusy && _live is null && !IsRecording && !IsTranscribing && !_devicesLoading;
    public bool CanUseManualTools => !LiveBusy;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static string FindProjectRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "local_model_worker.py"))) return folder.FullName;
        return AppContext.BaseDirectory;
    }

    private static string ResolvePythonPath(string projectRoot)
    {
        var rootEnvironment = Path.Combine(projectRoot, ".runtime", "Scripts", "python.exe");
        if (File.Exists(rootEnvironment))
            return rootEnvironment;

        var preservedEnvironment = Path.Combine(
            projectRoot, "JapaneseTranscriptionPrototype", ".runtime", "Scripts", "python.exe");
        return File.Exists(preservedEnvironment) ? preservedEnvironment : rootEnvironment;
    }

    private CaptionSettings Settings => new(PythonPath, WorkerPath, AsrModelPath, TranslationModelPath);

    public async Task InitializeAsync()
    {
        await InitializeCaptionHistoryAsync();
        if (!_devicesInitialized)
            await RefreshDevicesAsync();
    }

    private async Task InitializeCaptionHistoryAsync()
    {
        try
        {
            var settings = await _captionHistorySettings.LoadAsync();
            HistoryFilePath = settings.FilePath;
            HistorySavingEnabled = settings.Enabled;
            if (!settings.Enabled)
            {
                UpdateHistoryStatus();
                return;
            }

            if (string.IsNullOrWhiteSpace(settings.FilePath))
                throw new InvalidDataException("历史保存已开启，但设置中没有历史文件路径。");

            await _captionHistoryFile.StartSessionAsync(settings.FilePath, DateTimeOffset.Now);
            _historySessionActive = true;
            UpdateHistoryStatus();
        }
        catch (Exception exception)
        {
            HistoryStatus =
                $"历史记录未能开启，未确认任何记录已保存：{exception.Message}。请重新选择文件后重试。";
        }
    }

    private async Task ChooseHistoryFileAsync(bool enableAfterSelect)
    {
        var dialog = new SaveFileDialog
        {
            Title = "选择跨会话字幕历史文件（仅追加，不覆盖）",
            Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
            AddExtension = true,
            DefaultExt = ".csv",
            OverwritePrompt = false,
            CheckPathExists = true,
            FileName = string.IsNullOrWhiteSpace(HistoryFilePath)
                ? $"JapaneseCaptions-{DateTime.Now:yyyyMMdd}.csv"
                : Path.GetFileName(HistoryFilePath),
            InitialDirectory = Directory.Exists(Path.GetDirectoryName(HistoryFilePath))
                ? Path.GetDirectoryName(HistoryFilePath)
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog() != true)
            return;

        await ChangeHistoryFileAsync(dialog.FileName, HistorySavingEnabled || enableAfterSelect);
    }

    private async Task ChangeHistoryFileAsync(string path, bool enable)
    {
        var normalizedPath = Path.GetFullPath(path);
        try
        {
            if (_historySessionActive)
            {
                await _captionHistoryFile.EndSessionAsync(DateTimeOffset.Now);
                _historySessionActive = false;
            }

            HistoryFilePath = normalizedPath;
            await _captionHistorySettings.SaveAsync(new(enable, normalizedPath));
            HistorySavingEnabled = enable;
            if (enable)
            {
                await _captionHistoryFile.StartSessionAsync(normalizedPath, GetHistorySessionStartTime());
                _historySessionActive = true;
                if (!await FlushUnpersistedCaptionsAsync())
                    return;
            }
            UpdateHistoryStatus();
        }
        catch (Exception exception)
        {
            _historySessionActive = _captionHistoryFile.IsSessionActive;
            HistorySavingEnabled = _historySessionActive;
            HistoryStatus = $"历史文件切换/写入失败，未确认记录已保存：{exception.Message}";
            try
            {
                await _captionHistorySettings.SaveAsync(
                    new(HistorySavingEnabled, HistoryFilePath));
            }
            catch (Exception settingsException)
            {
                HistoryStatus += $"；保存关闭状态也失败：{settingsException.Message}";
            }
        }
    }

    private async Task ToggleHistorySavingAsync()
    {
        if (HistorySavingEnabled)
        {
            try
            {
                if (_historySessionActive)
                {
                    await _captionHistoryFile.EndSessionAsync(DateTimeOffset.Now);
                    _historySessionActive = false;
                }

                HistorySavingEnabled = false;
                await _captionHistorySettings.SaveAsync(new(false, HistoryFilePath));
                UpdateHistoryStatus();
            }
            catch (Exception exception)
            {
                HistoryStatus = $"暂停历史保存失败：{exception.Message}";
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(HistoryFilePath))
        {
            await ChooseHistoryFileAsync(enableAfterSelect: true);
            return;
        }

        try
        {
            await _captionHistorySettings.SaveAsync(new(true, HistoryFilePath));
            await _captionHistoryFile.StartSessionAsync(HistoryFilePath, GetHistorySessionStartTime());
            _historySessionActive = true;
            HistorySavingEnabled = true;
            if (!await FlushUnpersistedCaptionsAsync())
                return;
            UpdateHistoryStatus();
        }
        catch (Exception exception)
        {
            HistorySavingEnabled = false;
            HistoryStatus = $"无法开启历史追加，未确认记录已保存：{exception.Message}";
            try
            {
                await _captionHistorySettings.SaveAsync(new(false, HistoryFilePath));
            }
            catch (Exception settingsException)
            {
                HistoryStatus += $"；保存关闭状态也失败：{settingsException.Message}";
            }
        }
    }

    private async Task AddCaptionAsync(CaptionResult result)
    {
        LatestJapanese = result.Japanese;
        LatestChinese = result.Chinese;
        Captions.Add(result);
        _unpersistedCaptions.Enqueue(result);
        OnPropertyChanged(nameof(UnpersistedCaptionCount));
        CaptionStatus = $"本段识别 + 翻译 {result.Seconds:0.00} 秒；采集窗口最多 6 秒，安静时不识别。";
        if (HistorySavingEnabled && _historySessionActive)
            await FlushUnpersistedCaptionsAsync();
        else
            UpdateHistoryStatus();
    }

    private async Task<bool> FlushUnpersistedCaptionsAsync()
    {
        while (_unpersistedCaptions.TryPeek(out var caption))
        {
            try
            {
                await _captionHistoryFile.AppendCaptionAsync(caption);
                _unpersistedCaptions.Dequeue();
                OnPropertyChanged(nameof(UnpersistedCaptionCount));
            }
            catch (Exception exception)
            {
                HistoryStatus =
                    $"历史追加失败，未确认保存（{_unpersistedCaptions.Count} 条仍在当前窗口）：{exception.Message}。修复路径后重新开启保存可重试。";
                return false;
            }
        }

        UpdateHistoryStatus();
        return true;
    }

    private void UpdateHistoryStatus()
    {
        if (HistorySavingEnabled && _historySessionActive)
        {
            HistoryStatus = $"正在追加到：{HistoryFilePath}（UTF-8 CSV，不覆盖既有记录）。";
            return;
        }

        HistoryStatus = HistorySavingEnabled
            ? $"历史保存已配置但未能开启：{HistoryFilePath}。当前窗口有 {_unpersistedCaptions.Count} 条尚未写入。"
            : string.IsNullOrWhiteSpace(HistoryFilePath)
                ? $"跨会话历史未配置/未启用；当前窗口有 {_unpersistedCaptions.Count} 条未写入，关闭程序后不会保留。"
                : $"跨会话历史已暂停；当前窗口有 {_unpersistedCaptions.Count} 条未写入。文件：{HistoryFilePath}";
    }

    private DateTimeOffset GetHistorySessionStartTime() =>
        _unpersistedCaptions.TryPeek(out var oldest) ? oldest.Timestamp : DateTimeOffset.Now;

    private async Task RefreshDevicesAsync()
    {
        if (_devicesLoading) return;
        _devicesLoading = true;
        OnPropertyChanged(nameof(CanEditDevices));
        NotifyCommands();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var devices = await Task.Run(AudioDeviceService.GetDevices);
            SelectedAudioInput = null;
            AudioInputs.Clear();
            foreach (var device in devices) AudioInputs.Add(device);
            SelectedAudioInput = AudioInputs.FirstOrDefault(device => device.IsLoopback) ?? AudioInputs.FirstOrDefault();
            _devicesInitialized = true;
            CaptionStatus = $"设备列表已就绪（{stopwatch.Elapsed.TotalSeconds:0.00} 秒），请确认与 Teams 的扬声器输出一致。";
        }
        catch (Exception exception)
        {
            SelectedAudioInput = null;
            AudioInputs.Clear();
            CaptionStatus = $"刷新设备失败：{exception.Message}";
        }
        finally
        {
            _devicesLoading = false;
            OnPropertyChanged(nameof(CanEditDevices));
            NotifyCommands();
        }
    }

    private async Task StartLiveAsync()
    {
        LiveBusy = true;
        CaptionStatus = "正在加载本地 Qwen3-ASR 与 LMT 模型…尚未开始采集。";
        LatestJapanese = "等待日语语音…";
        LatestChinese = "等待本地翻译…";
        var service = new LiveCaptionService();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _liveStarted = started;
        _live = service;
        StopLiveCommand.NotifyCanExecuteChanged();
        service.Caption += result => Application.Current.Dispatcher
            .InvokeAsync(() => _live == service ? AddCaptionAsync(result) : Task.CompletedTask)
            .Task.Unwrap();
        service.JapaneseRecognized += text => Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (_live == service)
            {
                LatestJapanese = text;
                TranscriptionText = text;
                LatestChinese = "本段日语已识别，正在本地翻译…";
            }
        });
        service.Status += message => Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (_live == service) CaptionStatus = message;
        });
        service.Failed += message => Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            if (_live == service)
            {
                try { await StopLiveAsync(message); }
                catch (Exception exception) { CaptionStatus = $"停止采集时清理失败：{exception.Message}"; }
            }
        });
        string? failure = null;
        try
        {
            var device = SelectedAudioInput ?? throw new InvalidOperationException("没有可用设备，请刷新设备。");
            await service.StartAsync(device, Settings);
            CaptionStatus = $"采集中：{device.DisplayName}。也会采集同设备其他程序的声音，不是 Teams 专用接口。";
        }
        catch (Exception exception) { failure = $"启动失败：{exception.Message}"; }
        finally { started.TrySetResult(); StopLiveCommand.NotifyCanExecuteChanged(); }
        if (failure is not null && _live == service) await StopLiveAsync(failure);
    }

    private async Task StopLiveAsync(string? error = null)
    {
        var service = _live;
        _live = null;
        if (service is null && _transcriptionCancellation is not null)
        {
            _transcriptionCancellation.Cancel();
            if (_transcriptionFinished is not null)
                await _transcriptionFinished.Task.WaitAsync(TimeSpan.FromSeconds(45));
            CaptionStatus = "文件测试已取消，临时音频已清理。";
            return;
        }
        StopLiveCommand.NotifyCanExecuteChanged();
        Exception? cleanupFailure = null;
        try
        {
            if (service is not null)
            {
                service.Cancel();
                if (_liveStarted is not null)
                    await _liveStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await Task.Run(async () => await service.DisposeAsync());
            }
        }
        catch (Exception exception)
        {
            error = $"{error} 清理失败：{exception.Message}";
            cleanupFailure = exception;
            if (service is not null) _live = service;
        }
        finally
        {
            LiveBusy = false;
            CaptionStatus = error ?? "已停止采集和本地推理，未处理尾段已丢弃，临时音频已清理。";
            if (error is not null) LatestChinese = "翻译不可用（请查看错误状态）。";
        }
        if (cleanupFailure is not null)
            throw new InvalidOperationException(error, cleanupFailure);
    }

    private async Task TestFileAsync()
    {
        var dialog = new OpenFileDialog { Title = "仅选择获准公开或自制非敏感 WAV（不录制桌面）", Filter = "WAV|*.wav" };
        if (dialog.ShowDialog() != true) return;
        LiveBusy = true;
        _transcriptionCancellation = new CancellationTokenSource();
        StopLiveCommand.NotifyCanExecuteChanged();
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _transcriptionFinished = complete;
        var directory = Path.Combine(Path.GetTempPath(), "JapaneseTranscriptionPrototype", Guid.NewGuid().ToString("N"));
        string? recognizedJapanese = null;
        var watch = Stopwatch.StartNew();
        try
        {
            Directory.CreateDirectory(directory);
            CaptionStatus = "测试 WAV：本地识别 → 本地翻译；未启动音频设备。";
            var normalizedAudio = Path.Combine(directory, "input-16k-mono.wav");
            AudioFormatService.ConvertTo16kMonoPcmWav(dialog.FileName, normalizedAudio);
            await using var inference = new LocalModelService();
            inference.Status += message => CaptionStatus = message;
            await inference.StartAsync(
                PythonPath, WorkerPath, AsrModelPath, TranslationModelPath, _transcriptionCancellation.Token);
            inference.JapaneseRecognized += japanese =>
            {
                recognizedJapanese = japanese;
                TranscriptionText = japanese;
                LatestJapanese = japanese;
                LatestChinese = "正在进行本地日→中翻译…";
            };
            var result = await inference.TranscribeAndTranslateAsync(
                normalizedAudio, _transcriptionCancellation.Token);
            if (!result.HasTranslation)
            {
                TranscriptionText = string.Empty;
                LatestJapanese = "本段未产生可显示的日语转写。";
                LatestChinese = "本段未翻译；没有新增字幕。";
                CaptionStatus = result.StatusMessage;
                return;
            }
            recognizedJapanese = result.Japanese;
            TranscriptionText = result.Japanese;
            LatestJapanese = result.Japanese;
            LatestChinese = result.Chinese;
            var elapsed = watch.Elapsed.TotalSeconds;
            await AddCaptionAsync(new CaptionResult(result.Japanese, result.Chinese, result.TotalSeconds));
            CaptionStatus =
                $"WAV 本地识别 {result.AsrSeconds:0.00}s + 日→中翻译 {result.TranslationSeconds:0.00}s，模型加载 {inference.ReadySummary}；未启动音频设备。总计 {elapsed:0.00}s。";
        }
        catch (Exception exception)
        {
            if (recognizedJapanese is null)
            {
                CaptionStatus = $"WAV 测试失败：{exception.Message}";
                LatestChinese = "测试未完成，无有效翻译。";
            }
            else
            {
                CaptionStatus = $"日语识别已完成（{watch.Elapsed.TotalSeconds:0.00} 秒）；本地翻译不可用：{exception.Message}";
                LatestChinese = "本地推理未完成；没有有效中文译文。";
            }
        }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch (Exception exception) { CaptionStatus = $"临时文件清理失败：{exception.Message}"; }
            _transcriptionCancellation.Dispose();
            _transcriptionCancellation = null;
            _transcriptionFinished = null;
            LiveBusy = false;
            complete.TrySetResult();
        }
    }

    private bool CanStartRecording => SelectedAudioInput is not null && !IsRecording && !IsTranscribing &&
                                      !LiveBusy && _live is null;

    private bool CanTranscribe => HasRecording && !IsRecording && !IsTranscribing && !LiveBusy && _live is null;

    private async Task StartRecordingAsync()
    {
        try
        {
            if (SelectedAudioInput is null)
            {
                throw new InvalidOperationException("没有可用的录音设备。请连接麦克风或启用系统播放设备后重试。");
            }

            _audioCaptureService.StartRecording(SelectedAudioInput);
            IsRecording = true;
            HasRecording = false;
            AudioDuration = "录音中";
            Status = $"正在录制：{SelectedAudioInput.DisplayName}";
        }
        catch (Exception exception)
        {
            Status = $"无法开始录音：{exception.Message}";
        }

        await Task.CompletedTask;
    }

    private async Task StopRecordingAsync()
    {
        try
        {
            await _audioCaptureService.StopRecordingAsync();
            _recordingDuration = _audioCaptureService.GetRecordingDuration();
            IsRecording = false;
            HasRecording = _audioCaptureService.RecordingPath is not null;
            AudioDuration = FormatDuration(_recordingDuration);
            Status = HasRecording ? "录音已暂存在本机。转写后会自动清理；也可先手动保存。" : "没有可用录音。";
        }
        catch (Exception exception)
        {
            IsRecording = false;
            HasRecording = false;
            AudioDuration = "—";
            Status = $"停止录音失败，临时录音已清理：{exception.Message}";
        }
    }

    private void SaveRecording()
    {
        var dialog = new SaveFileDialog
        {
            Title = "保存录音到本机",
            Filter = "WAV 音频文件 (*.wav)|*.wav",
            AddExtension = true,
            DefaultExt = ".wav",
            FileName = $"JapaneseRecording-{DateTime.Now:yyyyMMdd-HHmmss}.wav"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            _audioCaptureService.SaveRecording(dialog.FileName);
            Status = $"录音已按你的操作保存到：{dialog.FileName}";
        }
        catch (Exception exception)
        {
            Status = $"保存录音失败：{exception.Message}";
        }
    }

    private async Task TranscribeAsync()
    {
        if (_audioCaptureService.RecordingPath is not { } recordingPath)
        {
            Status = "没有可转写的录音。";
            return;
        }

        IsTranscribing = true;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _transcriptionFinished = completion;
        _transcriptionCancellation = new CancellationTokenSource();
        Status = "正在本机加载 Qwen3-ASR 与 LMT 模型；不会上传音频或文字。";
        var stopwatch = Stopwatch.StartNew();
        var normalizedAudio = Path.Combine(Path.GetDirectoryName(recordingPath)!, $"{Guid.NewGuid():N}-16k-mono.wav");
        string? recognizedJapanese = null;
        try
        {
            AudioFormatService.ConvertTo16kMonoPcmWav(recordingPath, normalizedAudio);
            await using var inference = new LocalModelService();
            inference.Status += message => Status = message;
            await inference.StartAsync(
                PythonPath, WorkerPath, AsrModelPath, TranslationModelPath, _transcriptionCancellation.Token);
            inference.JapaneseRecognized += japanese =>
            {
                recognizedJapanese = japanese;
                TranscriptionText = japanese;
                LatestJapanese = japanese;
                LatestChinese = "正在进行本地日→中翻译…";
            };
            var result = await inference.TranscribeAndTranslateAsync(normalizedAudio, _transcriptionCancellation.Token);
            stopwatch.Stop();
            if (!result.HasTranslation)
            {
                TranscriptionText = string.Empty;
                LatestJapanese = "本段未产生可显示的日语转写。";
                LatestChinese = "本段未翻译；没有新增字幕。";
                Status = result.StatusMessage;
                return;
            }
            TranscriptionText = result.Japanese;
            LatestJapanese = result.Japanese;
            LatestChinese = result.Chinese;
            await AddCaptionAsync(new CaptionResult(result.Japanese, result.Chinese, result.TotalSeconds));
            ProcessingTime = FormatDuration(stopwatch.Elapsed);
            AudioDuration = FormatDuration(_recordingDuration);
            RealTimeCoefficient = stopwatch.Elapsed.TotalSeconds > 0
                ? $"{_recordingDuration.TotalSeconds / stopwatch.Elapsed.TotalSeconds:0.00}×"
                : "—";
            Status = $"本地识别 {result.AsrSeconds:0.00}s + 日→中翻译 {result.TranslationSeconds:0.00}s；{inference.ReadySummary}。临时音频已清理。";
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            LatestChinese = "转写已取消，没有完成翻译。";
            Status = "转写已取消；正在清理临时录音。";
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            LatestChinese = recognizedJapanese is null
                ? "本地识别或翻译失败；没有有效译文。"
                : "日语识别已完成，但本地翻译失败；没有有效译文。";
            Status = $"转写失败：{exception.Message}";
        }
        finally
        {
            try
            {
                if (File.Exists(normalizedAudio)) File.Delete(normalizedAudio);
                _audioCaptureService.DeleteCurrentRecording();
                HasRecording = false;
            }
            catch (Exception exception)
            {
                Status = $"{Status} 临时音频清理失败：{exception.Message}";
            }
            IsTranscribing = false;
            _transcriptionCancellation.Dispose();
            _transcriptionCancellation = null;
            _transcriptionFinished = null;
            completion.TrySetResult();
        }
    }

    private void UpdateCharacterErrorRate()
    {
        CharacterErrorRateText = string.IsNullOrWhiteSpace(ReferenceText)
            ? "未提供参考文本"
            : $"{CharacterErrorRate.Calculate(ReferenceText, TranscriptionText):P2}";
    }

    private void NotifyCommands()
    {
        RecordCommand?.NotifyCanExecuteChanged();
        StopCommand?.NotifyCanExecuteChanged();
        SaveRecordingCommand?.NotifyCanExecuteChanged();
        TranscribeCommand?.NotifyCanExecuteChanged();
        StartLiveCommand?.NotifyCanExecuteChanged();
        StopLiveCommand?.NotifyCanExecuteChanged();
        TestFileCommand?.NotifyCanExecuteChanged();
        RefreshDevicesCommand?.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanEditDevices));
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss\.fff");

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_live is not null) await StopLiveAsync();
        _transcriptionCancellation?.Cancel();
        if (_transcriptionFinished is not null)
        {
            await _transcriptionFinished.Task.WaitAsync(TimeSpan.FromSeconds(45));
        }

        await Task.Run(async () => await _audioCaptureService.DisposeAsync());
        if (_historySessionActive)
        {
            await _captionHistoryFile.EndSessionAsync(DateTimeOffset.Now);
            _historySessionActive = false;
        }
    }
}
