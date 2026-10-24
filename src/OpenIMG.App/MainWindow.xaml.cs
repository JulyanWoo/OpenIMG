using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Web.WebView2.Core;
using OpenIMG.App.Browser;
using OpenIMG.App.ViewModels;
using OpenIMG.App.Views;
using OpenIMG.Application.Abstractions.AI;
using OpenIMG.Application.Abstractions.Persistence;
using OpenIMG.Application.Abstractions.Storage;
#if OPENIMG_ENGINEERING
using OpenIMG.Application.Calibration;
#endif
using OpenIMG.Application.Configuration;

namespace OpenIMG.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly GptWebClient _gptWebClient;
    private readonly IServiceProvider _serviceProvider;
    private readonly OpenImgOptions _options;
#if OPENIMG_ENGINEERING
    private readonly GptCorrectiveVerificationService? _gptCorrectiveVerification;
    private readonly GptDirectRestorationPilotService? _gptDirectRestorationPilot;
    private readonly GptTwoImageCorrectiveSpikeService? _gptTwoImageCorrectiveSpike;
    private readonly GptPromptVerificationSpikeService? _gptPromptVerificationSpike;
#endif
    private readonly TaskCompletionSource<bool> _webViewsReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _gptNavigationCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
#if OPENIMG_ENGINEERING
    private bool _gptActivationSpikeMode;
    private DateTimeOffset? _gptTabActivatedAt;
#endif
    private DateTimeOffset? _gptWebViewLoadedAt;
    private DateTimeOffset? _gptCoreInitializedAt;
    private DateTimeOffset? _gptNavigationRequestedAt;
    private DateTimeOffset? _gptNavigationCompletedAt;
    private bool? _gptNavigationSucceeded;
    private string? _gptNavigationErrorStatus;
    private bool _closeAfterQueueStops;
    private bool _closeShutdownInProgress;

    private static InvalidOperationException CalibrationDisabled() =>
        new("Engineering calibration features are disabled in this production configuration.");

    public MainWindow(
        MainViewModel viewModel,
        GptWebClient gptWebClient,
        IServiceProvider serviceProvider,
        OpenImgOptions options)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _gptWebClient = gptWebClient;
        _gptWebClient.ResultStageChanged += OnGptResultStageChanged;
        _serviceProvider = serviceProvider;
        _options = options;
#if OPENIMG_ENGINEERING
        if (options.EnableCalibrationFeatures)
        {
            _gptCorrectiveVerification = serviceProvider.GetService<GptCorrectiveVerificationService>();
            _gptDirectRestorationPilot = serviceProvider.GetService<GptDirectRestorationPilotService>();
            _gptTwoImageCorrectiveSpike = serviceProvider.GetService<GptTwoImageCorrectiveSpikeService>();
            _gptPromptVerificationSpike = serviceProvider.GetService<GptPromptVerificationSpikeService>();
        }
#endif

        DataContext = _viewModel;
        GptWebView.Loaded += OnGptWebViewLoaded;
        Loaded += OnMainWindowLoaded;
        Closing += OnMainWindowClosing;
        Closed += OnMainWindowClosed;
    }

    private async void OnMainWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closeAfterQueueStops)
            return;

        // WPF's Closing event is synchronous. Cancelling it here ensures the
        // process does not disappear at the first await while the queue is
        // still writing its interruption/recovery checkpoint.
        e.Cancel = true;
        if (_closeShutdownInProgress)
            return;

        _closeShutdownInProgress = true;
        try
        {
            await _viewModel.StopQueueAsync();

            _gptWebClient.DetachAndCancel();
            GptWebView?.Dispose();
            _viewModel.Dispose();
            _closeAfterQueueStops = true;
            await Dispatcher.InvokeAsync(Close);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Error during MainWindow closing cleanup");
            _closeShutdownInProgress = false;
            MessageBox.Show(
                "OpenIMG no pudo guardar el punto de reanudación de la cola. La ventana seguirá abierta; vuelve a intentar cerrar después de revisar el estado.",
                "No se pudo cerrar OpenIMG de forma segura",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OnMainWindowClosed(object? sender, EventArgs e)
    {
        _gptWebClient.ResultStageChanged -= OnGptResultStageChanged;
        try
        {
            WebView2ProcessHelper.TerminateLingeringWebViewProcesses(_options.GptWebView2ProfilePath);
        }
        catch { }
    }

    private async void OnMainWindowLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // Keep product data initialization independent from the provider
            // panels. The internal GPT command only needs the already visible
            // WebView; waiting for the full image queue initialization could
            // otherwise make a valid authenticated session time out.
#if OPENIMG_ENGINEERING
            var viewModelInitialization = _gptActivationSpikeMode
                ? Task.CompletedTask
                : _viewModel.InitializeAsync();
#else
            var viewModelInitialization = _viewModel.InitializeAsync();
#endif
            await InitializeWebViewAsync();
            _webViewsReady.TrySetResult(true);
            await viewModelInitialization;
        }
        catch (Exception ex)
        {
            _webViewsReady.TrySetException(ex);
            Serilog.Log.Error(ex, "Unhandled exception during MainWindow load");
            MessageBox.Show($"Initialization error: {ex.Message}", "OpenIMG Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

#if OPENIMG_ENGINEERING
    /// <summary>
    /// Runs the internal GPT verification from the already visible product
    /// window. The GPT WebView is the same panel used by the normal app; no
    /// auxiliary verification window or second browser session is created.
    /// </summary>
    public async Task<GptCorrectiveVerificationSummary> RunGptCorrectiveVerificationAsync(
        string repoRoot,
        bool retryFailed = false,
        IReadOnlyCollection<string>? selectedCaseIds = null,
        CancellationToken cancellationToken = default,
        string? outputDirectoryName = null)
    {
        await _webViewsReady.Task.WaitAsync(TimeSpan.FromSeconds(90), cancellationToken);
        return await (_gptCorrectiveVerification ?? throw CalibrationDisabled()).RunAsync(repoRoot, retryFailed, cancellationToken, selectedCaseIds, outputDirectoryName);
    }

    public async Task<GptDirectRestorationPilotSummary> RunGptDirectRestorationPilotAsync(
        string repoRoot,
        CancellationToken cancellationToken = default)
    {
        await _webViewsReady.Task.WaitAsync(TimeSpan.FromSeconds(90), cancellationToken);
        await RealizeProviderTabAsync(0);
        return await (_gptDirectRestorationPilot ?? throw CalibrationDisabled()).RunAsync(repoRoot, cancellationToken: cancellationToken);
    }

    /// <summary>Initializes only the normal GPT panel for the GPT-direct pilot.</summary>
    public void PrepareForGptDirectRestorationPilot()
    {
        _gptActivationSpikeMode = true;
        _gptTabActivatedAt = DateTimeOffset.UtcNow;
        ProviderTabs.SelectedIndex = 0;
    }

    /// <summary>
    /// Selects the existing GPT provider tab before WPF realizes the WebView.
    /// This is used only by the internal corrective command.
    /// </summary>
    public void PrepareForGptCorrectiveVerification()
    {
        ProviderTabs.SelectedIndex = 0;
    }

    /// <summary>
    /// Prepares the normal product window for the bounded WebView2 activation
    /// spike using the same GPT WebView instance as the normal product.
    /// </summary>
    public void PrepareForGptActivationSpike()
    {
        _gptActivationSpikeMode = true;
        _gptTabActivatedAt = DateTimeOffset.UtcNow;
        ProviderTabs.SelectedIndex = 0;
    }

    /// <summary>Prepares the normal product window for one GPT two-image spike.</summary>
    public void PrepareForGptTwoImageCorrectiveSpike()
    {
        _gptActivationSpikeMode = true;
        _gptTabActivatedAt = DateTimeOffset.UtcNow;
        ProviderTabs.SelectedIndex = 0;
    }

    public async Task<GptTwoImageCorrectiveSpikeResult> RunGptTwoImageCorrectiveSpikeAsync(
        string repoRoot,
        CancellationToken cancellationToken = default)
    {
        await _webViewsReady.Task.WaitAsync(TimeSpan.FromSeconds(90), cancellationToken);
        await RealizeProviderTabAsync(0);
        return await (_gptTwoImageCorrectiveSpike ?? throw CalibrationDisabled()).RunAsync(repoRoot, cancellationToken);
    }

    public async Task<string> InspectExistingGptConversationAsync(string conversationPath, string screenshotPath, bool openViewer, CancellationToken cancellationToken = default)
    {
        await _webViewsReady.Task.WaitAsync(TimeSpan.FromSeconds(90), cancellationToken);
        await RealizeProviderTabAsync(0);
        if (openViewer)
            return await _gptWebClient.InspectExistingGeneratedImageViewerAsync(conversationPath, screenshotPath, cancellationToken);
        return await _gptWebClient.InspectExistingConversationAsync(conversationPath, screenshotPath, cancellationToken);
    }

    public async Task<string> RecoverExistingGptImageAsync(string conversationPath, string operationId, string destinationPath, CancellationToken cancellationToken = default)
    {
        await _webViewsReady.Task.WaitAsync(TimeSpan.FromSeconds(90), cancellationToken);
        await RealizeProviderTabAsync(0);
        return await _gptWebClient.RecoverExistingGeneratedImageAsync(conversationPath, operationId, destinationPath, cancellationToken);
    }

    public async Task<GptPreUploadCheckResult> RunGptPreUploadCheckAsync(
        string repoRoot,
        CancellationToken cancellationToken = default)
    {
        await _webViewsReady.Task.WaitAsync(TimeSpan.FromSeconds(90), cancellationToken);
        await RealizeProviderTabAsync(0);
        return await (_gptTwoImageCorrectiveSpike ?? throw CalibrationDisabled()).RunPreUploadCheckAsync(repoRoot, cancellationToken);
    }

    public async Task<GptAttachmentUploadSpikeResult> RunGptAttachmentUploadContractSpikeAsync(
        string repoRoot,
        CancellationToken cancellationToken = default)
    {
        await _webViewsReady.Task.WaitAsync(TimeSpan.FromSeconds(90), cancellationToken);
        await RealizeProviderTabAsync(0);
        return await (_gptTwoImageCorrectiveSpike ?? throw CalibrationDisabled()).RunAttachmentUploadContractSpikeAsync(repoRoot, cancellationToken);
    }

    public async Task<GptTwoAttachmentUploadSpikeResult> RunGptTwoAttachmentUploadSpikeAsync(
        string repoRoot,
        CancellationToken cancellationToken = default)
    {
        await _webViewsReady.Task.WaitAsync(TimeSpan.FromSeconds(90), cancellationToken);
        await RealizeProviderTabAsync(0);
        return await (_gptTwoImageCorrectiveSpike ?? throw CalibrationDisabled()).RunTwoAttachmentUploadSpikeAsync(repoRoot, cancellationToken);
    }

    public async Task<GptPromptVerificationSpikeResult> RunGptPromptVerificationSpikeAsync(
        string repoRoot,
        CancellationToken cancellationToken = default)
    {
        await _webViewsReady.Task.WaitAsync(TimeSpan.FromSeconds(90), cancellationToken);
        await RealizeProviderTabAsync(0);
        return await (_gptPromptVerificationSpike ?? throw CalibrationDisabled()).RunAsync(repoRoot, cancellationToken);
    }

    public async Task RunGptActivationSpikeAsync(string repoRoot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        var outputRoot = Path.Combine(repoRoot, "calibration", "gpt-webview2-activation-spike");
        Directory.CreateDirectory(outputRoot);
        var startedAt = DateTimeOffset.UtcNow;
        var failureStage = "Startup";
        var scriptResults = new Dictionary<string, string>(StringComparer.Ordinal);
        string? currentUrl = null;
        string? documentReadyState = null;
        string? documentTitle = null;
        string authenticationState = "Unknown";
        bool scriptExecutionHealthy = false;
        bool composerDetected = false;
        string? composerDiagnostics = null;

        try
        {
            failureStage = "WebViewReady";
            await _webViewsReady.Task.WaitAsync(TimeSpan.FromSeconds(90), cancellationToken);
            await RealizeProviderTabAsync(0);
            _gptTabActivatedAt ??= DateTimeOffset.UtcNow;

            failureStage = "Visibility";
            if (!GptWebView.IsVisible || !GptWebView.IsLoaded || GptWebView.ActualWidth <= 0 || GptWebView.ActualHeight <= 0)
                throw new InvalidOperationException("GPT WebView2 is not visible or has no realized dimensions.");

            failureStage = "CoreWebView2";
            if (GptWebView.CoreWebView2 == null)
                throw new InvalidOperationException("GPT CoreWebView2 is not initialized.");

            failureStage = "Navigation";
            if (_gptNavigationCompleted.Task is { IsCompleted: false })
                await _gptNavigationCompleted.Task.WaitAsync(TimeSpan.FromSeconds(90), cancellationToken);

            failureStage = "ScriptSanity";
            var sanityScripts = new Dictionary<string, string>
            {
                ["document.readyState"] = "document.readyState",
                ["document.title"] = "document.title",
                ["window.location.href"] = "window.location.href",
                ["document.body != null"] = "document.body != null",
                ["document.body.innerText.length"] = "document.body ? document.body.innerText.length : -1"
            };
            foreach (var (name, script) in sanityScripts)
                scriptResults[name] = await ExecuteGptScriptAsync(script, cancellationToken);

            documentReadyState = NormalizeScriptResult(scriptResults["document.readyState"]);
            documentTitle = NormalizeScriptResult(scriptResults["document.title"]);
            currentUrl = NormalizeScriptResult(scriptResults["window.location.href"]);
            scriptExecutionHealthy = scriptResults.Values.All(value => !string.IsNullOrWhiteSpace(value) && !string.Equals(value, "{}", StringComparison.Ordinal));
            if (!scriptExecutionHealthy)
                throw new InvalidOperationException("GPT WebView2 scripts returned empty or object-only values.");

            failureStage = "ComposerDetection";
            await ExecuteGptScriptAsync(Browser.GptDomAdapter.HelperScript, cancellationToken);
            composerDiagnostics = NormalizeScriptResult(await ExecuteGptScriptAsync("JSON.stringify((function(){var all=Array.from(document.querySelectorAll('textarea, [contenteditable=\"true\"], [role=\"textbox\"]')); var visible=all.filter(function(el){var r=el.getBoundingClientRect(); return r.width>0 && r.height>0;}); var el=visible[0]||null; return {candidateCount:all.length, visibleCandidateCount:visible.length, detectedComposerTag:el?el.tagName:null, detectedRole:el?(el.getAttribute('role')||null):null, detectedContentEditable:el?el.getAttribute('contenteditable')===\"true\":false, detectedAriaLabel:el?(el.getAttribute('aria-label')||el.getAttribute('placeholder')||null):null};})())", cancellationToken));
            composerDetected = composerDiagnostics.Contains("\"visibleCandidateCount\":", StringComparison.Ordinal) && !composerDiagnostics.Contains("\"visibleCandidateCount\":0", StringComparison.Ordinal);

            var loginVisible = NormalizeScriptResult(await ExecuteGptScriptAsync("!!document.querySelector('a[href*=\"/auth/login\"], button[aria-label*=\"log in\" i], button[aria-label*=\"sign in\" i], button[aria-label*=\"iniciar sesión\" i]')", cancellationToken));
            authenticationState = string.Equals(loginVisible, "true", StringComparison.OrdinalIgnoreCase)
                ? "AuthenticationRequired"
                : composerDetected ? "Authenticated" : "Unknown";

            if (!composerDetected)
            {
                failureStage = "ComposerDetection";
                throw new InvalidOperationException("GPT composer was not detected after DOM sanity checks.");
            }
            failureStage = "Completed";
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "GPT WebView2 activation spike failed at {FailureStage}", failureStage);
            if (string.Equals(authenticationState, "AuthenticationRequired", StringComparison.Ordinal))
                failureStage = "Authentication";
        }

        var telemetry = new
        {
            Passed = string.Equals(failureStage, "Completed", StringComparison.Ordinal),
            GptPanelActivated = _gptActivationSpikeMode && ProviderTabs.SelectedIndex == 0,
            WebViewLoaded = GptWebView.IsLoaded,
            IsVisible = GptWebView.IsVisible,
            ActualWidth = GptWebView.ActualWidth,
            ActualHeight = GptWebView.ActualHeight,
            CoreWebView2Initialized = GptWebView.CoreWebView2 != null,
            TabActivatedAt = _gptTabActivatedAt,
            WebViewLoadedAt = _gptWebViewLoadedAt,
            CoreWebView2InitializedAt = _gptCoreInitializedAt,
            NavigationRequestedAt = _gptNavigationRequestedAt,
            NavigationCompletedAt = _gptNavigationCompletedAt,
            NavigationSucceeded = _gptNavigationSucceeded,
            NavigationErrorStatus = _gptNavigationErrorStatus,
            CoreSource = GptWebView.CoreWebView2?.Source,
            CurrentUrl = currentUrl,
            DocumentReadyState = documentReadyState,
            DocumentTitle = documentTitle,
            ScriptResults = scriptResults,
            ScriptExecutionHealthy = scriptExecutionHealthy,
            AuthenticationState = authenticationState,
            ComposerDetected = composerDetected,
            ComposerDiagnostics = composerDiagnostics,
            FailureStage = failureStage,
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow
        };
        var telemetryPath = Path.Combine(outputRoot, "gpt_webview2_activation_spike_telemetry.json");
        await File.WriteAllTextAsync(telemetryPath, JsonSerializer.Serialize(telemetry, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        var reportPath = Path.Combine(outputRoot, "GPT_WEBVIEW2_ACTIVATION_SPIKE_REPORT.md");
        var report = $"# GPT WebView2 Activation Spike\n\n" +
            $"- Result: **{(telemetry.Passed ? "PASS" : "FAIL")}**\n" +
            $"- GptPanelActivated: `{telemetry.GptPanelActivated}`\n" +
            $"- WebViewLoaded: `{telemetry.WebViewLoaded}`\n" +
            $"- CoreWebView2Initialized: `{telemetry.CoreWebView2Initialized}`\n" +
            $"- NavigationSucceeded: `{telemetry.NavigationSucceeded}`\n" +
            $"- DocumentReadyState: `{telemetry.DocumentReadyState}`\n" +
            $"- CurrentUrl: `{telemetry.CurrentUrl}`\n" +
            $"- AuthenticationState: `{telemetry.AuthenticationState}`\n" +
            $"- ScriptExecutionHealthy: `{telemetry.ScriptExecutionHealthy}`\n" +
            $"- ComposerDetected: `{telemetry.ComposerDetected}`\n" +
            $"- FailureStage: `{telemetry.FailureStage}`\n\n" +
            $"Telemetry: `{telemetryPath}`\n";
        await File.WriteAllTextAsync(reportPath, report, cancellationToken);
        Serilog.Log.Information("GPT WebView2 activation spike completed: {Result}; report={ReportPath}", telemetry.Passed ? "PASS" : "FAIL", reportPath);
    }

    /// <summary>
    /// Uses the identical readiness gate consumed by <see cref="GptWebProvider"/>.
    /// It supersedes the earlier direct-DOM activation probe for command use.
    /// </summary>
    public async Task RunSharedGptReadinessSpikeAsync(string repoRoot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        var outputRoot = Path.Combine(repoRoot, "calibration", "gpt-webview2-activation-spike");
        Directory.CreateDirectory(outputRoot);
        var startedAt = DateTimeOffset.UtcNow;

        await _webViewsReady.Task.WaitAsync(TimeSpan.FromSeconds(90), cancellationToken);
        await RealizeProviderTabAsync(0);
        _gptTabActivatedAt ??= DateTimeOffset.UtcNow;
        var readiness = await _gptWebClient.AssessReadinessAsync(cancellationToken);

        var telemetry = new
        {
            Passed = readiness.IsReady,
            GptPanelActivated = readiness.PanelActivated,
            readiness.WebViewLoaded,
            readiness.IsVisible,
            readiness.ActualWidth,
            readiness.ActualHeight,
            readiness.CoreWebView2Initialized,
            TabActivatedAt = _gptTabActivatedAt,
            WebViewLoadedAt = _gptWebViewLoadedAt,
            CoreWebView2InitializedAt = _gptCoreInitializedAt,
            NavigationRequestedAt = _gptNavigationRequestedAt,
            NavigationCompletedAt = _gptNavigationCompletedAt,
            readiness.NavigationSucceeded,
            NavigationErrorStatus = _gptNavigationErrorStatus,
            CoreSource = GptWebView.CoreWebView2?.Source,
            readiness.CurrentUrl,
            readiness.DocumentReadyState,
            readiness.DocumentTitle,
            readiness.BodyAvailable,
            readiness.BodyInnerTextLength,
            readiness.ScriptExecutionHealthy,
            readiness.AuthenticationState,
            readiness.ComposerCandidateCount,
            readiness.VisibleComposerCandidateCount,
            readiness.ComposerDetected,
            readiness.ComposerDiagnostics,
            readiness.FailureStage,
            readiness.FailureReason,
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow
        };

        var telemetryPath = Path.Combine(outputRoot, "gpt_webview2_activation_spike_telemetry.json");
        await File.WriteAllTextAsync(telemetryPath, JsonSerializer.Serialize(telemetry, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        var reportPath = Path.Combine(outputRoot, "GPT_WEBVIEW2_ACTIVATION_SPIKE_REPORT.md");
        var report = $"# GPT WebView2 Shared Readiness Spike\n\n" +
            $"- Result: **{(telemetry.Passed ? "PASS" : "FAIL")}**\n" +
            $"- GptPanelActivated: `{telemetry.GptPanelActivated}`\n" +
            $"- AuthenticationState: `{telemetry.AuthenticationState}`\n" +
            $"- ScriptExecutionHealthy: `{telemetry.ScriptExecutionHealthy}`\n" +
            $"- ComposerDetected: `{telemetry.ComposerDetected}`\n" +
            $"- FailureStage: `{telemetry.FailureStage}`\n\n" +
            $"Telemetry: `{telemetryPath}`\n";
        await File.WriteAllTextAsync(reportPath, report, cancellationToken);
    }

#endif

    private async Task InitializeWebViewAsync()
    {
        var gptProfilePath = _options.GptWebView2ProfilePath;
        Directory.CreateDirectory(gptProfilePath);
        WebView2ProcessHelper.TerminateLingeringWebViewProcesses(gptProfilePath);

        try
        {
            await RealizeProviderTabAsync(0);
            Serilog.Log.Information("Preparing the GPT product WebView2 environment.");
            var gptEnvironment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: gptProfilePath);

            _gptWebClient.AttachWebView(GptWebView);
            await GptWebView.EnsureCoreWebView2Async(gptEnvironment);
            GptWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _gptCoreInitializedAt = DateTimeOffset.UtcNow;
            GptWebView.CoreWebView2.NavigationCompleted -= OnGptNavigationCompleted;
            GptWebView.CoreWebView2.NavigationCompleted += OnGptNavigationCompleted;
            Serilog.Log.Information("GPT WebView2 Core initialized successfully.");

            GptWebView.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
            GptWebView.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
            var currentUri = GptWebView.Source;
            if (GptWebNavigationPolicy.ShouldNavigateToChatGpt(currentUri?.AbsoluteUri))
            {
                _gptNavigationRequestedAt = DateTimeOffset.UtcNow;
                GptWebView.Source = new Uri("https://chatgpt.com/");
                Serilog.Log.Information("GPT navigation requested for chatgpt.com.");
            }
            ProviderTabs.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            if (ex.HResult == unchecked((int)0x800700AA) || ex.Message.Contains("0x800700AA", StringComparison.OrdinalIgnoreCase))
            {
                Serilog.Log.Warning(ex, "GPT WebView2 profile is busy. Cleaning owned WebView2 processes and retrying initialization once.");
                WebView2ProcessHelper.TerminateAllOurWebViewProcesses(gptProfilePath);
                await Task.Delay(1000);
                try
                {
                    await RealizeProviderTabAsync(0);
                    var retryEnvironment = await CoreWebView2Environment.CreateAsync(null, gptProfilePath);
                    _gptWebClient.AttachWebView(GptWebView);
                    await GptWebView.EnsureCoreWebView2Async(retryEnvironment);
                    GptWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                    _gptCoreInitializedAt = DateTimeOffset.UtcNow;
                    GptWebView.CoreWebView2.NavigationCompleted -= OnGptNavigationCompleted;
                    GptWebView.CoreWebView2.NavigationCompleted += OnGptNavigationCompleted;
                    GptWebView.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
                    GptWebView.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
                    if (GptWebNavigationPolicy.ShouldNavigateToChatGpt(GptWebView.Source?.AbsoluteUri))
                    {
                        _gptNavigationRequestedAt = DateTimeOffset.UtcNow;
                        GptWebView.Source = new Uri("https://chatgpt.com/");
                    }
                    ProviderTabs.SelectedIndex = 0;
                    return;
                }
                catch (Exception retryException)
                {
                    Serilog.Log.Error(retryException, "GPT WebView2 retry failed.");
                }
            }

            Serilog.Log.Error(ex, "Error initializing WebView2 browser");
            MessageBox.Show($"Error initializing WebView2 browser: {ex.Message}", "WebView2 Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task RealizeProviderTabAsync(int index)
    {
        ProviderTabs.SelectedIndex = index;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
    }

    private void OnGptWebViewLoaded(object sender, RoutedEventArgs e)
    {
        _gptWebViewLoadedAt ??= DateTimeOffset.UtcNow;
    }

    private void OnGptNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _gptNavigationCompletedAt = DateTimeOffset.UtcNow;
        _gptNavigationSucceeded = e.IsSuccess;
        _gptNavigationErrorStatus = e.WebErrorStatus.ToString();
        _gptNavigationCompleted.TrySetResult(true);
    }

    private async Task<string> ExecuteGptScriptAsync(string script, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (GptWebView.CoreWebView2 == null)
            throw new InvalidOperationException("GPT CoreWebView2 is not initialized.");
        return await Dispatcher.InvokeAsync(() => GptWebView.CoreWebView2.ExecuteScriptAsync(script)).Task.Unwrap();
    }

    private static string NormalizeScriptResult(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        try
        {
            return JsonSerializer.Deserialize<string>(raw) ?? raw;
        }
        catch
        {
            return raw.Trim('"');
        }
    }

    private void OnOpenReviewClicked(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedAsset == null)
        {
            MessageBox.Show("Please select an image first to inspect its review details.", "OpenIMG", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var assetRepo = _serviceProvider.GetRequiredService<IImageAssetRepository>();
        var fileService = _serviceProvider.GetRequiredService<IFileSystemService>();

        var reviewVm = new ReviewViewModel(_viewModel.SelectedAsset.Asset, assetRepo, fileService, _options);
        var reviewDialog = new ReviewDialog(reviewVm)
        {
            Owner = this
        };
        reviewDialog.ShowDialog();
        _viewModel.SelectedAsset.RefreshState();
    }

    private void OnOpenSettingsClicked(object sender, RoutedEventArgs e)
    {
        var projectRepository = _serviceProvider.GetRequiredService<IProjectRepository>();
        var settingsVm = new SettingsViewModel(_options, projectRepository, _viewModel.CurrentProject);
        var settingsDialog = new SettingsDialog(settingsVm)
        {
            Owner = this
        };
        settingsDialog.ShowDialog();
    }

    private async void OnResetAllProcessingStatesClicked(object sender, RoutedEventArgs e)
    {
        var confirmation = MessageBox.Show(
            "Se detendrá la cola actual y se esperará a que la foto activa termine su cierre seguro. " +
            "Después, todas las fotos de esta biblioteca volverán a Pendiente para poder empezar de nuevo.\n\n" +
            "No se borrarán originales, resultados GPT, intentos anteriores ni revisiones humanas. " +
            "Si una operación GPT quedó incompleta, OpenIMG seguirá protegiéndola y pedirá recuperación antes de volver a enviarla.\n\n" +
            "¿Quieres continuar?",
            "Reiniciar estados de procesamiento",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirmation != MessageBoxResult.Yes)
            return;

        try
        {
            await _viewModel.ResetAllProcessingStatesAsync();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Could not reset processing states for the current project.");
            MessageBox.Show(
                "No se pudieron reiniciar los estados. La cola quedó detenida y los originales/resultados se conservaron. " +
                "Revisa el registro de OpenIMG antes de volver a intentarlo.",
                "No se pudo reiniciar",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
