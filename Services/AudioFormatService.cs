using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace JapaneseTranscriptionPrototype.Services;

public static class AudioFormatService
{
    public static void ConvertTo16kMonoPcmWav(string sourcePath, string destinationPath)
    {
        using var reader = new WaveFileReader(sourcePath);
        ISampleProvider samples = reader.ToSampleProvider();
        if (samples.WaveFormat.Channels == 2)
        {
            samples = new StereoToMonoSampleProvider(samples)
            {
                LeftVolume = 0.5f,
                RightVolume = 0.5f
            };
        }
        else if (samples.WaveFormat.Channels != 1)
        {
            throw new InvalidDataException(
                $"不支持 {samples.WaveFormat.Channels} 声道音频；请选择单声道或立体声音频。");
        }

        var resampled = new WdlResamplingSampleProvider(samples, 16000);
        WaveFileWriter.CreateWaveFile16(destinationPath, resampled);
    }
}
