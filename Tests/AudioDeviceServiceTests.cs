using System.Reflection;
using JapaneseTranscriptionPrototype.Models;
using JapaneseTranscriptionPrototype.Services;
using NAudio.CoreAudioApi;
using Xunit;

namespace JapaneseTranscriptionPrototype.Tests;

public sealed class AudioDeviceServiceTests
{
    [Fact]
    public async Task EnumeratesOnStaAndReturnsComFreeDescriptorsUsableFromOtherThreads()
    {
        var staDevices = await RunOnStaThread(AudioDeviceService.GetDevices);
        var mtaDevices = await Task.Run(AudioDeviceService.GetDevices);

        AssertComFree(staDevices);
        AssertComFree(mtaDevices);
        var labelsReadFromAnotherThread = await Task.Run(
            () => staDevices.Select(device => device.DisplayName).ToArray());
        Assert.Equal(staDevices.Select(device => device.DisplayName), labelsReadFromAnotherThread);
        Assert.Equal(staDevices.Select(device => device.Id).Distinct().Count(), staDevices.Count);
    }

    private static void AssertComFree(IReadOnlyList<AudioInputDeviceOption> devices)
    {
        var descriptorType = typeof(AudioInputDeviceOption);
        Assert.DoesNotContain(
            descriptorType.GetProperties(),
            property => typeof(MMDevice).IsAssignableFrom(property.PropertyType));
        Assert.DoesNotContain(
            descriptorType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public),
            field => typeof(MMDevice).IsAssignableFrom(field.FieldType));
        Assert.All(devices, device =>
        {
            Assert.False(string.IsNullOrWhiteSpace(device.Id));
            Assert.False(string.IsNullOrWhiteSpace(device.FriendlyName));
            Assert.False(string.IsNullOrWhiteSpace(device.DisplayName));
        });
    }

    private static Task<T> RunOnStaThread<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(action());
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "Audio device enumeration test"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
