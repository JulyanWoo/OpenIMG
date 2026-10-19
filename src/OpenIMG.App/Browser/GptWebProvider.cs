using System.Security.Cryptography;
using System.IO;
using OpenIMG.Application.Abstractions.AI;
using OpenIMG.Application.Abstractions.Persistence;
using OpenIMG.Application.Configuration;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;
using Microsoft.Extensions.Logging;

namespace OpenIMG.App.Browser;

/// <summary>GPT Web image generation used by the normal single-view product pipeline.</summary>
public sealed class GptWebProvider : IImageGenerationProvider, ICorrectiveImageGenerationProvider
{
    private readonly IGptWebClient _client;
    private readonly IProviderUsageRepository _usageRepository;
    private readonly OpenImgOptions _options;
    private readonly ILogger<GptWebProvider> _logger;

    public GptWebProvider(
        IGptWebClient client,
        IProviderUsageRepository usageRepository,
        OpenImgOptions options,
        ILogger<GptWebProvider> logger)
    {
        _client = client;
        _usageRepository = usageRepository;
        _options = options;
        _logger = logger;
    }

    public Task<string> GenerateRestorationAsync(
        string imagePath,
        string restorationPrompt,
        string tempDownloadDirectory,
        CancellationToken cancellationToken = default)
    {
        var context = new GenerationOwnershipContext
        {
            GenerationOperationId = $"gpt_{Guid.NewGuid():N}",
            ExpectedSourcePath = imagePath,
            ProviderName = "GPT"
        };
        return GenerateRestorationScopedAsync(imagePath, restorationPrompt, tempDownloadDirectory, context, cancellationToken);
    }

    public Task<string> GenerateRestorationScopedAsync(
        string imagePath,
        string restorationPrompt,
        string tempDownloadDirectory,
        GenerationOwnershipContext context,
        CancellationToken cancellationToken = default) =>
        GenerateFromImagesAsync([imagePath], restorationPrompt, tempDownloadDirectory, context, cancellationToken);

    public Task<string> GenerateRestorationMultiViewScopedAsync(
        IReadOnlyList<string> imagePaths,
        string restorationPrompt,
        string tempDownloadDirectory,
        GenerationOwnershipContext context,
        CancellationToken cancellationToken = default)
    {
        if (imagePaths is null || imagePaths.Count is < 1 or > 2)
            throw new NotSupportedException("The GPT product provider currently supports one source image; the legacy two-image corrective path remains internal only.");
        return GenerateFromImagesAsync(imagePaths, restorationPrompt, tempDownloadDirectory, context, cancellationToken);
    }

    public Task<string> GenerateCorrectedAsync(
        string originalImagePath,
        string restoredDraftPath,
        string correctivePrompt,
        string tempDownloadDirectory,
        GenerationOwnershipContext context,
        CancellationToken cancellationToken = default) =>
        GenerateFromImagesAsync([originalImagePath, restoredDraftPath], correctivePrompt, tempDownloadDirectory, context, cancellationToken);

    private async Task<string> GenerateFromImagesAsync(
        IReadOnlyList<string> imagePaths,
        string prompt,
        string tempDownloadDirectory,
        GenerationOwnershipContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(imagePaths);
        if (imagePaths.Count is < 1 or > 2)
            throw new ArgumentOutOfRangeException(nameof(imagePaths), "GPT accepts one product source image; at most two are supported by the internal corrective path.");
        foreach (var imagePath in imagePaths)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
            if (!Path.IsPathFullyQualified(imagePath))
                throw new ArgumentException("GPT image paths must be absolute.", nameof(imagePaths));
            if (!File.Exists(imagePath))
                throw new FileNotFoundException("GPT input image does not exist.", imagePath);
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(tempDownloadDirectory);
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.GenerationOperationId))
            throw new ArgumentException("A unique generation operation ID is required.", nameof(context));

        var sourcePath = Path.GetFullPath(imagePaths[0]);
        var secondaryPath = imagePaths.Count == 2 ? Path.GetFullPath(imagePaths[1]) : null;
        context.ProviderName = "GPT";
        context.ExpectedSourcePath = sourcePath;
        context.ExpectedSecondarySourcePath = secondaryPath;
        context.ExpectedAttachmentCount = imagePaths.Count;

        using var browserLock = await _client.AcquireBrowserLockAsync(cancellationToken);
        var readiness = await _client.AssessReadinessAsync(cancellationToken);
        if (!readiness.IsReady)
        {
            context.ProviderReadinessFailureStage = readiness.FailureStage;
            throw new InvalidOperationException($"GPT readiness failed at {readiness.FailureStage}: {readiness.FailureReason ?? "unknown reason"}.");
        }
        context.ProviderReadinessConfirmed = true;

        if (!await _client.StartNewChatAsync(cancellationToken))
            throw new InvalidOperationException("GPT clean conversation could not be established.");
        context.AttachmentCountBeforeUpload = await _client.GetAttachmentCountAsync(cancellationToken);
        if (context.AttachmentCountBeforeUpload != 0)
            throw new InvalidOperationException("GPT clean conversation contained stale attachments.");
        context.FreshChatConfirmed = true;

        var baselineResponses = await _client.GetAssistantResponseCountAsync(cancellationToken);
        var baselineImages = await _client.GetGeneratedImageCountAsync(cancellationToken);
        var baselineUserMessages = await _client.GetUserMessageCountAsync(cancellationToken);
        context.ExistingAssistantResponseCount = baselineResponses;
        context.ExistingGeneratedImageCount = baselineImages;
        context.BaselineUserMessageCount = baselineUserMessages;
        context.BaselineAssistantResponseCount = baselineResponses;

        if (imagePaths.Count == 1)
        {
            var upload = await _client.UploadSingleImageAsync(sourcePath, cancellationToken);
            context.AttachmentCountAfterUpload = upload.ObservedAttachmentCount ?? 0;
            if (!upload.ReadinessPassed || !upload.AttachmentReady || upload.ObservedAttachmentCount != 1)
                throw new InvalidOperationException($"GPT source upload failed at {upload.FailureStage}: {upload.FailureReason ?? "one visible attachment was required"}.");
        }
        else
        {
            var upload = await _client.UploadImagesSequentiallyAsync(sourcePath, secondaryPath!, cancellationToken);
            context.AttachmentCountAfterOriginal = upload.Attachment1?.ObservedAttachmentCount;
            context.AttachmentCountAfterGeminiDraft = upload.Attachment2?.ObservedAttachmentCount;
            if (upload.FailureStage != "Completed" || upload.Attachment1?.AttachmentReady != true ||
                upload.Attachment1.ObservedAttachmentCount != 1 || upload.Attachment2?.AttachmentReady != true ||
                upload.Attachment2.ObservedAttachmentCount != 2 || upload.ObservedAttachmentCount != 2)
                throw new InvalidOperationException($"GPT corrective attachment contract failed at {upload.FailureStage}: {upload.FailureReason ?? "visible attachment counts 1 then 2 were required"}.");
            context.AttachmentCountAfterUpload = upload.ObservedAttachmentCount.Value;
        }

        var insertion = await _client.InsertAndVerifyPromptAsync(prompt, cancellationToken);
        if (!insertion.PromptContentVerified || !insertion.CanonicalContentMatches || !insertion.InputEventObserved ||
            !insertion.SendButtonDetected || !insertion.SendButtonEnabled)
        {
            context.ProviderFailureStage = insertion.FailureStage;
            throw new InvalidOperationException($"GPT prompt verification failed at {insertion.FailureStage}: {insertion.FailureReason ?? "exact canonical prompt and enabled send control were required"}.");
        }
        context.CanonicalCompiledPromptSha256 = insertion.CanonicalCompiledSha256;
        context.CanonicalComposerPromptSha256 = insertion.CanonicalComposerSha256;
        context.CanonicalPromptContentMatches = insertion.CanonicalContentMatches;
        context.PromptContentVerified = insertion.PromptContentVerified;
        context.PromptInputEventObserved = insertion.InputEventObserved;
        context.PromptSendButtonDetected = insertion.SendButtonDetected;
        context.PromptSendButtonEnabled = insertion.SendButtonEnabled;

        var budgetOptions = _options.DualModelRestoration.GptBudget;
        var reservationTime = DateTimeOffset.UtcNow;
        var budget = await _usageRepository.TryReserveAsync(
            "GPT", context.GenerationOperationId, budgetOptions, reservationTime, cancellationToken);
        if (!budget.Allowed)
        {
            var resumeText = budget.NextEligibleAt.HasValue
                ? $" Next eligible time: {budget.NextEligibleAt.Value.ToLocalTime():g}."
                : string.Empty;
            throw new ProviderUsageLimitException(
                $"GPT image generation is paused by the local usage safeguard ({budget.Reason}; {budget.OperationsInWindow}/{budget.EffectiveBudget} in the rolling window; {budget.OperationsInDay}/{budget.EffectiveDailyBudget?.ToString() ?? "unlimited"} in 24 hours).{resumeText}",
                budget.NextEligibleAt);
        }

        // Persist an ambiguity checkpoint immediately before the one allowed
        // Send click. A process crash after this point must not blindly replay
        // the same image generation.
        await PersistOperationStateAsync(context, ProviderOperationState.GenerationMayExist, cancellationToken);
        var submissionTimeout = TimeSpan.FromSeconds(Math.Clamp(_options.SubmissionAcknowledgementTimeoutSeconds, 3, 60));
        var submission = await _client.SubmitPromptOnceAsync(
            prompt, baselineUserMessages, baselineResponses, submissionTimeout, cancellationToken);
        context.CanonicalCompiledPromptSha256 = submission.CanonicalCompiledSha256;
        context.CanonicalComposerPromptSha256 = submission.CanonicalComposerSha256;
        context.CanonicalPromptContentMatches = submission.CanonicalContentMatches;
        context.PromptContentVerified = submission.PromptContentVerified;
        context.PromptInputEventObserved = submission.InputEventObserved;
        context.PromptSendButtonDetected = submission.SendButtonDetected;
        context.PromptSendButtonEnabled = submission.SendButtonEnabled;
        context.SendClickedExactlyOnce = submission.SendClickedExactlyOnce;
        context.SubmissionAcknowledgedAt = submission.SubmissionAcknowledgedAt;
        context.SubmissionAcknowledgementSignal = submission.SubmissionAcknowledgementSignal;
        context.ConversationPathAfterSubmit = submission.ConversationPathAfterSubmit;
        context.SubmissionDiagnostics = submission.SubmissionDiagnostics;
        context.UserMessageCountAfterSubmit = submission.UserMessageCountAfterSubmit;
        context.ComposerClearedAfterSubmit = submission.ComposerClearedAfterSubmit;
        context.AttachmentsClearedAfterSubmit = submission.AttachmentsClearedAfterSubmit;
        if (submission.Submitted)
            context.PromptSentAt = submission.SubmittedAt;
        if (!submission.Submitted || !submission.SubmissionAcknowledged || !submission.SendClickedExactlyOnce)
        {
            if (!submission.SendClickedExactlyOnce)
            {
                await _usageRepository.ReleaseUnsubmittedReservationAsync(
                    "GPT", context.GenerationOperationId, cancellationToken);
                await PersistOperationStateAsync(context, OpenIMG.Core.Enums.ProviderOperationState.GenerationPrepared, cancellationToken);
            }
            context.ProviderFailureStage = submission.FailureStage;
            throw new InvalidOperationException($"GPT prompt submission was not acknowledged at {submission.FailureStage}: {submission.FailureReason ?? "no accepted user message was observed"}.");
        }
        await PersistOperationStateAsync(context, ProviderOperationState.GenerationSubmitted, cancellationToken);

        var generationTimeoutSeconds = context.GenerationTimeoutOverrideSeconds ?? _options.ResponseTimeoutSeconds;
        var imageResult = await _client.WaitForCurrentResponseImageAsync(
            baselineResponses, TimeSpan.FromSeconds(Math.Clamp(generationTimeoutSeconds, 30, 600)), cancellationToken);
        context.GenerationStartedAt = imageResult.GenerationStartedAt;
        context.GenerationCompletedAt = imageResult.GenerationCompletedAt;
        context.NewResponseDetected = imageResult.AssistantResponseCount > baselineResponses;
        context.NewGeneratedImageDetected = imageResult.GeneratedImageDetected;
        context.GeneratedImageCreatedAfterPrompt = imageResult.CurrentAssistantResponseOwned &&
            imageResult.GenerationStartedAt.HasValue && context.PromptSentAt.HasValue &&
            imageResult.GenerationStartedAt.Value >= context.PromptSentAt.Value;
        if (string.Equals(imageResult.FailureStage, "RateLimited", StringComparison.OrdinalIgnoreCase))
            throw new ProviderUsageLimitException(imageResult.FailureReason ?? "GPT reported an image-generation usage limit.");
        if (!imageResult.GenerationStarted || !imageResult.GenerationCompleted || !imageResult.GeneratedImageDetected ||
            !imageResult.CurrentAssistantResponseOwned)
        {
            context.ProviderFailureStage = imageResult.FailureStage;
            throw new TimeoutException($"GPT current-response image was not safely completed at {imageResult.FailureStage}: {imageResult.FailureReason ?? "ownership/completion evidence incomplete"}.");
        }

        var operationDirectory = Path.Combine(tempDownloadDirectory, context.GenerationOperationId);
        Directory.CreateDirectory(operationDirectory);
        var target = Path.Combine(operationDirectory, "gpt-final.png");
        context.DownloadStartedAt = DateTimeOffset.UtcNow;
        var downloaded = await _client.DownloadLatestGeneratedImageAsync(
            context.GenerationOperationId, target, TimeSpan.FromSeconds(_options.DownloadTimeoutSeconds), cancellationToken);
        if (string.IsNullOrWhiteSpace(downloaded) || !File.Exists(downloaded))
            throw new FileNotFoundException("GPT generated-image download was not owned by the current operation.", target);

        context.DownloadCompletedAt = DateTimeOffset.UtcNow;
        context.DownloadOwnedByCurrentOperation = true;
        context.DownloadedTempPath = downloaded;
        context.DownloadedCandidateSha256 = await ComputeSha256Async(downloaded, cancellationToken);
        foreach (var inputPath in imagePaths)
        {
            var inputHash = await ComputeSha256Async(inputPath, cancellationToken);
            if (string.Equals(inputHash, context.DownloadedCandidateSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("GPT returned an image byte-identical to an input instead of a newly generated restoration.");
        }
        await PersistOperationStateAsync(context, ProviderOperationState.CandidateDownloaded, cancellationToken);
        _logger.LogInformation("[{OperationId}] GPT image downloaded and operation-owned at {Path}.", context.GenerationOperationId, downloaded);
        return downloaded;
    }

    private static async Task PersistOperationStateAsync(
        GenerationOwnershipContext context,
        OpenIMG.Core.Enums.ProviderOperationState state,
        CancellationToken cancellationToken)
    {
        context.OperationState = state;
        if (context.PersistOperationStateAsync is not null)
            await context.PersistOperationStateAsync(state, cancellationToken);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }
}
