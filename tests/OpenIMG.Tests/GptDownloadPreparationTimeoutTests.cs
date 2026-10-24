using OpenIMG.App.Browser;

namespace OpenIMG.Tests;

[Trait("Category", TestCategories.Unit)]
public sealed class GptDownloadPreparationTimeoutTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnresponsiveBrowserQueryCannotOutlivePreparationDeadlineOrCancellation(bool cancel)
    {
        var query = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        if (cancel) cancellation.CancelAfter(100);
        var session = new GptGeneratedImageDownloadSession("/c/operation", 0, "prompt");
        var preparation = session.PrepareAsync((_, _) => query.Task,
            cancel ? TimeSpan.FromSeconds(10) : TimeSpan.FromMilliseconds(100), cancellation.Token);
        try
        {
            Assert.Same(preparation, await Task.WhenAny(preparation, Task.Delay(1500)));
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparation);
            else await Assert.ThrowsAsync<TimeoutException>(() => preparation);
        }
        finally
        {
            query.TrySetCanceled();
            try { await preparation; } catch (Exception) { }
        }
    }
}
