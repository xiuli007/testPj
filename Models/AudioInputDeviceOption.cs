namespace JapaneseTranscriptionPrototype.Models;

public sealed record AudioInputDeviceOption(string Id, string FriendlyName, bool IsLoopback)
{
    public string DisplayName { get; } =
        $"{(IsLoopback ? "系统播放 · WASAPI Loopback" : "麦克风")} — {FriendlyName}";
}
