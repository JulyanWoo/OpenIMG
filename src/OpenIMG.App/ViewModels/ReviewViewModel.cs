using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenIMG.App.Helpers;
using OpenIMG.Application.Abstractions.Persistence;
using OpenIMG.Application.Abstractions.Storage;
#if OPENIMG_ENGINEERING
using OpenIMG.Application.Calibration;
#endif
using OpenIMG.Application.Configuration;
using OpenIMG.Application.Validation;
using OpenIMG.Core.Entities;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;

namespace OpenIMG.App.ViewModels;

public partial class ReviewViewModel : ObservableObject
{
    private readonly IImageAssetRepository _assetRepository;
    private readonly IFileSystemService _fileService;
    private readonly OpenImgOptions _options;
#if OPENIMG_ENGINEERING
    private string? _groundTruthPath;
#endif

    [ObservableProperty]
    private ImageAsset _asset;

    [ObservableProperty]
    private RestorationAttempt? _latestAttempt;

    [ObservableProperty]
    private FurnitureAnalysis? _analysis;

    [ObservableProperty]
    private LocalValidationReport? _localReport;

    [ObservableProperty]
    private GeminiValidationReport? _geminiReport;

    [ObservableProperty]
    private BitmapSource? _originalBitmap;

    [ObservableProperty]
    private BitmapSource? _restoredBitmap;

    [ObservableProperty]
    private string _decisionTitle = "Review Decision";

    // --- Phase 4D Barrier 3 & Barrier 4 Routing Diagnostics ---
    [ObservableProperty]
    private string _barrier3Decision = "Unknown";

    [ObservableProperty]
    private string _barrier4Routing = "Not Evaluated";

    [ObservableProperty]
    private string _barrier4RoutingExplanation = string.Empty;

    // Product review summary. This is presentation-only and does not change
    // Barrier 3/4 policy or the persisted human ground truth.
    [ObservableProperty]
    private ObservableCollection<string> _automatedDecisionReasons = new();

    // --- Phase 3 Blind Review & Ground Truth Properties ---
    [ObservableProperty]
    private string _calibrationId = string.Empty;

    [ObservableProperty]
    private bool _isCalibrationItem;

    [ObservableProperty]
    private HumanGroundTruthJudgment _humanJudgment = HumanGroundTruthJudgment.Pending;

    [ObservableProperty]
    private bool _isBadColor;

    [ObservableProperty]
    private bool _isBadGeometry;

    [ObservableProperty]
    private bool _isBadComponent;

    [ObservableProperty]
    private bool _isBadMaterial;

    [ObservableProperty]
    private bool _isBadAddedDetail;

    [ObservableProperty]
    private bool _isBadRemovedDetail;

    [ObservableProperty]
    private bool _isBadComposition;

    [ObservableProperty]
    private string _humanNotes = string.Empty;

    [ObservableProperty]
    private AnalysisGroundTruthQuality _analysisQuality = AnalysisGroundTruthQuality.Uncertain;

    [ObservableProperty]
    private ProtectedRegionQuality _regionQuality = ProtectedRegionQuality.Approximate;

    [ObservableProperty]
    private string _groundTruthSaveStatus = string.Empty;

    [ObservableProperty]
    private HumanReviewStatus _humanReviewStatus = HumanReviewStatus.Pending;

    [ObservableProperty]
    private string _humanReviewSaveStatus = string.Empty;

    public bool IsCalibrationFeaturesEnabled
    {
        get
        {
#if OPENIMG_ENGINEERING
            return _options.EnableCalibrationFeatures;
#else
            return false;
#endif
        }
    }
    public bool CanExportCandidate =>
        HumanReviewStatus == HumanReviewStatus.Approved &&
        LatestAttempt?.GeneratedImagePath is { } path && File.Exists(path);

    public event Action? RequestClose;
    public event Action? RequestExport;

    partial void OnHumanReviewStatusChanged(HumanReviewStatus value) => OnPropertyChanged(nameof(CanExportCandidate));

    partial void OnLatestAttemptChanged(RestorationAttempt? value) => OnPropertyChanged(nameof(CanExportCandidate));

    public ReviewViewModel(
        ImageAsset asset,
        IImageAssetRepository assetRepository,
        IFileSystemService fileService,
        OpenImgOptions options)
    {
        _asset = asset;
        _assetRepository = assetRepository;
        _fileService = fileService;
        _options = options;

        LoadAttemptDetails();
#if OPENIMG_ENGINEERING
        if (_options.EnableCalibrationFeatures)
            LoadCalibrationContext();
#endif
    }

    private void LoadAttemptDetails()
    {
        var job = Asset.ProcessingJobs.OrderByDescending(j => j.CreatedAt).FirstOrDefault();
        if (job != null)
        {
            if (!string.IsNullOrEmpty(job.FurnitureAnalysisJson))
            {
                try
                {
                    Analysis = JsonSerializer.Deserialize<FurnitureAnalysis>(job.FurnitureAnalysisJson);
                }
                catch { }
            }

            LatestAttempt = job.Attempts.OrderByDescending(a => a.AttemptNumber).FirstOrDefault();
            if (LatestAttempt != null)
            {
                if (!string.IsNullOrWhiteSpace(LatestAttempt.HumanReviewJson))
                {
                    try
                    {
                        var review = JsonSerializer.Deserialize<HumanReviewAssessment>(LatestAttempt.HumanReviewJson);
                        if (review != null)
                        {
                            HumanReviewStatus = review.Status;
                            HumanNotes = review.Notes ?? string.Empty;
                        }
                    }
                    catch { }
                }

                if (!string.IsNullOrEmpty(LatestAttempt.LocalValidationReportJson))
                {
                    try
                    {
                        LocalReport = JsonSerializer.Deserialize<LocalValidationReport>(LatestAttempt.LocalValidationReportJson);
                    }
                    catch { }
                }

                if (!string.IsNullOrEmpty(LatestAttempt.GeminiValidationReportJson))
                {
                    try
                    {
                        GeminiReport = JsonSerializer.Deserialize<GeminiValidationReport>(LatestAttempt.GeminiValidationReportJson);
                    }
                    catch { }
                }
                else if (!string.IsNullOrEmpty(LatestAttempt.CalibrationRemoteValidationJson))
                {
                    try
                    {
                        GeminiReport = JsonSerializer.Deserialize<GeminiValidationReport>(LatestAttempt.CalibrationRemoteValidationJson);
                    }
                    catch { }
                }

                if (!string.IsNullOrEmpty(LatestAttempt.GeneratedImagePath) && File.Exists(LatestAttempt.GeneratedImagePath))
                {
                    try
                    {
                        RestoredBitmap = BitmapHelper.LoadBitmapWithExifOrientation(LatestAttempt.GeneratedImagePath);
                    }
                    catch { }
                }

                if (LocalReport != null)
                {
                    var decisionEngine = new ValidationDecisionEngine(_options);
                    var dec = decisionEngine.EvaluateLocalReport(LocalReport, out _);
                    Barrier3Decision = dec.ToString();
                    AutomatedDecisionReasons = BuildAutomatedDecisionReasons(LocalReport);
                }

                if (!_options.EnableCalibrationFeatures)
                {
                    Barrier4Routing = "Disabled in production";
                    Barrier4RoutingExplanation = "Barrier 4 is not part of the GPT-only production workflow.";
                }
                else if (!string.IsNullOrEmpty(LatestAttempt.RemoteValidationRequirement))
                {
                    Barrier4Routing = LatestAttempt.RemoteValidationRequirement;
                    if (LatestAttempt.RemoteValidationSkipped)
                    {
                        Barrier4RoutingExplanation = "Skipped because local validation produced a low-risk acceptance.";
                    }
                    else if (LatestAttempt.RemoteValidationRequirement == "BlockedByLocalRejection")
                    {
                        Barrier4RoutingExplanation = "Blocked because local deterministic evidence found a blocking physical violation (Barrier 3 Rejected).";
                    }
                    else if (!string.IsNullOrEmpty(LatestAttempt.RemoteValidationReasonsJson))
                    {
                        try
                        {
                            var reasons = JsonSerializer.Deserialize<List<string>>(LatestAttempt.RemoteValidationReasonsJson);
                            if (reasons != null && reasons.Count > 0)
                            {
                                Barrier4RoutingExplanation = $"Required because {string.Join(", ", reasons)}.";
                            }
                        }
                        catch { }
                    }
                }
                else if (LocalReport != null)
                {
                    var policy = new RemoteValidationPolicy(_options);
                    var dec = Enum.TryParse<ValidationDecision>(Barrier3Decision, out var d) ? d : (LocalReport.Passed ? ValidationDecision.Accepted : ValidationDecision.Rejected);
                    var eval = policy.Evaluate(LocalReport, dec);
                    Barrier4Routing = eval.Requirement.ToString();
                    Barrier4RoutingExplanation = eval.Explanation;
                }
            }
        }

        if (File.Exists(Asset.OriginalFilePath))
        {
            try
            {
                OriginalBitmap = BitmapHelper.LoadBitmapWithExifOrientation(Asset.OriginalFilePath);
            }
            catch { }
        }

        DecisionTitle = $"Review: {Asset.OriginalFileName} (Status: {Asset.Status})";
    }

    private static ObservableCollection<string> BuildAutomatedDecisionReasons(LocalValidationReport report)
    {
        var reasons = report.Evidences
            .Where(e => !e.Passed)
            .OrderByDescending(e => e.Severity)
            .ThenBy(e => e.Type, StringComparer.Ordinal)
            .Select(MapEvidenceToProductReason)
            .Where(reason => !string.IsNullOrWhiteSpace(reason))
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToList();

        return new ObservableCollection<string>(reasons);
    }

    private static string MapEvidenceToProductReason(ValidationEvidence evidence)
    {
        return evidence.Type switch
        {
            "UnsupportedPhysicalAddition" or
            "ComponentStructureAdded" or
            "SevereStructuralDeviation" or
            "UnexpectedStructureInProtectedAbsence" => "Possible added or altered physical structure",
            "MaterialColorIdentityViolation" or
            "ComponentColorIdentityChanged" => "Material or color identity changed",
            "CleanupIncomplete" => "Scene cleanup is incomplete",
            "PersistentSceneDetailRemoved" or
            "PersistentSceneDetailAltered" => "A persistent scene detail was removed or altered",
            "InsufficientPhotographicImprovement" => "Photographic improvement is insufficient",
            "ArtificialRectangularArtifact" => "Artificial rectangular artifact detected",
            "ComponentCardinalityMismatch" => "Observed component count changed",
            "ImageRegistration" => "Image registration is uncertain",
            _ when !string.IsNullOrWhiteSpace(evidence.Message) => evidence.Message,
            _ => evidence.Type
        };
    }

#if OPENIMG_ENGINEERING
    private void LoadCalibrationContext()
    {
        try
        {
            var manifestPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "calibration", "phase3", "calibration_manifest.json");
            if (!File.Exists(manifestPath))
            {
                // Try workspace root fallback
                manifestPath = Path.Combine(Directory.GetCurrentDirectory(), "calibration", "phase3", "calibration_manifest.json");
            }

            if (File.Exists(manifestPath))
            {
                var manifestText = File.ReadAllText(manifestPath);
                var items = JsonSerializer.Deserialize<List<CalibrationManifestItem>>(manifestText);
                var matched = items?.FirstOrDefault(i =>
                    (!string.IsNullOrEmpty(Asset.Sha256Hash) && string.Equals(i.SourceSha256, Asset.Sha256Hash, StringComparison.OrdinalIgnoreCase)) ||
                    string.Equals(i.SourceFileName, Asset.OriginalFileName, StringComparison.OrdinalIgnoreCase));
                if (matched != null)
                {
                    CalibrationId = matched.CalibrationId;
                    IsCalibrationItem = true;
                    DecisionTitle = $"Phase 3 Calibration [{CalibrationId}] ({matched.Category}): {Asset.OriginalFileName}";

                    // Try loading existing ground truth
                    _groundTruthPath = Path.Combine(Path.GetDirectoryName(manifestPath)!, "phase3_ground_truth.json");
                    if (File.Exists(_groundTruthPath))
                    {
                        var gtText = File.ReadAllText(_groundTruthPath);
                        var gts = JsonSerializer.Deserialize<List<HumanGroundTruth>>(gtText);
                        var existing = gts?.FirstOrDefault(g => g.CalibrationId == CalibrationId || string.Equals(g.SourceFileName, Asset.OriginalFileName, StringComparison.OrdinalIgnoreCase));
                        if (existing != null)
                        {
                            HumanJudgment = existing.OverallJudgment;
                            HumanNotes = existing.HumanNotes ?? string.Empty;
                            AnalysisQuality = existing.AnalysisQuality;
                            RegionQuality = existing.RegionQuality;

                            IsBadColor = existing.DefectTags.Contains(DefectTag.BadColor);
                            IsBadGeometry = existing.DefectTags.Contains(DefectTag.BadGeometry);
                            IsBadComponent = existing.DefectTags.Contains(DefectTag.BadComponent);
                            IsBadMaterial = existing.DefectTags.Contains(DefectTag.BadMaterial);
                            IsBadAddedDetail = existing.DefectTags.Contains(DefectTag.BadAddedDetail);
                            IsBadRemovedDetail = existing.DefectTags.Contains(DefectTag.BadRemovedDetail);
                            IsBadComposition = existing.DefectTags.Contains(DefectTag.BadComposition);
                            GroundTruthSaveStatus = $"Loaded human ground truth recorded at {existing.ReviewedAt:g}";
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore calibration context load errors gracefully
        }
    }
#endif

    [RelayCommand]
    public async Task SetJudgmentGoodAsync()
    {
        HumanJudgment = HumanGroundTruthJudgment.Good;
        // Uncheck defect tags when marking good
        IsBadColor = false;
        IsBadGeometry = false;
        IsBadComponent = false;
        IsBadMaterial = false;
        IsBadAddedDetail = false;
        IsBadRemovedDetail = false;
        IsBadComposition = false;
        await SaveGroundTruthAsync();
    }

    [RelayCommand]
    public async Task SetJudgmentUncertainAsync()
    {
        HumanJudgment = HumanGroundTruthJudgment.Uncertain;
        await SaveGroundTruthAsync();
    }

    [RelayCommand]
    public async Task SetJudgmentBadAsync()
    {
        HumanJudgment = HumanGroundTruthJudgment.Bad;
        await SaveGroundTruthAsync();
    }

    [RelayCommand]
    public async Task SaveGroundTruthAsync()
    {
#if OPENIMG_ENGINEERING
        if (!_options.EnableCalibrationFeatures || !IsCalibrationItem || string.IsNullOrEmpty(_groundTruthPath))
        {
            GroundTruthSaveStatus = "This item is not part of the Phase 3 calibration manifest.";
            return;
        }

        var defectTags = new List<DefectTag>();
        if (IsBadColor) defectTags.Add(DefectTag.BadColor);
        if (IsBadGeometry) defectTags.Add(DefectTag.BadGeometry);
        if (IsBadComponent) defectTags.Add(DefectTag.BadComponent);
        if (IsBadMaterial) defectTags.Add(DefectTag.BadMaterial);
        if (IsBadAddedDetail) defectTags.Add(DefectTag.BadAddedDetail);
        if (IsBadRemovedDetail) defectTags.Add(DefectTag.BadRemovedDetail);
        if (IsBadComposition) defectTags.Add(DefectTag.BadComposition);

        if (defectTags.Count > 0 && HumanJudgment == HumanGroundTruthJudgment.Pending)
        {
            HumanJudgment = HumanGroundTruthJudgment.Bad;
        }

        var entry = new HumanGroundTruth
        {
            CalibrationId = !string.IsNullOrEmpty(CalibrationId) ? CalibrationId : Asset.OriginalFileName,
            SourceFileName = Asset.OriginalFileName,
            OverallJudgment = HumanJudgment,
            DefectTags = defectTags,
            HumanNotes = HumanNotes,
            AnalysisQuality = AnalysisQuality,
            RegionQuality = RegionQuality,
            ReviewedAt = DateTimeOffset.UtcNow
        };

        try
        {
            var existingList = await Phase3CalibrationService.LoadGroundTruthAsync(_groundTruthPath);
            existingList.RemoveAll(x => x.CalibrationId == entry.CalibrationId || string.Equals(x.SourceFileName, entry.SourceFileName, StringComparison.OrdinalIgnoreCase));
            existingList.Add(entry);
            existingList = existingList.OrderBy(x => x.CalibrationId).ToList();

            await Phase3CalibrationService.SaveGroundTruthAsync(_groundTruthPath, existingList);
            GroundTruthSaveStatus = $"Ground truth saved ({entry.OverallJudgment}) at {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            GroundTruthSaveStatus = $"Error saving ground truth: {ex.Message}";
        }
#else
        await Task.CompletedTask;
        GroundTruthSaveStatus = "Calibration ground truth is unavailable in the product build.";
#endif
    }

    [RelayCommand]
    public async Task AcceptManuallyAsync()
    {
        await SaveHumanReviewAsync(HumanReviewStatus.Approved);
    }

    [RelayCommand]
    public async Task RejectAsync()
    {
        await SaveHumanReviewAsync(HumanReviewStatus.Rejected);
    }

    [RelayCommand]
    public async Task MarkUncertainAsync() => await SaveHumanReviewAsync(HumanReviewStatus.Uncertain);

    [RelayCommand]
    public async Task SaveHumanReviewAsync() => await SaveHumanReviewAsync(HumanReviewStatus);

    private async Task SaveHumanReviewAsync(HumanReviewStatus status)
    {
        if (LatestAttempt == null)
        {
            HumanReviewSaveStatus = "No restoration candidate is available to review.";
            return;
        }

        var previous = string.IsNullOrWhiteSpace(LatestAttempt.HumanReviewJson)
            ? new HumanReviewAssessment()
            : JsonSerializer.Deserialize<HumanReviewAssessment>(LatestAttempt.HumanReviewJson) ?? new HumanReviewAssessment();
        var review = previous with
        {
            Status = status,
            Notes = HumanNotes,
            ReviewedAt = DateTimeOffset.UtcNow
        };
        LatestAttempt.HumanReviewJson = JsonSerializer.Serialize(review);
        await _assetRepository.UpdateAttemptAsync(LatestAttempt);
        HumanReviewStatus = status;
        HumanReviewSaveStatus = $"Human review saved as {status} at {DateTime.Now:HH:mm:ss}. Automated decision was not changed.";
    }

    [RelayCommand]
    public void RequestCandidateExport() => RequestExport?.Invoke();

    public async Task ExportCandidateAsync(string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var attempt = LatestAttempt;
        if (HumanReviewStatus != HumanReviewStatus.Approved)
            throw new InvalidOperationException("Export is available only after an explicit human approval. Automated acceptance alone is not human approval.");

        var candidatePath = attempt?.GeneratedImagePath;
        if (string.IsNullOrWhiteSpace(candidatePath) || !File.Exists(candidatePath))
            throw new FileNotFoundException("The generated candidate is not available for export.", candidatePath);
        if (attempt == null)
            throw new InvalidOperationException("The restoration attempt is no longer available.");

        var fullDestination = Path.GetFullPath(destinationPath);
        var candidateFullPath = Path.GetFullPath(candidatePath);
        if (!string.Equals(Path.GetExtension(fullDestination), ".png", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Restoration candidates are exported as PNG files.");
        if (string.Equals(fullDestination, Path.GetFullPath(Asset.OriginalFilePath), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fullDestination, candidateFullPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Export must use a new path. The original and stored candidate are immutable.");

        var sidecarPath = fullDestination + ".openimg.json";
        Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);
        if (File.Exists(fullDestination) || File.Exists(sidecarPath))
            throw new IOException("The export image or its provenance file already exists. Choose a different filename.");

        var provenance = new
        {
            sourceFileName = Asset.OriginalFileName,
            sourcePath = Asset.OriginalFilePath,
            sourceSha256 = Asset.Sha256Hash,
            candidatePath,
            candidateSha256 = attempt.GeneratedSha256 ?? await _fileService.ComputeSha256Async(candidatePath),
            operationId = attempt.ProviderOperationId,
            automatedDecision = attempt.FinalDecision?.ToString(),
            humanReview = attempt.HumanReviewJson,
            exportedAt = DateTimeOffset.UtcNow
        };

        var destinationCreated = false;
        try
        {
            await using (var source = new FileStream(candidatePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true))
            await using (var destination = new FileStream(fullDestination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            {
                destinationCreated = true;
                await source.CopyToAsync(destination);
                await destination.FlushAsync();
            }
            await using var sidecar = new FileStream(sidecarPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true);
            await JsonSerializer.SerializeAsync(sidecar, provenance, cancellationToken: CancellationToken.None);
            HumanReviewSaveStatus = $"Candidate exported with provenance: {fullDestination}";
        }
        catch
        {
            if (destinationCreated && File.Exists(fullDestination))
                File.Delete(fullDestination);
            throw;
        }
    }

    [RelayCommand]
    public async Task RetryAsync()
    {
        Asset.Status = ImageProcessingStatus.Pending;
        await _assetRepository.UpdateAsync(Asset);
        RequestClose?.Invoke();
    }

    [RelayCommand]
    public void Close()
    {
        RequestClose?.Invoke();
    }
}
