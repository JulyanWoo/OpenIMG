namespace OpenIMG.Application.Abstractions.AI;

/// <summary>
/// GPT WebView2 capabilities. The application sees evidence and ownership,
/// never DOM selectors.
/// </summary>
public interface IGptWebClient
{
    /// <summary>
    /// Runs the provider's bounded, evidence-producing readiness gate before
    /// any operation is allowed to inspect or upload attachments.
    /// </summary>
    Task<GptWebReadinessResult> AssessReadinessAsync(CancellationToken cancellationToken = default);
    Task<GptAttachmentUploadSpikeResult> UploadSingleImageAsync(string imagePath, CancellationToken cancellationToken = default);
    Task<GptAttachmentUploadSpikeResult> RunSingleImageAttachmentUploadSpikeAsync(string imagePath, CancellationToken cancellationToken = default);
    Task<GptTwoAttachmentUploadSpikeResult> RunTwoImageAttachmentUploadSpikeAsync(string originalPath, string geminiDraftPath, CancellationToken cancellationToken = default);
    Task<GptTwoAttachmentUploadSpikeResult> UploadImagesSequentiallyAsync(string originalPath, string geminiDraftPath, CancellationToken cancellationToken = default);
    Task<bool> EnsureReadyAsync(CancellationToken cancellationToken = default);
    Task<bool> StartNewChatAsync(CancellationToken cancellationToken = default);
    Task<int> GetAttachmentCountAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetAttachmentDiagnosticsAsync(CancellationToken cancellationToken = default);
    Task<bool> UploadImagesAsync(IReadOnlyList<string> imagePaths, CancellationToken cancellationToken = default);
    Task<int> GetUserMessageCountAsync(CancellationToken cancellationToken = default);
    Task<GptSubmissionStateResult> GetSubmissionStateAsync(string expectedPrompt, CancellationToken cancellationToken = default);
    Task<GptPromptSubmissionResult> SubmitPromptOnceAsync(string prompt, int baselineUserMessageCount, int baselineAssistantResponseCount, TimeSpan acknowledgementTimeout, CancellationToken cancellationToken = default);
    Task<GptPromptInsertionVerificationResult> InsertAndVerifyPromptAsync(string compiledPrompt, CancellationToken cancellationToken = default);
    Task<GptCurrentResponseImageResult> WaitForCurrentResponseImageAsync(int baselineResponseCount, TimeSpan timeout, CancellationToken cancellationToken = default);
    Task<int> GetAssistantResponseCountAsync(CancellationToken cancellationToken = default);
    Task<int> GetGeneratedImageCountAsync(CancellationToken cancellationToken = default);
    Task<bool> SendPromptAsync(string prompt, CancellationToken cancellationToken = default);
    Task<bool> WaitForGeneratedImageAsync(int baselineResponseCount, int baselineImageCount, TimeSpan timeout, CancellationToken cancellationToken = default);
    Task<string?> DownloadLatestGeneratedImageAsync(string operationId, string destinationPath, TimeSpan timeout, CancellationToken cancellationToken = default);
    Task<string> GetOperationDiagnosticsJsonAsync(CancellationToken cancellationToken = default);
    Task<IDisposable> AcquireBrowserLockAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Non-sensitive evidence for the hosted GPT WebView2 readiness gate.
/// It intentionally contains no cookies, tokens, account data, or DOM HTML.
/// </summary>
public sealed record GptWebReadinessResult
{
    public bool PanelActivated { get; init; }
    public bool IsVisible { get; init; }
    public double ActualWidth { get; init; }
    public double ActualHeight { get; init; }
    public bool WebViewLoaded { get; init; }
    public bool CoreWebView2Initialized { get; init; }
    public bool NavigationSucceeded { get; init; }
    public string? CurrentUrl { get; init; }
    public string? DocumentReadyState { get; init; }
    public string? DocumentTitle { get; init; }
    public bool BodyAvailable { get; init; }
    public int BodyInnerTextLength { get; init; }
    public bool ScriptExecutionHealthy { get; init; }
    public string AuthenticationState { get; init; } = "Unknown";
    public int ComposerCandidateCount { get; init; }
    public int VisibleComposerCandidateCount { get; init; }
    public bool ComposerDetected { get; init; }
    public string? ComposerDiagnostics { get; init; }
    public string FailureStage { get; init; } = "NotStarted";
    public string? FailureReason { get; init; }

    public bool IsReady =>
        PanelActivated &&
        WebViewLoaded &&
        CoreWebView2Initialized &&
        NavigationSucceeded &&
        ScriptExecutionHealthy &&
        string.Equals(AuthenticationState, "Authenticated", StringComparison.Ordinal) &&
        ComposerDetected &&
        string.Equals(FailureStage, "Completed", StringComparison.Ordinal);
}

/// <summary>Evidence from a single, non-submitting GPT attachment upload spike.</summary>
public sealed record GptAttachmentUploadSpikeResult
{
    public bool ReadinessPassed { get; init; }
    public int? InitialAttachmentCount { get; init; }
    public bool AttachmentUiActivated { get; init; }
    public IReadOnlyList<GptAttachmentButtonDiagnostic> AttachmentButtonCandidates { get; init; } = [];
    public int FileInputCountBefore { get; init; }
    public int FileInputCountAfter { get; init; }
    public IReadOnlyList<GptFileInputDiagnostic> FileInputsBefore { get; init; } = [];
    public IReadOnlyList<GptFileInputDiagnostic> FileInputsAfter { get; init; } = [];
    public int? SelectedInputIndex { get; init; }
    public string? SelectedInputAccept { get; init; }
    public bool? SelectedInputMultiple { get; init; }
    public string? SelectedInputFrame { get; init; }
    public string? NodeIdentifierType { get; init; }
    public string? NodeIdentifierValue { get; init; }
    public string? TestFilePath { get; init; }
    public bool TestFileExists { get; init; }
    public long? TestFileSizeBytes { get; init; }
    public string? TestFileExtension { get; init; }
    public bool? TestFileReadable { get; init; }
    public bool SetFileInputFilesInvoked { get; init; }
    public string? ExactCdpResult { get; init; }
    public string? ExactCdpError { get; init; }
    public IReadOnlyList<string> FrameDiagnostics { get; init; } = [];
    public IReadOnlyList<string> AttachmentDiagnostics { get; init; } = [];
    public int? ObservedAttachmentCount { get; init; }
    public bool AttachmentPreviewDetected { get; init; }
    public string? AttachmentPreviewEvidence { get; init; }
    public bool AttachmentReady { get; init; }
    public string FailureStage { get; init; } = "NotStarted";
    public string? FailureReason { get; init; }
}

/// <summary>Sequential evidence for exactly two attachments; never submits a prompt.</summary>
public sealed record GptTwoAttachmentUploadSpikeResult
{
    public bool ReadinessPassed { get; init; }
    public int? InitialAttachmentCount { get; init; }
    public GptAttachmentUploadSpikeResult? Attachment1 { get; init; }
    public GptAttachmentUploadSpikeResult? Attachment2 { get; init; }
    public int? ObservedAttachmentCount { get; init; }
    public bool AttachmentOrderVerified { get; init; }
    public bool AttachmentOwnershipVerified { get; init; }
    public string FailureStage { get; init; } = "NotStarted";
    public string? FailureReason { get; init; }
}

public sealed record GptPromptSubmissionResult
{
    public bool ComposerDetected { get; init; }
    public bool ComposerHasContent { get; init; }
    public int PromptLength { get; init; }
    public bool Submitted { get; init; }
    public bool ResponseStateChanged { get; init; }
    public DateTimeOffset? SubmittedAt { get; init; }
    public string? CanonicalCompiledSha256 { get; init; }
    public string? CanonicalComposerSha256 { get; init; }
    public bool CanonicalContentMatches { get; init; }
    public bool PromptContentVerified { get; init; }
    public bool InputEventObserved { get; init; }
    public bool SendButtonDetected { get; init; }
    public bool SendButtonEnabled { get; init; }
    public bool SendClickedExactlyOnce { get; init; }
    public bool SubmissionAcknowledged { get; init; }
    public DateTimeOffset? SubmissionAcknowledgedAt { get; init; }
    public string? SubmissionAcknowledgementSignal { get; init; }
    public string? ConversationPathAfterSubmit { get; init; }
    public int BaselineUserMessageCount { get; init; }
    public int UserMessageCountAfterSubmit { get; init; }
    public int BaselineAssistantResponseCount { get; init; }
    public bool ComposerClearedAfterSubmit { get; init; }
    public bool AttachmentsClearedAfterSubmit { get; init; }
    public string? SubmissionDiagnostics { get; init; }
    public string FailureStage { get; init; } = "NotStarted";
    public string? FailureReason { get; init; }
}

public sealed record GptSubmissionStateResult
{
    public string? ConversationPath { get; init; }
    public int UserMessageCount { get; init; }
    public bool LastUserMessageMatchesPrompt { get; init; }
    public bool ComposerEmpty { get; init; }
    public int ComposerAttachmentCount { get; init; }
    public bool StopControlDetected { get; init; }
    public bool SendButtonDisabled { get; init; }
    public string? Diagnostics { get; init; }

    public bool IndicatesNewConversationSubmissionFrom(GptSubmissionStateResult before) =>
        before.ConversationPath == "/" &&
        ConversationPath?.StartsWith("/c/", StringComparison.Ordinal) == true &&
        ComposerEmpty && ComposerAttachmentCount == 0;
}

public sealed record GptPromptInsertionVerificationResult
{
    public string InsertionMethod { get; init; } = "Unknown";
    public bool ComposerDetected { get; init; }
    public bool PromptInserted { get; init; }
    public bool InputEventObserved { get; init; }
    public int CompiledPromptLength { get; init; }
    public int ComposerRawTextLength { get; init; }
    public int ComposerInnerTextLength { get; init; }
    public int ComposerTextContentLength { get; init; }
    public int ComposerInnerHtmlLength { get; init; }
    public string? ComposerNewlineRepresentation { get; init; }
    public int CanonicalCompiledLength { get; init; }
    public int CanonicalComposerLength { get; init; }
    public string? CanonicalCompiledSha256 { get; init; }
    public string? CanonicalComposerSha256 { get; init; }
    public int CanonicalTokenCount { get; init; }
    public bool CanonicalContentMatches { get; init; }
    public bool PromptContentVerified { get; init; }
    public int? FirstMismatchIndex { get; init; }
    public string? MismatchContext { get; init; }
    public bool PrefixMatches { get; init; }
    public bool SuffixMatches { get; init; }
    public int CompiledLineCount { get; init; }
    public int ComposerLineCount { get; init; }
    public bool SendButtonDetected { get; init; }
    public bool SendButtonEnabled { get; init; }
    public string FailureStage { get; init; } = "NotStarted";
    public string? FailureReason { get; init; }
}

public sealed record GptCurrentResponseImageResult
{
    public bool GenerationStarted { get; init; }
    public bool GenerationCompleted { get; init; }
    public bool GeneratedImageDetected { get; init; }
    public bool CurrentAssistantResponseOwned { get; init; }
    public DateTimeOffset? GenerationStartedAt { get; init; }
    public DateTimeOffset? GenerationCompletedAt { get; init; }
    public int AssistantResponseCount { get; init; }
    public int CurrentResponseImageCount { get; init; }
    public string FailureStage { get; init; } = "NotStarted";
    public string? FailureReason { get; init; }
}

public sealed record GptAttachmentButtonDiagnostic
{
    public int Index { get; init; }
    public string Tag { get; init; } = "";
    public string? Role { get; init; }
    public string? AriaLabel { get; init; }
    public string? Title { get; init; }
    public string? Text { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
}

public sealed record GptFileInputDiagnostic
{
    public int Index { get; init; }
    public string? Accept { get; init; }
    public bool Multiple { get; init; }
    public bool Disabled { get; init; }
    public bool Connected { get; init; }
    public bool Visible { get; init; }
    public string? OwnerFrame { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
}
