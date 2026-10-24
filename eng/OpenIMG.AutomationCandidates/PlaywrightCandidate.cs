using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using OpenIMG.Application.Restoration;
using OpenIMG.Core.Enums;

namespace OpenIMG.AutomationCandidates;

internal static class PlaywrightCandidate
{
    // Explicit manual-login utility only. It cannot upload or send a prompt.
    public static async Task OpenForManualAuthenticationAsync()
    {
        Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenIMG", "Playwright", "Browsers"));
        using var playwright = await Playwright.CreateAsync();
        await using var context = await playwright.Chromium.LaunchPersistentContextAsync(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenIMG", "Playwright", "GPT"),
            new() { Headless = false, AcceptDownloads = false });
        var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        page.Close += (_, _) => closed.TrySetResult();
        await page.GotoAsync("https://chatgpt.com/");
        Console.WriteLine("Dedicated OpenIMG profile opened. Log in manually, then close the browser. No generation will be submitted.");
        try { await closed.Task.WaitAsync(TimeSpan.FromMinutes(10)); }
        catch (TimeoutException) { Console.WriteLine("Manual authentication window reached its bounded deadline."); }
    }

    public static async Task<CaseResult> RunAsync(string scenario)
    {
        Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenIMG", "Playwright", "Browsers"));
        using var playwright = await Playwright.CreateAsync();
        var fixture = new OperationFixture();
        var profile = Path.Combine(fixture.Root, "playwright-profile");
        IBrowserContext? context = null;
        var sends = 0;var downloads = 0;var downloaded = false;string? error = null;
        async Task<IPage> OpenAsync(bool recovery, bool sameProfile = true)
        {
            context = await playwright.Chromium.LaunchPersistentContextAsync(
                sameProfile ? profile : Path.Combine(fixture.Root, "fresh-recovery-profile"),
                new() { Headless = false, AcceptDownloads = true, ServiceWorkers = ServiceWorkerPolicy.Block, ViewportSize = new() { Width = 950, Height = 750 } });
            context.SetDefaultTimeout(3000);
            await context.RouteAsync("**/*", async route =>
            {
                var svg = new Uri(route.Request.Url).AbsolutePath.EndsWith(".svg", StringComparison.Ordinal);
                var html = Program.Page(scenario);
                if (recovery) html = html.Replace("if(sessionStorage.prompt)render();",
                    scenario == "failed-route-recovery" ? "history.replaceState(null,'','/');" :
                    "sessionStorage.prompt=" + System.Text.Json.JsonSerializer.Serialize(Program.Prompt) + ";render();", StringComparison.Ordinal);
                await route.FulfillAsync(new() { Status = 200, ContentType = svg ? "image/svg+xml" : "text/html", Body = svg
                    ? "<svg xmlns='http://www.w3.org/2000/svg' width='400' height='300'><rect width='400' height='300' fill='#176c9d'/></svg>" : html });
            });
            var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();
            page.Download += (_, _) => downloads++;
            await page.GotoAsync(recovery ? "https://chatgpt.com/c/operation" : "https://chatgpt.com/");
            return page;
        }
        try
        {
            var page = await OpenAsync(false);
            if (scenario == "existing-profile")
            {
                await page.EvaluateAsync("localStorage.fixtureSession='authenticated'");
                await context!.CloseAsync();
                page = await OpenAsync(false);
                if (await page.EvaluateAsync<string>("localStorage.fixtureSession") != "authenticated")
                    throw new InvalidOperationException("Persistent profile marker was not retained.");
            }
            var adapter = new PlaywrightSemanticAdapter(page);
            var authenticated = await adapter.IsAuthenticatedAsync();
            if (scenario == "sign-in-with-visible-composer")
            {
                if (authenticated) throw new InvalidOperationException("The visible sign-in action was not detected.");
                return new(scenario, true, false, 0, downloads, "AuthenticationRequired", null);
            }
            if (!authenticated) throw new InvalidOperationException("AuthenticationRequired");
            await fixture.Lifecycle.AdvanceAsync(ProviderOperationState.Ready, CancellationToken.None);
            await adapter.UploadAsync(fixture.SourcePath);
            await fixture.Lifecycle.AdvanceAsync(ProviderOperationState.AttachmentsUploaded, CancellationToken.None);
            await adapter.FillAndVerifyAsync(Program.Prompt);
            await fixture.Lifecycle.AdvanceAsync(ProviderOperationState.PromptVerified, CancellationToken.None);
            await fixture.Lifecycle.AdvanceAsync(ProviderOperationState.GenerationMayExist, CancellationToken.None);
            await adapter.SubmitOnceAsync();
            sends++;
            fixture.Context.SendClickedExactlyOnce = true;fixture.Context.PromptSentAt = DateTimeOffset.UtcNow;
            if (scenario == "duplicate-send")
            {
                var blocked = false;
                try { await adapter.SubmitOnceAsync(); }
                catch (InvalidOperationException) { blocked = true; }
                if (!blocked) throw new InvalidOperationException("Duplicate submission was not blocked.");
            }
            await fixture.Lifecycle.AdvanceAsync(ProviderOperationState.GenerationSubmitted, CancellationToken.None);
            var submittedPath = GenerationRecoveryCheckpoint.NormalizeConversationPath(new Uri(page.Url).AbsolutePath)
                ?? throw new InvalidOperationException("ConversationPending");
            await fixture.Lifecycle.BindConversationAsync(submittedPath, CancellationToken.None);
            if (scenario == "restart-after-generation") await adapter.WaitForCandidateAsync();
            if (scenario == "restart-viewer-open")
            {
                await adapter.WaitForCandidateAsync();
                await (await adapter.GetCandidateAsync()).Button.ClickAsync();
            }
            if (scenario is "restart-after-submit" or "restart-after-generation" or "app-restart" or "restart-viewer-open" or "failed-route-recovery")
            {
                await context!.CloseAsync();fixture.Restore();
                page = await OpenAsync(true, sameProfile: scenario != "app-restart");
                adapter = new PlaywrightSemanticAdapter(page);
            }
            await adapter.WaitForCandidateAsync();
            await adapter.VerifyConversationAsync(fixture.Context.ConversationPathAfterSubmit!, Program.Prompt);
            var image = await adapter.GetCandidateAsync();
            await fixture.Lifecycle.OwnCandidateAsync(0, image.Width, image.Height, image.Source, CancellationToken.None);
            await image.Button.ClickAsync();
            await adapter.VerifyViewerAsync(image.Source, fixture.Context.ConversationPathAfterSubmit!);
            await fixture.Lifecycle.AdvanceAsync(ProviderOperationState.ViewerOpened, CancellationToken.None);
            await fixture.Lifecycle.AdvanceAsync(ProviderOperationState.DownloadStarted, CancellationToken.None);
            var download = await page.RunAndWaitForDownloadAsync(async () =>
            {
                await adapter.VerifyViewerAsync(image.Source, fixture.Context.ConversationPathAfterSubmit!);
                await adapter.GetDownloadButton().ClickAsync();
            }, new() { Timeout = 4000 });
            if (scenario == "download-interrupted")
            {
                await context!.CloseAsync();
                throw new IOException("Browser closed before download persistence.");
            }
            if (await download.FailureAsync() is not null) throw new IOException("Native download failed.");
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.Destination)!);
            var pending = fixture.Destination + ".pending";
            await download.SaveAsAsync(pending);
            File.Move(pending, fixture.Destination, overwrite: false);
            await fixture.VerifyFileAsync();downloaded = true;
        }
        catch (Exception ex)
        {
            // Locator exceptions can include page content; the published result keeps only a category.
            error = ex is TimeoutException ? "BoundedLocatorTimeout" : ex is PlaywrightException ? "BrowserOperationFailed" : ex.Message;
            if (sends > 0) await fixture.Lifecycle.AdvanceAsync(ProviderOperationState.NeedsRecovery, CancellationToken.None);
        }
        finally { if (context is not null) await context.CloseAsync(); }
        var expected = Program.ExpectedDownload(scenario);
        return new(scenario, downloaded == expected && sends == 1 && downloads <= 1 &&
            (expected ? downloads == 1 : !File.Exists(fixture.Destination)), downloaded, sends, downloads, fixture.Context.OperationState.ToString(), error);
    }
}

/// <summary>Isolated spike adapter. Role/label locators first; message attributes are bounded fallbacks.</summary>
internal sealed class PlaywrightSemanticAdapter(IPage page)
{
    private bool _submitted;
    private ILocator Composer => page.GetByRole(AriaRole.Textbox, new() { Name = "Message ChatGPT", Exact = true });
    private ILocator Candidates => page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^(Imagen generada|Generated image) [0-9]+$") });
    public async Task<bool> IsAuthenticatedAsync() => await Composer.IsVisibleAsync() &&
        !await page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^(Log in|Sign in|Iniciar sesión)$", RegexOptions.IgnoreCase) }).IsVisibleAsync();
    public async Task UploadAsync(string path)
    {
        var input = page.GetByLabel("Attach image", new() { Exact = true });
        if (await input.CountAsync() == 0)
        {
            await page.GetByRole(AriaRole.Button, new() { Name = "Add photos and files", Exact = true }).ClickAsync();
            await page.GetByText("Upload from computer", new() { Exact = true }).ClickAsync();
            input = page.GetByLabel("Attach image", new() { Exact = true });
        }
        await input.SetInputFilesAsync(path);
    }
    public async Task FillAndVerifyAsync(string prompt)
    {
        await Composer.FillAsync(prompt);
        if (GptPromptTextNormalizer.Canonicalize(await Composer.InnerTextAsync()) != GptPromptTextNormalizer.Canonicalize(prompt))
            throw new InvalidOperationException("PromptVerificationFailed");
    }
    public async Task SubmitOnceAsync()
    {
        if (_submitted) throw new InvalidOperationException("DuplicateSendPrevented");
        _submitted = true;
        await page.GetByRole(AriaRole.Button, new() { Name = "Send prompt", Exact = true }).ClickAsync();
    }
    public async Task WaitForCandidateAsync()
    {
        var candidate = Candidates.First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 3000 });
        var warning = page.GetByRole(AriaRole.Alert).First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 3000 });
        var winner = await Task.WhenAny(candidate, warning);
        try { await winner; }
        finally { _ = ObserveAsync(candidate); _ = ObserveAsync(warning); }
        if (await page.GetByRole(AriaRole.Alert).CountAsync() > 0) throw new InvalidOperationException("ProviderWarning");
    }
    private static async Task ObserveAsync(Task task) { try { await task; } catch (PlaywrightException) { } }
    public async Task VerifyConversationAsync(string expectedPath, string prompt)
    {
        if (GenerationRecoveryCheckpoint.NormalizeConversationPath(new Uri(page.Url).AbsolutePath) != expectedPath)
            throw new InvalidOperationException("OwnershipFailed:DifferentConversation");
        var users = page.Locator("[data-message-author-role=user]");
        if (await users.CountAsync() != 1 || GptPromptTextNormalizer.Canonicalize(await users.Last.InnerTextAsync()) != GptPromptTextNormalizer.Canonicalize(prompt))
            throw new InvalidOperationException("OwnershipFailed:DifferentPrompt");
    }
    public async Task<(ILocator Button, string Source, int Width, int Height)> GetCandidateAsync()
    {
        var button = Candidates.First;var image = button.GetByRole(AriaRole.Img);
        await image.WaitForAsync(new() { State = WaitForSelectorState.Visible });
        await page.WaitForFunctionAsync("img=>img.complete&&img.naturalWidth>120&&img.naturalHeight>120", await image.ElementHandleAsync(), new() { Timeout = 3000 });
        return (button, await image.GetAttributeAsync("src") ?? throw new InvalidDataException("Missing image source."),
            await image.EvaluateAsync<int>("img=>img.naturalWidth"), await image.EvaluateAsync<int>("img=>img.naturalHeight"));
    }
    private ILocator Viewer => page.GetByRole(AriaRole.Dialog, new() { Name = "Generated image viewer", Exact = true });
    public ILocator GetDownloadButton() => Viewer.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^(Download( image)?|Descargar( imagen)?)$", RegexOptions.IgnoreCase) });
    public async Task VerifyViewerAsync(string expectedSource, string conversation)
    {
        await Viewer.WaitForAsync(new() { State = WaitForSelectorState.Visible });
        var path = new Uri(page.Url).AbsolutePath;
        if (path == "/" || path.StartsWith("/c/", StringComparison.Ordinal) && GenerationRecoveryCheckpoint.NormalizeConversationPath(path) != conversation)
            throw new InvalidOperationException("OwnershipFailed:ViewerNavigation");
        if (await page.GetByRole(AriaRole.Alert).CountAsync() > 0) throw new InvalidOperationException("ProviderWarning");
        var images = Viewer.GetByRole(AriaRole.Img);
        if (await images.CountAsync() != 1 || await images.First.GetAttributeAsync("src") != expectedSource)
            throw new InvalidOperationException("OwnershipFailed:DifferentViewerImage");
        if (await GetDownloadButton().CountAsync() != 1) throw new InvalidOperationException("AmbiguousDownloadControl");
    }
}
