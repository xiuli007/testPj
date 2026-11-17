using JapaneseTranscriptionPrototype.ViewModels;
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
}
