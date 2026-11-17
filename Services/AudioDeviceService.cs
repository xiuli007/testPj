using JapaneseTranscriptionPrototype.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace JapaneseTranscriptionPrototype.Services;

public static class AudioDeviceService
{
    public static IReadOnlyList<AudioInputDeviceOption> GetDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = new List<AudioInputDeviceOption>();
        AddDevices(enumerator, devices, DataFlow.Capture, isLoopback: false);
        AddDevices(enumerator, devices, DataFlow.Render, isLoopback: true);
        return devices;
    }

    public static WasapiCapture CreateCapture(AudioInputDeviceOption input)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(input.Id);
        return input.IsLoopback
            ? new WasapiLoopbackCapture(device)
            : new WasapiCapture(device);
    }

    private static void AddDevices(
        MMDeviceEnumerator enumerator,
        ICollection<AudioInputDeviceOption> devices,
        DataFlow flow,
        bool isLoopback)
    {
        var endpoints = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
        try
        {
            foreach (var device in endpoints)
                devices.Add(new AudioInputDeviceOption(device.ID, device.FriendlyName, isLoopback));
        }
        finally
        {
            foreach (var device in endpoints)
                device.Dispose();
        }
    }
}
