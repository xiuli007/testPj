using System.Diagnostics;
using JapaneseTranscriptionPrototype.Services;
using NAudio.Wave;
using Xunit;
using Xunit.Abstractions;

namespace JapaneseTranscriptionPrototype.Tests;

public sealed class LocalModelIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RunsSuppliedJa1WithLocalModelsWhenOptedIn()
    {
        if (Environment.GetEnvironmentVariable("RUN_LOCAL_MODEL_INTEGRATION") != "1")
        {
            output.WriteLine("Skipped: set RUN_LOCAL_MODEL_INTEGRATION=1 to run the local model test.");
            return;
        }

        var source = Environment.GetEnvironmentVariable("JA1_WAV_PATH")
            ?? @"D:\AI\models\asr-qwen3\test_wavs\ja1.wav";
        var project = FindProjectRoot();
        var python = Environment.GetEnvironmentVariable("LOCAL_MODEL_PYTHON")
            ?? ResolvePythonPath(project);
        var asr = Environment.GetEnvironmentVariable("QWEN3_ASR_PATH")
            ?? @"D:\AI\models\asr-qwen3";
        var translation = Environment.GetEnvironmentVariable("LMT_MODEL_PATH")
            ?? @"D:\AI\models\mt-lmt-high";
        Assert.True(File.Exists(source), $"Authorized local test WAV not found: {source}");

        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "JapaneseTranscriptionPrototype", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var normalized = Path.Combine(temporaryDirectory, "ja1-16k-mono.wav");
        try
        {
            AudioFormatService.ConvertTo16kMonoPcmWav(source, normalized);
            await using var inference = new LocalModelService();
            var coldWatch = Stopwatch.StartNew();
            await inference.StartAsync(python, Path.Combine(project, "local_model_worker.py"), asr, translation, CancellationToken.None);
            var modelLoadSeconds = coldWatch.Elapsed.TotalSeconds;
            var cold = await inference.TranscribeAndTranslateAsync(normalized, CancellationToken.None);
            Assert.True(cold.HasTranslation);
            float[] samples;
            using (var reader = new WaveFileReader(normalized))
            {
                var bytes = new byte[reader.Length];
                reader.ReadExactly(bytes);
                samples = Enumerable.Range(0, bytes.Length / 2)
                    .Select(index => BitConverter.ToInt16(bytes, index * 2) / 32768f).ToArray();
            }
            foreach (var (name, clip) in new[]
                     {
                         ("silence", new float[32000]),
                         ("low-amplitude", samples.Select(sample => sample * 0.001f).ToArray()),
                         ("boundary-0.5s", samples.Take(8000).ToArray()),
                         ("energetic-tone", Enumerable.Range(0, 16000)
                             .Select(index => (float)(0.05 * Math.Sin(index * 2 * Math.PI * 440 / 16000))).ToArray())
                     })
            {
                var clipPath = Path.Combine(temporaryDirectory, $"{name}.wav");
                WriteClip(clipPath, clip);
                var result = await inference.TranscribeAndTranslateAsync(clipPath, CancellationToken.None);
                output.WriteLine($"{name}: {result.Outcome}; {result.Diagnostics}; ASR {result.AsrSeconds:0.00}s; MT {result.TranslationSeconds:0.00}s");
                if (name == "silence")
                {
                    Assert.Equal(LocalModelOutcome.NoSpeech, result.Outcome);
                    Assert.Empty(result.Japanese);
                    Assert.Empty(result.Chinese);
                    Assert.Equal(0, result.TranslationSeconds);
                }
                if (!result.HasTranslation)
                {
                    Assert.Empty(result.Japanese);
                    Assert.Empty(result.Chinese);
                    Assert.Equal(0, result.TranslationSeconds);
                }
            }
            var invalidFormat = Path.Combine(temporaryDirectory, "invalid-rate.wav");
            using (var writer = new WaveFileWriter(invalidFormat, new WaveFormat(8000, 16, 1)))
                writer.Write(new byte[16000], 0, 16000);
            var formatError = await Assert.ThrowsAsync<InvalidOperationException>(
                () => inference.TranscribeAndTranslateAsync(invalidFormat, CancellationToken.None));
            Assert.Contains("16 kHz", formatError.Message);
            output.WriteLine("Invalid sample rate: explicit error, not classified as silence.");
            var warm = await inference.TranscribeAndTranslateAsync(normalized, CancellationToken.None);
            Assert.True(warm.HasTranslation);
            var totalSeconds = coldWatch.Elapsed.TotalSeconds;

            output.WriteLine($"Model load (cold): {modelLoadSeconds:0.00}s; {inference.ReadySummary}");
            output.WriteLine($"First inference: ASR {cold.AsrSeconds:0.00}s + MT {cold.TranslationSeconds:0.00}s");
            output.WriteLine($"Warm inference: ASR {warm.AsrSeconds:0.00}s + MT {warm.TranslationSeconds:0.00}s");
            output.WriteLine($"Total sequence elapsed (including synthetic/boundary clips): {totalSeconds:0.00}s");
            output.WriteLine($"Worker-reported working set after warm inference: {warm.WorkingSetMegabytes} MB");
            output.WriteLine($"Japanese: {cold.Japanese}");
            output.WriteLine($"Chinese: {cold.Chinese}");

            Assert.False(string.IsNullOrWhiteSpace(cold.Japanese));
            Assert.False(string.IsNullOrWhiteSpace(cold.Chinese));
            Assert.Equal(cold.Japanese, warm.Japanese);
            Assert.Equal(cold.Chinese, warm.Chinese);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
                Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static void WriteClip(string path, float[] samples)
    {
        using var writer = new WaveFileWriter(path, new WaveFormat(16000, 16, 1));
        var bytes = new byte[samples.Length * 2];
        for (var index = 0; index < samples.Length; index++)
            BitConverter.GetBytes((short)Math.Round(samples[index] * short.MaxValue)).CopyTo(bytes, index * 2);
        writer.Write(bytes, 0, bytes.Length);
    }

    private static string FindProjectRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "local_model_worker.py")))
                return folder.FullName;
        throw new DirectoryNotFoundException("Cannot locate local_model_worker.py from the test output directory.");
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
}
