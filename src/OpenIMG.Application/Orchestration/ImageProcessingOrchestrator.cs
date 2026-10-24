using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenIMG.Application.Abstractions.AI;
using OpenIMG.Application.Abstractions.Orchestration;
using OpenIMG.Application.Abstractions.Persistence;
using OpenIMG.Application.Abstractions.Storage;
using OpenIMG.Application.Abstractions.Validation;
using OpenIMG.Application.Configuration;
using OpenIMG.Application.Parsing;
using OpenIMG.Application.Prompts;
using OpenIMG.Application.Validation;
using OpenIMG.Application.Policies;
using OpenIMG.Application.Restoration;
using OpenIMG.Core.Entities;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;

using OpenIMG.Application.Abstractions.Analysis;
using OpenIMG.Application.Analysis;

namespace OpenIMG.Application.Orchestration;

public class ImageProcessingOrchestrator : IImageProcessingOrchestrator
{
    private readonly IImageGenerationProvider _generationProvider;
    private readonly ILocalImageValidator _localValidator;
    private readonly IFileSystemService _fileService;
    private readonly IImageAssetRepository _assetRepository;
    private readonly ValidationDecisionEngine _decisionEngine;
    private readonly RetryPolicy _retryPolicy;
    private readonly OpenImgOptions _options;
    private readonly ILogger<ImageProcessingOrchestrator> _logger;
    private readonly ILocalAnalysisEngine? _localAnalysisEngine;
    private readonly ILocalToFurnitureAnalysisAdapter _localToFurnitureAdapter;
    private readonly IFurnitureAnalysisContractAdapter _analysisContractAdapter;
    private readonly ILocalPromptCompactor _localPromptCompactor;

    public ImageProcessingOrchestrator(
        IImageGenerationProvider generationProvider,
        ILocalImageValidator localValidator,
        IFileSystemService fileService,
        IImageAssetRepository assetRepository,
        ValidationDecisionEngine decisionEngine,
        OpenImgOptions options,
        ILogger<ImageProcessingOrchestrator> logger,
        RetryPolicy? retryPolicy = null,
        ILocalAnalysisEngine? localAnalysisEngine = null,
        ILocalToFurnitureAnalysisAdapter? localToFurnitureAdapter = null,
        ILocalPromptCompactor? localPromptCompactor = null,
        IFurnitureAnalysisContractAdapter? analysisContractAdapter = null)
    {
        _generationProvider = generationProvider;
        _localValidator = localValidator;
        _fileService = fileService;
        _assetRepository = assetRepository;
        _decisionEngine = decisionEngine;
        _options = options;
        _logger = logger;
        _retryPolicy = retryPolicy ?? new RetryPolicy(options.MaxAutomaticAttempts);
        _localAnalysisEngine = localAnalysisEngine;
        _localToFurnitureAdapter = localToFurnitureAdapter ?? new Analysis.LocalToFurnitureAnalysisAdapter();
        _analysisContractAdapter = analysisContractAdapter ?? new FurnitureAnalysisContractAdapter(_localToFurnitureAdapter);
        _localPromptCompactor = localPromptCompactor ?? new Analysis.LocalPromptCompactor();
    }

    public async Task<ProcessingResult> ProcessAssetAsync(
        Project project,
        ImageAsset asset,
        Action<string>? progressReporter = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting 8-step pipeline for asset: {FileName} (ID: {AssetId})", asset.OriginalFileName, asset.Id);

        var outputDir = project.OutputDirectory;
        var acceptedDir = Path.Combine(outputDir, "Accepted");
        var reviewDir = Path.Combine(outputDir, "Review");
        var failedDir = Path.Combine(outputDir, "Failed");
        var metadataDir = Path.Combine(outputDir, "Metadata");
        var diagnosticsDir = Path.Combine(outputDir, "Diagnostics");
        var operationsDirectory = string.IsNullOrWhiteSpace(_options.OperationsDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenIMG", "operations")
            : _options.OperationsDirectory;
        var tempDir = Path.Combine(operationsDirectory, project.Id.ToString("N"), asset.Id.ToString("N"));
        Directory.CreateDirectory(tempDir);

        _fileService.EnsureProjectOutputDirectories(outputDir);

        var baseName = Path.GetFileNameWithoutExtension(asset.OriginalFileName);

        // Step 1: Source Verification & Hashing
        progressReporter?.Invoke("Verifying source file integrity and hashing...");
        if (!File.Exists(asset.OriginalFilePath))
        {
            return await FailAndPersistAsync(
                asset,
                RestorationFailureReason.Unknown,
                $"Original file does not exist: {asset.OriginalFilePath}",
                cancellationToken);
        }

        var sourceHashBefore = await _fileService.ComputeSha256Async(asset.OriginalFilePath, cancellationToken);
        if (!string.IsNullOrEmpty(asset.Sha256Hash) && !string.Equals(sourceHashBefore, asset.Sha256Hash, StringComparison.OrdinalIgnoreCase))
        {
            return await FailAndPersistAsync(
                asset,
                RestorationFailureReason.SourceIntegrityFailed,
                "Source file SHA-256 hash changed prior to processing!",
                cancellationToken);
        }

        _fileService.NormalizeOrientation(asset.OriginalFilePath);

        // Get or create ProcessingJob
        var job = await _assetRepository.GetLatestJobForAssetAsync(asset.Id, cancellationToken)
                  ?? await _assetRepository.CreateJobAsync(asset.Id, cancellationToken);

        // Never delete an incomplete provider attempt. In particular, a
        // GenerationMayExist checkpoint means the Send click may have reached
        // GPT even if the process died before receiving acknowledgement.
        var existingAttempts = await _assetRepository.GetAttemptsForJobAsync(job.Id, cancellationToken);
        var incompleteAttempts = existingAttempts.Where(a => a.CompletedAt == null).ToList();
        var ambiguousAttempt = existingAttempts.FirstOrDefault(a =>
            ProviderOperationRecoveryPolicy.MayHaveSubmittedGeneration(a.ProviderOperationState));
        var recoveryContext = ambiguousAttempt is null ? null : GenerationRecoveryCheckpoint.Restore(ambiguousAttempt, asset, sourceHashBefore);
        var recoveryAttempt = recoveryContext is null ? null : ambiguousAttempt;
        if (ambiguousAttempt != null && recoveryContext is null)
        {
            ambiguousAttempt.ProviderOperationState = ProviderOperationState.NeedsRecovery;
            ambiguousAttempt.ProviderFailureCategory ??= "NeedsRecovery";
            ambiguousAttempt.FailureDetails ??= "A provider operation may have been submitted before OpenIMG stopped. Inspect the persisted operation/candidate before starting another generation.";
            await _assetRepository.UpdateAttemptAsync(ambiguousAttempt, cancellationToken);
            asset.Status = ImageProcessingStatus.NeedsReview;
            job.Status = ImageProcessingStatus.NeedsReview;
            job.FailureReason = RestorationFailureReason.OperationRecoveryRequired;
            job.ErrorMessage = ambiguousAttempt.FailureDetails;
            job.CurrentStep = "GPT operation requires recovery review";
            await _assetRepository.UpdateAsync(asset, cancellationToken);
            await _assetRepository.UpdateJobAsync(job, cancellationToken);
            return new ProcessingResult
            {
                Success = false,
                Decision = ValidationDecision.NeedsReview,
                AttemptNumber = ambiguousAttempt.AttemptNumber,
                OutputPath = ambiguousAttempt.GeneratedImagePath,
                FailureReason = RestorationFailureReason.OperationRecoveryRequired,
                ErrorMessage = ambiguousAttempt.FailureDetails,
                QueuePauseRequested = true
            };
        }

        foreach (var safeToRetry in incompleteAttempts)
        {
            if (safeToRetry.Id == recoveryAttempt?.Id) continue;
            safeToRetry.ProviderOperationState = ProviderOperationState.Failed;
            safeToRetry.ProviderFailureCategory ??= "SafeToRetryBeforeSubmission";
            safeToRetry.FailureDetails ??= "OpenIMG stopped before the GPT submission checkpoint; no generation was submitted.";
            safeToRetry.CompletedAt = DateTimeOffset.UtcNow;
            await _assetRepository.UpdateAttemptAsync(safeToRetry, cancellationToken);
        }
        var completedAttempts = existingAttempts.Count(a => a.CompletedAt != null) + incompleteAttempts.Count;

        if (recoveryAttempt != null)
        {
            progressReporter?.Invoke("Recuperando la descarga GPT existente; no se enviará otra generación…");
        }
        else if (asset.Status == ImageProcessingStatus.Pending && job.AttemptCount >= _options.MaxAutomaticAttempts)
        {
            job.AttemptCount = 0;
            await _assetRepository.UpdateJobAsync(job, cancellationToken);
        }
        else if (job.AttemptCount != completedAttempts)
        {
            _logger.LogInformation("Crash recovery detected for asset {FileName}: syncing AttemptCount from {OldCount} to completed attempts ({CompletedCount})",
                asset.OriginalFileName, job.AttemptCount, completedAttempts);
            job.AttemptCount = completedAttempts;
            await _assetRepository.UpdateJobAsync(job, cancellationToken);
        }

        // GPT is the only generation provider in the product path. Source
        // understanding is rebuilt locally; legacy Gemini analysis JSON is
        // never promoted into the current prompt or Barrier 3 contract.
        LocalFurnitureContract? localContract = null;
        if (!string.IsNullOrWhiteSpace(job.LocalFurnitureContractJson))
        {
            try { localContract = JsonSerializer.Deserialize<LocalFurnitureContract>(job.LocalFurnitureContractJson); } catch { }
        }

        var cachedLocalContractIsCurrent = localContract != null &&
            string.Equals(localContract.SourceSha256, sourceHashBefore, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(localContract.AuthorityPolicyVersion, LocalContractConflictDetector.CurrentAuthorityPolicyVersion, StringComparison.Ordinal);
        if (!cachedLocalContractIsCurrent)
        {
            localContract = null;
            job.LocalFurnitureContractJson = null;
        }

        if (_localAnalysisEngine == null || _options.LocalAnalysisMode == LocalAnalysisMode.Disabled)
        {
            return await StopForLocalAnalysisReviewAsync("The GPT-only product workflow requires the local image-analysis engine, but it is unavailable or disabled.");
        }

        if (localContract == null)
        {
            try
            {
                progressReporter?.Invoke("Analyzing source locally before GPT restoration...");
                localContract = await _localAnalysisEngine.AnalyzeAsync(asset.OriginalFilePath, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Local source analysis failed for {FileName}; GPT generation is not started.", asset.OriginalFileName);
                return await StopForLocalAnalysisReviewAsync($"Local source analysis failed: {ex.Message}");
            }

            job.LocalFurnitureContractJson = JsonSerializer.Serialize(localContract);
            await _assetRepository.UpdateJobAsync(job, cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(metadataDir, $"{baseName}__local_analysis.json"),
                JsonSerializer.Serialize(localContract, new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);
        }

        if (localContract == null)
            return await StopForLocalAnalysisReviewAsync("Local image analysis returned no contract.");

        var localContractProblem = DescribeUnusableLocalContract(localContract, sourceHashBefore);
        if (localContractProblem != null)
            return await StopForLocalAnalysisReviewAsync(localContractProblem);

        var verifiedLocalContract = localContract;
        var analysisSource = AnalysisSource.Local;
        var analysis = _localToFurnitureAdapter.Adapt(verifiedLocalContract);
        job.LocalFurnitureContractJson = JsonSerializer.Serialize(verifiedLocalContract);
        job.FurnitureAnalysisJson = JsonSerializer.Serialize(analysis);
        job.Status = ImageProcessingStatus.AnalysisCompleted;
        await _assetRepository.UpdateJobAsync(job, cancellationToken);
        var adaptedJson = JsonSerializer.Serialize(analysis, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(metadataDir, $"{baseName}__analysis.json"), adaptedJson, cancellationToken);

        var canonicalContract = _analysisContractAdapter.FromLocal(verifiedLocalContract);
        var canonicalAnalysis = _analysisContractAdapter.ToLegacy(canonicalContract);

        async Task<ProcessingResult> StopForLocalAnalysisReviewAsync(string message)
        {
            asset.Status = ImageProcessingStatus.NeedsReview;
            job.Status = ImageProcessingStatus.NeedsReview;
            job.FailureReason = RestorationFailureReason.AnalysisFailed;
            job.ErrorMessage = message;
            job.CurrentStep = "Local analysis requires review";
            await _assetRepository.UpdateAsync(asset, cancellationToken);
            await _assetRepository.UpdateJobAsync(job, cancellationToken);
            return new ProcessingResult
            {
                Success = false,
                Decision = ValidationDecision.NeedsReview,
                FailureReason = RestorationFailureReason.AnalysisFailed,
                ErrorMessage = message
            };
        }

        string? DescribeUnusableLocalContract(LocalFurnitureContract contract, string expectedSourceHash)
        {
            if (!string.Equals(contract.SourceSha256, expectedSourceHash, StringComparison.OrdinalIgnoreCase))
                return "Local analysis does not match the current original image hash.";
            if (contract.Status is LocalAnalysisStatus.Failed or LocalAnalysisStatus.ModelUnavailable)
                return $"Local analysis status is {contract.Status}; no alternate AI analysis is configured.";
            if (contract.FurnitureBounds == null || contract.StructuralRegions.Count == 0)
                return "Local analysis did not provide both a furniture envelope and structural evidence.";
            if (contract.FurnitureEnvelopeAuthority == ConstraintAuthority.Uncertain)
                return "Local analysis marked the furniture envelope uncertain.";
            if (contract.LocalConfidence < _options.LocalAnalysisConfidenceThreshold)
                return $"Local analysis confidence {contract.LocalConfidence:F2} is below the configured {_options.LocalAnalysisConfidenceThreshold:F2} minimum.";
            return null;
        }

        // Execution of Restoration Attempts (Loop up to MaxAutomaticAttempts)
        string? failureFeedback = null;
        ProcessingResult? finalResult = null;

        for (int attemptNum = recoveryAttempt?.AttemptNumber ?? job.AttemptCount + 1;
             attemptNum <= Math.Max(_options.MaxAutomaticAttempts, recoveryAttempt?.AttemptNumber ?? 0); attemptNum++)
        {
            job.AttemptCount = attemptNum;
            _logger.LogInformation("Processing attempt {Attempt} for asset {FileName}", attemptNum, asset.OriginalFileName);

            // Step 4: Prompt Compilation
            progressReporter?.Invoke($"Compiling deterministic restoration prompt (Attempt {attemptNum})...");
            PromptCompactionReport? localCompaction = null;
            if (analysisSource == AnalysisSource.Local)
            {
                localCompaction = _localPromptCompactor.Compact(verifiedLocalContract, analysis);
                await File.WriteAllTextAsync(
                    Path.Combine(metadataDir, $"{baseName}__prompt_compaction.json"),
                    JsonSerializer.Serialize(localCompaction, new JsonSerializerOptions { WriteIndented = true }),
                    cancellationToken);
            }

            var prompt = recoveryAttempt?.RestorationPrompt ?? PromptCompiler.BuildRestorationPrompt(
                canonicalContract,
                _analysisContractAdapter,
                attemptNum,
                failureFeedback,
                _options.RestorationPromptVersion,
                _options,
                analysisSource,
                localCompaction?.Assemblies,
                localCompaction?.IndividualCriticalRegionIds);

            var generationOpId = recoveryAttempt?.ProviderOperationId ?? $"{asset.Id}_att{attemptNum}_{Guid.NewGuid():N}";
            var attempt = recoveryAttempt ?? new RestorationAttempt
            {
                ProcessingJobId = job.Id,
                AttemptNumber = attemptNum,
                PromptVersion = _options.RestorationPromptVersion,
                RestorationPrompt = prompt,
                AnalysisSource = analysisSource,
                ProviderOperationId = generationOpId,
                ProviderOperationState = ProviderOperationState.GenerationPrepared,
                CreatedAt = DateTimeOffset.UtcNow
            };
            if (recoveryAttempt is null)
                await _assetRepository.AddAttemptAsync(attempt, cancellationToken);

            // Step 5: Restoration Generation & Download
            progressReporter?.Invoke(recoveryAttempt is null
                ? $"Generating GPT restoration (Attempt {attemptNum})..."
                : "Recuperando la imagen de la operación GPT anterior…");
            _logger.LogInformation("[{Timestamp}] State Transition: Asset {FileName} (ID: {AssetId}) -> Restoring (Attempt {Attempt})",
                DateTimeOffset.UtcNow.ToString("o"), asset.OriginalFileName, asset.Id, attemptNum);

            asset.Status = ImageProcessingStatus.Restoring;
            job.Status = ImageProcessingStatus.Restoring;
            job.CurrentStep = $"Restoration Attempt {attemptNum}";
            await _assetRepository.UpdateAsync(asset, cancellationToken);
            await _assetRepository.UpdateJobAsync(job, cancellationToken);

            string downloadedTempPath;
            var ownershipContext = recoveryContext ?? new GenerationOwnershipContext
            {
                GenerationOperationId = generationOpId,
                AssetId = asset.Id.ToString(),
                ExpectedSourcePath = asset.OriginalFilePath,
                ExpectedSourceSha256 = asset.Sha256Hash
            };
            ownershipContext.PersistOperationStateAsync = async (state, token) =>
            {
                attempt.ProviderOperationState = state;
                attempt.RawResponse = GenerationRecoveryCheckpoint.Capture(ownershipContext, prompt);
                if (state == ProviderOperationState.CandidateDownloaded)
                {
                    attempt.GeneratedImagePath = ownershipContext.DownloadedTempPath;
                    attempt.GeneratedSha256 = ownershipContext.DownloadedCandidateSha256;
                }
                await _assetRepository.UpdateAttemptAsync(attempt, token);
                _logger.LogInformation("[{OperationId}] GPT stage {ProviderStage}; persisted state {ProviderState}.",
                    generationOpId, ownershipContext.ProviderFailureStage, state);
                progressReporter?.Invoke(state == ProviderOperationState.CandidateDownloaded ? "GPT: archivo descargado; verificando y guardando resultado…"
                    : ownershipContext.ProviderFailureStage switch
                    {
                        "BrowserLock" => "GPT: esperando acceso al navegador…",
                        "Readiness" => "GPT: comprobando sesión y compositor…",
                        "ConversationStart" => "GPT: preparando conversación nueva…",
                        "AttachmentUpload" => "GPT: adjuntando imagen original…",
                        "PromptInsertion" => "GPT: insertando y verificando instrucciones…",
                        "UsageBudget" => "GPT: comprobando disponibilidad antes del envío…",
                        "PromptSubmission" => "GPT: enviando una vez y esperando confirmación…",
                        "Generation" => "GPT: mensaje aceptado; esperando imagen terminada…",
                        "Download" => "GPT: buscando imagen de la operación y preparando descarga…",
                        _ => "GPT: procesando operación…"
                    });
            };

            try
            {
                downloadedTempPath = await _generationProvider.GenerateRestorationScopedAsync(asset.OriginalFilePath, prompt, tempDir, ownershipContext, cancellationToken);
                _logger.LogInformation("[{Timestamp}] State Transition: Asset {FileName} (ID: {AssetId}) -> RestorationCompleted (Attempt {Attempt}, OpId: {OpId})",
                    DateTimeOffset.UtcNow.ToString("o"), asset.OriginalFileName, asset.Id, attemptNum, generationOpId);
            }
            catch (ProviderUsageLimitException ex)
            {
                var generationMayExist = ProviderOperationRecoveryPolicy.MayHaveSubmittedGeneration(ownershipContext.OperationState);
                attempt.ProviderFailureCategory = string.Equals(ownershipContext.ProviderFailureStage, "RateLimited", StringComparison.OrdinalIgnoreCase)
                    ? "RateLimited"
                    : "UsageBudgetReached";
                attempt.ProviderOperationState = generationMayExist ? ProviderOperationState.NeedsRecovery : ownershipContext.OperationState;
                ownershipContext.OperationState = attempt.ProviderOperationState;
                attempt.RawResponse = GenerationRecoveryCheckpoint.Capture(ownershipContext, prompt);
                attempt.FailureReason = generationMayExist
                    ? RestorationFailureReason.OperationRecoveryRequired
                    : RestorationFailureReason.ProviderUsageLimitReached;
                attempt.FailureDetails = ex.Message;
                attempt.FinalDecision = ValidationDecision.NeedsReview;
                attempt.CompletedAt = DateTimeOffset.UtcNow;
                await _assetRepository.UpdateAttemptAsync(attempt, cancellationToken);

                if (generationMayExist)
                {
                    asset.Status = ImageProcessingStatus.NeedsReview;
                    job.Status = ImageProcessingStatus.NeedsReview;
                    job.FailureReason = RestorationFailureReason.OperationRecoveryRequired;
                    job.CurrentStep = "GPT operation requires recovery review";
                }
                else
                {
                    asset.Status = ImageProcessingStatus.Pending;
                    job.Status = ImageProcessingStatus.Paused;
                    job.FailureReason = RestorationFailureReason.ProviderUsageLimitReached;
                    job.CurrentStep = "Paused: GPT image-generation capacity";
                }
                job.ErrorMessage = ex.Message;
                await _assetRepository.UpdateAsync(asset, cancellationToken);
                await _assetRepository.UpdateJobAsync(job, cancellationToken);
                return new ProcessingResult
                {
                    Success = false,
                    Decision = ValidationDecision.NeedsReview,
                    AttemptNumber = attemptNum,
                    FailureReason = generationMayExist
                        ? RestorationFailureReason.OperationRecoveryRequired
                        : RestorationFailureReason.ProviderUsageLimitReached,
                    ErrorMessage = ex.Message,
                    QueuePauseRequested = true
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Restoration generation failed on attempt {Attempt} (OpId: {OpId})", attemptNum, generationOpId);
                if (ProviderOperationRecoveryPolicy.MayHaveSubmittedGeneration(ownershipContext.OperationState))
                {
                    attempt.ProviderOperationState = ProviderOperationState.NeedsRecovery;
                    ownershipContext.OperationState = ProviderOperationState.NeedsRecovery;
                    attempt.RawResponse = GenerationRecoveryCheckpoint.Capture(ownershipContext, prompt);
                    attempt.ProviderFailureCategory = ClassifyProviderFailure(ownershipContext, ex);
                    attempt.FailureReason = RestorationFailureReason.OperationRecoveryRequired;
                    attempt.FailureDetails = "GPT may have accepted this operation. OpenIMG will not automatically submit it again. " + DescribeProviderFailure(ownershipContext, ex);
                    attempt.CompletedAt = DateTimeOffset.UtcNow;
                    await _assetRepository.UpdateAttemptAsync(attempt, cancellationToken);
                    asset.Status = ImageProcessingStatus.NeedsReview;
                    job.Status = ImageProcessingStatus.NeedsReview;
                    job.FailureReason = RestorationFailureReason.OperationRecoveryRequired;
                    job.ErrorMessage = attempt.FailureDetails;
                    job.CurrentStep = "GPT operation requires recovery review";
                    await _assetRepository.UpdateAsync(asset, cancellationToken);
                    await _assetRepository.UpdateJobAsync(job, cancellationToken);
                    return new ProcessingResult
                    {
                        Success = false,
                        Decision = ValidationDecision.NeedsReview,
                        AttemptNumber = attemptNum,
                        OutputPath = attempt.GeneratedImagePath,
                        FailureReason = RestorationFailureReason.OperationRecoveryRequired,
                        ErrorMessage = attempt.FailureDetails,
                        QueuePauseRequested = true
                    };
                }

                var providerStage = ownershipContext.ProviderReadinessFailureStage ?? ownershipContext.ProviderFailureStage;
                if (!string.IsNullOrWhiteSpace(providerStage))
                {
                    var failureCategory = ClassifyProviderFailure(ownershipContext, ex);
                    var failureReason = failureCategory switch
                    {
                        "AuthenticationRequired" => RestorationFailureReason.AuthenticationRequired,
                        "RateLimited" or "UsageBudgetReached" => RestorationFailureReason.ProviderUsageLimitReached,
                        "AttachmentUploadFailed" => RestorationFailureReason.UploadFailed,
                        _ => RestorationFailureReason.BrowserUnavailable
                    };
                    attempt.ProviderOperationState = ProviderOperationState.Failed;
                    attempt.ProviderFailureCategory = failureCategory;
                    attempt.FailureReason = failureReason;
                    attempt.FailureDetails = DescribeProviderFailure(ownershipContext, ex);
                    attempt.FinalDecision = ValidationDecision.NeedsReview;
                    attempt.CompletedAt = DateTimeOffset.UtcNow;
                    await _assetRepository.UpdateAttemptAsync(attempt, cancellationToken);

                    asset.Status = ImageProcessingStatus.Pending;
                    job.Status = ImageProcessingStatus.Paused;
                    job.FailureReason = failureReason;
                    job.ErrorMessage = attempt.FailureDetails;
                    job.CurrentStep = "Paused after a GPT provider warning or failure";
                    await _assetRepository.UpdateAsync(asset, cancellationToken);
                    await _assetRepository.UpdateJobAsync(job, cancellationToken);
                    return new ProcessingResult
                    {
                        Success = false,
                        Decision = ValidationDecision.NeedsReview,
                        AttemptNumber = attemptNum,
                        FailureReason = failureReason,
                        ErrorMessage = attempt.FailureDetails,
                        QueuePauseRequested = true
                    };
                }

                attempt.FailureReason = RestorationFailureReason.RestorationFailed;
                attempt.ProviderFailureCategory = ClassifyProviderFailure(ownershipContext, ex);
                attempt.ProviderOperationState = ProviderOperationState.Failed;
                attempt.FailureDetails = ex.Message;
                attempt.FinalDecision = ValidationDecision.Rejected;
                attempt.CompletedAt = DateTimeOffset.UtcNow;
                await _assetRepository.UpdateAttemptAsync(attempt, cancellationToken);
                continue;
            }

            attempt.GeneratedImagePath = downloadedTempPath;
            attempt.CompletedAt = null;
            attempt.FailureReason = null;
            attempt.FailureDetails = null;
            attempt.ProviderFailureCategory = null;
            attempt.GeneratedSha256 = await _fileService.ComputeSha256Async(downloadedTempPath, cancellationToken);
            attempt.ProviderOperationState = ProviderOperationState.ValidationPending;
            await _assetRepository.UpdateAttemptAsync(attempt, cancellationToken);
            ownershipContext.PersistedCandidateOperationId = generationOpId;
            ownershipContext.AssertPersistenceOwnership(downloadedTempPath, attempt.GeneratedSha256, asset.Id.ToString());

            // Step 6: Local OpenCV Validation (Barrier 3)
            progressReporter?.Invoke($"Executing local OpenCV geometric & color validation (Attempt {attemptNum})...");
            _logger.LogInformation("[{Timestamp}] State Transition: Asset {FileName} (ID: {AssetId}) -> LocalValidation (Attempt {Attempt})",
                DateTimeOffset.UtcNow.ToString("o"), asset.OriginalFileName, asset.Id, attemptNum);

            asset.Status = ImageProcessingStatus.LocalValidation;
            job.Status = ImageProcessingStatus.LocalValidation;
            await _assetRepository.UpdateAsync(asset, cancellationToken);
            await _assetRepository.UpdateJobAsync(job, cancellationToken);

            var localReport = await _localValidator.ValidateAsync(
                asset.OriginalFilePath,
                downloadedTempPath,
                canonicalAnalysis,
                diagnosticsDir,
                $"{baseName}__openimg_v{attemptNum}",
                cancellationToken);
            attempt.LocalValidationReportJson = JsonSerializer.Serialize(localReport);

            var localDecision = _decisionEngine.EvaluateLocalReport(localReport, out var localEvidences);
            _logger.LogInformation("Attempt {Attempt} Local Decision: {Decision}", attemptNum, localDecision);

            // Barrier 3 remains the product's complete automated decision.
            // Barrier 4 was Gemini comparative validation and is disabled.
            GeminiValidationReport? remoteReport = null;
            var combinedDecision = localDecision;
            var allEvidences = localEvidences;
            const bool remoteValidationSkipped = true;

            // These fields are legacy Gemini telemetry. GPT use is accounted
            // separately in the durable provider-usage ledger.
            attempt.GeminiStructuredAnalysisUsed = false;
            attempt.GeminiRestorationUsed = false;
            attempt.GeminiComparativeValidationUsed = false;
            attempt.GeminiOperationsTotal = 0;
            attempt.RemoteValidationSkipped = remoteValidationSkipped;
            attempt.RemoteValidationSkipReason = "Barrier4DisabledInGptPrimaryPipeline";
            attempt.RemoteValidationRequirement = RemoteValidationRequirement.NotRequired.ToString();
            attempt.RemoteValidationReasonsJson = "[]";

            attempt.FinalDecision = combinedDecision;
            await _assetRepository.UpdateAttemptAsync(attempt, cancellationToken);

            // Step 8: Final Safeguard & Persistence
            progressReporter?.Invoke("Running final source integrity safeguard check...");
            var sourceHashAfter = await _fileService.ComputeSha256Async(asset.OriginalFilePath, cancellationToken);
            if (!string.Equals(sourceHashBefore, sourceHashAfter, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogCritical("CRITICAL: Original file was modified during processing! {FilePath}", asset.OriginalFilePath);
                throw new InvalidOperationException($"CRITICAL SECURITY VIOLATION: Source photograph {asset.OriginalFilePath} hash altered during execution!");
            }

            // Evaluate Retry Policy
            var retryEval = _retryPolicy.EvaluateRetry(attemptNum, combinedDecision, localReport, remoteReport, allEvidences);

            // Move files to target destination
            string finalImagePath;
            string targetFolder;

            if (combinedDecision == ValidationDecision.Accepted)
            {
                targetFolder = acceptedDir;
                asset.Status = ImageProcessingStatus.Accepted;
                job.Status = ImageProcessingStatus.Accepted;
            }
            else if (combinedDecision == ValidationDecision.NeedsReview || !retryEval.ShouldRetry)
            {
                // Non-retryable rejection (uncertainty, homography, conflict, or attempts exhausted) routes strictly to Review
                targetFolder = reviewDir;
                asset.Status = ImageProcessingStatus.NeedsReview;
                job.Status = ImageProcessingStatus.NeedsReview;
            }
            else
            {
                // Actionable failure with retries remaining
                targetFolder = failedDir;
                asset.Status = ImageProcessingStatus.Failed;
                job.Status = ImageProcessingStatus.Failed;
            }

            _logger.LogInformation("[{Timestamp}] State Transition: Asset {FileName} (ID: {AssetId}) -> {FinalStatus} (Attempt {Attempt}, Decision: {Decision})",
                DateTimeOffset.UtcNow.ToString("o"), asset.OriginalFileName, asset.Id, asset.Status, attemptNum, combinedDecision);

            var destFileName = $"{baseName}__openimg_v{attemptNum}_{generationOpId[^8..]}.png";
            finalImagePath = Path.Combine(targetFolder, destFileName);
            File.Copy(downloadedTempPath, finalImagePath, overwrite: false);
            attempt.GeneratedImagePath = finalImagePath;
            ownershipContext.PersistedCandidatePath = finalImagePath;
            ownershipContext.PersistedCandidateSha256 = attempt.GeneratedSha256;
            ownershipContext.OperationState = ProviderOperationState.Finalized;
            attempt.ProviderOperationState = ProviderOperationState.Finalized;
            attempt.RawResponse = GenerationRecoveryCheckpoint.Capture(ownershipContext, prompt);
            await _assetRepository.UpdateAttemptAsync(attempt, cancellationToken);

            // Save sidecar metadata
            var sidecarData = new
            {
                sourceFileName = asset.OriginalFileName,
                sourceOriginalPath = asset.OriginalFilePath,
                sourceSha256 = sourceHashBefore,
                generatedSha256 = attempt.GeneratedSha256,
                analysis,
                attemptNumber = attemptNum,
                promptVersion = attempt.PromptVersion,
                localValidation = localReport,
                remoteValidation = remoteReport,
                evidences = allEvidences,
                finalDecision = combinedDecision.ToString(),
                retryReason = retryEval.Reason.ToString(),
                retryExplanation = retryEval.Explanation,
                remoteValidationRouting = new
                {
                    requirement = RemoteValidationRequirement.NotRequired.ToString(),
                    reasons = Array.Empty<string>(),
                    wouldCallGemini = false,
                    explanation = "Barrier 4 is disabled in the GPT-primary product pipeline.",
                    mode = "Disabled"
                },
                quotaAccounting = new
                {
                    geminiStructuredAnalysisUsed = attempt.GeminiStructuredAnalysisUsed,
                    geminiRestorationUsed = attempt.GeminiRestorationUsed,
                    geminiComparativeValidationUsed = attempt.GeminiComparativeValidationUsed,
                    geminiOperationsTotal = attempt.GeminiOperationsTotal,
                    gptImageGenerationUsed = true,
                    remoteValidationSkipped = attempt.RemoteValidationSkipped,
                    remoteValidationSkipReason = attempt.RemoteValidationSkipReason
                },
                manualApproval = false,
                timestamp = DateTimeOffset.UtcNow
            };

            await _fileService.SaveSidecarMetadataAsync(metadataDir, $"{baseName}__openimg", sidecarData, cancellationToken);

            attempt.CompletedAt = DateTimeOffset.UtcNow;
            await _assetRepository.UpdateAttemptAsync(attempt, cancellationToken);
            await _assetRepository.UpdateAsync(asset, cancellationToken);
            await _assetRepository.UpdateJobAsync(job, cancellationToken);

            finalResult = new ProcessingResult
            {
                Success = combinedDecision == ValidationDecision.Accepted,
                Decision = combinedDecision,
                AttemptNumber = attemptNum,
                OutputPath = finalImagePath,
                MetadataPath = Path.Combine(metadataDir, $"{baseName}__openimg.json"),
                GeneratedSha256 = attempt.GeneratedSha256,
                Evidences = allEvidences,
                RemoteValidationRequirement = RemoteValidationRequirement.NotRequired,
                RemoteValidationReasons = [],
                RemoteValidationSkipped = remoteValidationSkipped,
                GeminiOperationsTotal = attempt.GeminiOperationsTotal
            };

            if (combinedDecision == ValidationDecision.Accepted || !retryEval.ShouldRetry || recoveryAttempt is not null)
            {
                if (!retryEval.ShouldRetry && combinedDecision != ValidationDecision.Accepted)
                {
                    _logger.LogInformation("Attempt {Attempt} will not be retried. Reason: {Reason} ({Explanation})",
                        attemptNum, retryEval.Reason, retryEval.Explanation);
                }
                break;
            }

            failureFeedback = retryEval.CorrectiveConstraint;
            _logger.LogInformation("Automatic retry approved for attempt {NextAttempt} ({Reason}). Corrective constraint: {Constraint}",
                attemptNum + 1, retryEval.Reason, failureFeedback);
        }

        progressReporter?.Invoke($"Finished processing {asset.OriginalFileName}: {finalResult?.Decision}");
        if (finalResult == null)
        {
            asset.Status = ImageProcessingStatus.Failed;
            await _assetRepository.UpdateAsync(asset, cancellationToken);
            return FailResult(asset, RestorationFailureReason.Unknown, "No attempts produced a valid result.");
        }
        return finalResult;
    }

    private static string ClassifyProviderFailure(GenerationOwnershipContext context, Exception exception)
    {
        var stage = context.ProviderReadinessFailureStage ?? context.ProviderFailureStage ?? string.Empty;
        if (stage.Contains("RateLimited", StringComparison.OrdinalIgnoreCase)) return "RateLimited";
        if (stage.Contains("Ownership", StringComparison.OrdinalIgnoreCase) || exception.Message.Contains("ownership", StringComparison.OrdinalIgnoreCase)) return "OwnershipFailed";
        if (exception is ProviderUsageLimitException)
            return "UsageBudgetReached";
        if (stage.Contains("Authentication", StringComparison.OrdinalIgnoreCase)) return "AuthenticationRequired";
        if (stage.Contains("Navigation", StringComparison.OrdinalIgnoreCase) || stage.Contains("ProviderUnavailable", StringComparison.OrdinalIgnoreCase)) return "ProviderUnavailable";
        if (stage.Contains("Dom", StringComparison.OrdinalIgnoreCase) || stage.Contains("Composer", StringComparison.OrdinalIgnoreCase)) return "DomContractUnavailable";
        if (stage.Contains("Attachment", StringComparison.OrdinalIgnoreCase) || stage.Contains("Upload", StringComparison.OrdinalIgnoreCase)) return "AttachmentUploadFailed";
        if (stage.Contains("Prompt", StringComparison.OrdinalIgnoreCase) || stage.Contains("Submission", StringComparison.OrdinalIgnoreCase)) return "PromptSubmissionFailed";
        if (stage.Contains("GenerationDidNotStart", StringComparison.OrdinalIgnoreCase)) return "GenerationDidNotStart";
        if (stage.Contains("Generation", StringComparison.OrdinalIgnoreCase)) return "GenerationFailed";
        if (stage.Contains("Download", StringComparison.OrdinalIgnoreCase)) return "DownloadFailed";
        if (exception is TimeoutException) return "ProviderUnavailable";
        return "ProviderUnavailable";
    }

    private static string DescribeProviderFailure(GenerationOwnershipContext context, Exception exception)
    {
        var stage = context.ProviderReadinessFailureStage ?? context.ProviderFailureStage;
        return string.IsNullOrWhiteSpace(stage)
            ? $"Provider details: {exception.Message}"
            : $"Provider stage: {stage}. {exception.Message}";
    }

    private ProcessingResult FailResult(ImageAsset asset, RestorationFailureReason reason, string message)
    {
        asset.Status = ImageProcessingStatus.Failed;
        return new ProcessingResult
        {
            Success = false,
            Decision = ValidationDecision.Rejected,
            FailureReason = reason,
            ErrorMessage = message
        };
    }

    private async Task<ProcessingResult> FailAndPersistAsync(
        ImageAsset asset,
        RestorationFailureReason reason,
        string message,
        CancellationToken cancellationToken)
    {
        var result = FailResult(asset, reason, message);
        await _assetRepository.UpdateAsync(asset, cancellationToken);
        return result;
    }

    private async Task<ProcessingResult> HandleFailureAsync(
        ImageAsset asset,
        ProcessingJob job,
        RestorationFailureReason reason,
        string message,
        CancellationToken cancellationToken)
    {
        asset.Status = ImageProcessingStatus.Failed;
        job.Status = ImageProcessingStatus.Failed;
        job.FailureReason = reason;
        job.ErrorMessage = message;
        await _assetRepository.UpdateAsync(asset, cancellationToken);
        await _assetRepository.UpdateJobAsync(job, cancellationToken);

        return new ProcessingResult
        {
            Success = false,
            Decision = ValidationDecision.Rejected,
            FailureReason = reason,
            ErrorMessage = message
        };
    }

}
