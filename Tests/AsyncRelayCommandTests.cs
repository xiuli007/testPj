using JapaneseTranscriptionPrototype.Commands;
using Xunit;

namespace JapaneseTranscriptionPrototype.Tests;

public sealed class AsyncRelayCommandTests
{
    [Fact]
    public async Task ReportsExecutionFailuresInsteadOfLeakingAsyncVoidExceptions()
    {
        var expected = new InvalidOperationException("test failure");
        var command = new AsyncRelayCommand(() => Task.FromException(expected));
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        command.ExecutionFailed += exception => reported.TrySetResult(exception);

        command.Execute(null);

        Assert.Same(expected, await reported.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }
}
