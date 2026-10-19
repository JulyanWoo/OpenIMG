using System.Reflection;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Web.WebView2.Wpf;
using OpenIMG.App.Browser;

namespace OpenIMG.Tests;

[Trait("Category", TestCategories.Unit)]
public sealed class GptWebClientDispatcherTests
{
    [Fact]
    public async Task ScriptExecutionReadsCoreWebView2OnTheOwningDispatcher()
    {
        await using var uiThread = await DedicatedUiThread.StartAsync();
        var client = new GptWebClient(NullLogger<GptWebClient>.Instance);
        var webView = await uiThread.Dispatcher.InvokeAsync(() => new WebView2()).Task;
        await uiThread.Dispatcher.InvokeAsync(() => client.AttachWebView(webView)).Task;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => InvokeExecuteScriptAsync(client, "1"));

        Assert.Equal("GPT WebView2 is not initialized.", exception.Message);

        await uiThread.Dispatcher.InvokeAsync(client.DetachAndCancel).Task;
    }

    private static Task<string> InvokeExecuteScriptAsync(GptWebClient client, string script)
    {
        var method = typeof(GptWebClient).GetMethod("ExecuteAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(GptWebClient).FullName, "ExecuteAsync");
        return (Task<string>)(method.Invoke(client, [script])
            ?? throw new InvalidOperationException("GPT script execution returned no task."));
    }

    private sealed class DedicatedUiThread : IAsyncDisposable
    {
        private readonly Thread _thread;

        private DedicatedUiThread()
        {
            DispatcherReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _thread = new Thread(RunDispatcher)
            {
                IsBackground = true,
                Name = "OpenIMG GPT dispatcher test"
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        private TaskCompletionSource<Dispatcher> DispatcherReady { get; }

        public Dispatcher Dispatcher => DispatcherReady.Task.GetAwaiter().GetResult();

        public static async Task<DedicatedUiThread> StartAsync()
        {
            var host = new DedicatedUiThread();
            await host.DispatcherReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return host;
        }

        private void RunDispatcher()
        {
            DispatcherReady.TrySetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        }

        public async ValueTask DisposeAsync()
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return;

            await Dispatcher.InvokeAsync(() => Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send)).Task;
            Assert.True(_thread.Join(TimeSpan.FromSeconds(5)), "The dedicated WPF dispatcher did not stop.");
        }
    }
}
