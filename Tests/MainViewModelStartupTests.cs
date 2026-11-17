using JapaneseTranscriptionPrototype.ViewModels;
using JapaneseTranscriptionPrototype.Services;
using Xunit;

namespace JapaneseTranscriptionPrototype.Tests;

public sealed class MainViewModelStartupTests
{
    [Fact]
    public async Task ConstructorDefersAudioDeviceEnumeration()
    {
        await using var viewModel = new MainViewModel();

        Assert.Empty(viewModel.AudioInputs);
        Assert.Contains("正在加载音频设备", viewModel.CaptionStatus);
        Assert.Equal(@"D:\AI\models\asr-qwen3", viewModel.AsrModelPath);
        Assert.Equal(@"D:\AI\models\mt-lmt-high", viewModel.TranslationModelPath);
    }

    [Fact]
    public async Task ClearKeepsPendingHistoryAndNewCaptionsContinueOnSameTimeline()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            await using var viewModel = CreateViewModel(directory);
            var sharedTimeline = viewModel.Captions;
            var first = new CaptionResult("first japanese", "first chinese", 1);
            var second = new CaptionResult("second japanese", "second chinese", 2);
            await viewModel.AddCaptionAsync(first);
            var status = viewModel.CaptionStatus;
            var historyStatus = viewModel.HistoryStatus;
            Assert.True(viewModel.ClearCaptionsCommand.CanExecute(null));

            viewModel.ClearCaptionsCommand.Execute(null);
            viewModel.ClearCaptionsCommand.Execute(null);

            Assert.Same(sharedTimeline, viewModel.Captions);
            Assert.Empty(sharedTimeline);
            Assert.Empty(viewModel.LatestJapanese);
            Assert.Empty(viewModel.LatestChinese);
            Assert.Equal(1, viewModel.UnpersistedCaptionCount);
            Assert.Equal(status, viewModel.CaptionStatus);
            Assert.Equal(historyStatus, viewModel.HistoryStatus);
            await viewModel.AddCaptionAsync(second);
            Assert.Equal(second, Assert.Single(sharedTimeline));

            var path = Path.Combine(directory, "history.csv");
            await viewModel.ChangeHistoryFileAsync(path, enable: true);
            Assert.Equal(0, viewModel.UnpersistedCaptionCount);
            var csv = await File.ReadAllTextAsync(path);
            Assert.Contains(first.Japanese, csv);
            Assert.Contains(second.Japanese, csv);
            Assert.True(csv.IndexOf(first.Japanese, StringComparison.Ordinal) <
                        csv.IndexOf(second.Japanese, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ClearPreservesCsvActiveSessionAndFailedPendingWrite()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            await using var viewModel = CreateViewModel(directory);
            var path = Path.Combine(directory, "history.csv");
            const string existing = "existing history\n";
            await File.WriteAllTextAsync(path, existing);
            await viewModel.ChangeHistoryFileAsync(path, enable: true);
            await viewModel.AddCaptionAsync(new CaptionResult("saved japanese", "saved chinese", 1));
            var before = await File.ReadAllBytesAsync(path);

            // Exclusively lock only the synthetic test CSV to exercise a failed append.
            await using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                await viewModel.AddCaptionAsync(new CaptionResult("pending japanese", "pending chinese", 1));
                var failure = viewModel.HistoryStatus;
                Assert.Contains("历史追加失败", failure);
                viewModel.ClearCaptionsCommand.Execute(null);
                Assert.Equal(failure, viewModel.HistoryStatus);
                Assert.Equal(1, viewModel.UnpersistedCaptionCount);
            }

            Assert.Equal(before, await File.ReadAllBytesAsync(path));
            Assert.True(viewModel.HistorySavingEnabled);
            await viewModel.AddCaptionAsync(new CaptionResult("new japanese", "new chinese", 1));
            Assert.Equal(0, viewModel.UnpersistedCaptionCount);
            Assert.Equal("new japanese", Assert.Single(viewModel.Captions).Japanese);
            var after = await File.ReadAllTextAsync(path);
            Assert.StartsWith(System.Text.Encoding.UTF8.GetString(before), after);
            Assert.Contains("pending japanese", after);
            Assert.Contains("new japanese", after);
            Assert.Equal(1, after.Split("session_start,").Length - 1);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static MainViewModel CreateViewModel(string directory) =>
        new(new CaptionHistorySettingsStore(Path.Combine(directory, "settings.json")));

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"caption-clear-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
