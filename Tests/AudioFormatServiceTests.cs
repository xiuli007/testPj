using JapaneseTranscriptionPrototype.Services;
using NAudio.Wave;
using Xunit;

namespace JapaneseTranscriptionPrototype.Tests;

public sealed class AudioFormatServiceTests
{
    [Fact]
    public void ConvertsStereoPcmToMonoSixteenKhzPcm()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "source.wav");
        var converted = Path.Combine(directory, "converted.wav");
        try
        {
            const int sampleRate = 44100;
            const int frameCount = sampleRate / 2;
            var bytes = new byte[frameCount * 2 * sizeof(short)];
            for (var index = 0; index < frameCount; index++)
            {
                var sample = (short)(Math.Sin(index * 2 * Math.PI * 440 / sampleRate) * short.MaxValue / 2);
                BitConverter.GetBytes(sample).CopyTo(bytes, index * 4);
                BitConverter.GetBytes(sample).CopyTo(bytes, index * 4 + 2);
            }

            using (var writer = new WaveFileWriter(source, new WaveFormat(sampleRate, 16, 2)))
                writer.Write(bytes, 0, bytes.Length);

            AudioFormatService.ConvertTo16kMonoPcmWav(source, converted);

            using var reader = new WaveFileReader(converted);
            Assert.Equal(16000, reader.WaveFormat.SampleRate);
            Assert.Equal(16, reader.WaveFormat.BitsPerSample);
            Assert.Equal(1, reader.WaveFormat.Channels);
            Assert.InRange(reader.TotalTime.TotalSeconds, 0.49, 0.51);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
