using JapaneseTranscriptionPrototype.Services;
using Xunit;

namespace JapaneseTranscriptionPrototype.Tests;

public sealed class CaptionHistoryStoreTests
{
    [Fact]
    public async Task AppendsUnicodeCaptionsAndSessionBoundariesWithoutOverwritingExistingFile()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "captions.csv");
            const string existing = "record_type,timestamp,session_id,japanese,chinese\r\nold_record,keep\n";
            await File.WriteAllTextAsync(path, existing, new System.Text.UTF8Encoding(false));
            var store = new CaptionHistoryFile();
            await store.StartSessionAsync(path, DateTimeOffset.Parse("2026-11-17T10:00:00+08:00"));
            await store.AppendCaptionAsync(new CaptionResult(
                "こんにちは、世界\n「字幕」", "你好，世界\n“字幕”", 1.25)
            {
                Timestamp = DateTimeOffset.Parse("2026-11-17T10:00:01+08:00")
            });
            await store.EndSessionAsync(DateTimeOffset.Parse("2026-11-17T10:00:02+08:00"));
            await store.StartSessionAsync(path, DateTimeOffset.Parse("2026-11-18T10:00:00+08:00"));
            await store.AppendCaptionAsync(new CaptionResult("二回目", "第二次", 0.9));
            await store.EndSessionAsync(DateTimeOffset.Parse("2026-11-18T10:00:02+08:00"));

            var bytes = await File.ReadAllBytesAsync(path);
            var text = System.Text.Encoding.UTF8.GetString(bytes);
            Assert.StartsWith(existing, text);
            Assert.Equal(2, text.Split("session_start,").Length - 1);
            Assert.Equal(2, text.Split("session_end,").Length - 1);
            Assert.Contains("2026-11-17T10:00:01.0000000+08:00", text);
            Assert.Contains("\"こんにちは、世界\n「字幕」\"", text);
            Assert.Contains("\"你好，世界\n“字幕”\"", text);
            Assert.Contains("二回目", text);
            Assert.Contains("第二次", text);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CreatesUtf8CsvWithHeaderAndDoesNotAddBom()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "new.csv");
            var store = new CaptionHistoryFile();
            await store.StartSessionAsync(path, DateTimeOffset.Now);
            await store.EndSessionAsync(DateTimeOffset.Now);

            var bytes = await File.ReadAllBytesAsync(path);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
            Assert.StartsWith(
                "record_type,timestamp,session_id,japanese,chinese",
                System.Text.Encoding.UTF8.GetString(bytes));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReportsMissingFolderAndDirectoryAsFileErrors()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var store = new CaptionHistoryFile();
            await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
                store.StartSessionAsync(Path.Combine(directory, "missing", "captions.csv"), DateTimeOffset.Now));
            await Assert.ThrowsAsync<IOException>(() =>
                store.StartSessionAsync(directory, DateTimeOffset.Now));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FailedCaptionAppendCanBeRetriedWithoutDiscardingTheSession()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "captions.csv");
            var store = new CaptionHistoryFile();
            await store.StartSessionAsync(path, DateTimeOffset.Now);
            Directory.Delete(directory, recursive: true);

            await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
                store.AppendCaptionAsync(new CaptionResult("日本語", "中文", 1)));

            Directory.CreateDirectory(directory);
            await store.AppendCaptionAsync(new CaptionResult("日本語", "中文", 1));
            await store.EndSessionAsync(DateTimeOffset.Now);

            var text = await File.ReadAllTextAsync(path, System.Text.Encoding.UTF8);
            Assert.Contains("日本語", text);
            Assert.Contains("中文", text);
            Assert.Contains("session_end,", text);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SeparatesAppendedRowsWhenExistingFileHasNoTrailingNewline()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "captions.csv");
            await File.WriteAllTextAsync(path, "existing data", new System.Text.UTF8Encoding(false));
            var store = new CaptionHistoryFile();
            await store.StartSessionAsync(path, DateTimeOffset.Now);
            await store.EndSessionAsync(DateTimeOffset.Now);

            var text = await File.ReadAllTextAsync(path, System.Text.Encoding.UTF8);
            Assert.StartsWith("existing data\r\nsession_start,", text);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PersistsHistoryPathAndDisabledDefault()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "settings.json");
            var store = new CaptionHistorySettingsStore(path);
            Assert.Equal(CaptionHistorySettings.Default, await store.LoadAsync());

            var settings = new CaptionHistorySettings(true, Path.Combine(directory, "captions.csv"));
            await store.SaveAsync(settings);

            Assert.Equal(settings, await store.LoadAsync());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TimelineFollowPausesWhenReadingOldCaptionsAndResumesAtBottom()
    {
        var state = new TimelineFollowState();
        Assert.True(state.IsFollowingTail);

        state.OnScroll(verticalOffset: 120, scrollableHeight: 500, extentChanged: false);
        Assert.False(state.IsFollowingTail);

        state.OnScroll(verticalOffset: 492, scrollableHeight: 500, extentChanged: false);
        Assert.True(state.IsFollowingTail);

        state.OnScroll(verticalOffset: 0, scrollableHeight: 500, extentChanged: true);
        Assert.True(state.IsFollowingTail);
    }

    [Fact]
    public void ClearingTimelineResumesFollowingNewCaptions()
    {
        var state = new TimelineFollowState();
        state.OnScroll(verticalOffset: 0, scrollableHeight: 500, extentChanged: false);
        Assert.False(state.IsFollowingTail);

        state.Reset();

        Assert.True(state.IsFollowingTail);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"caption-history-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
