using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenCvSharp;
using OpenIMG.Application.Abstractions.Analysis;
using OpenIMG.Application.Analysis;
using OpenIMG.Application.Configuration;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;
using OpenIMG.Infrastructure.ImageProcessing;

namespace OpenIMG.Infrastructure.Analysis;

public class LocalAnalysisEngine : ILocalAnalysisEngine
{
    private readonly ILocalObjectDetector _objectDetector;
    private readonly IStructuralRegionDetector _structuralDetector;
    private readonly IColorRegionAnalyzer _colorAnalyzer;
    private readonly IHardwareCandidateDetector _hardwareDetector;
    private readonly ILocalContractBuilder _contractBuilder;
    private readonly IProtectedAbsenceDetector _absenceDetector;
    private readonly IPersistentSceneDetailDetector _sceneDetailDetector;
    private readonly ISceneColorAnalyzer _sceneColorAnalyzer;
    private readonly ILocalContractConflictDetector _conflictDetector;
    private readonly OpenImgOptions _options;
    private readonly ILogger<LocalAnalysisEngine> _logger;

    // Compatibility constructor for calibration tests and small offline tools.
    // The desktop composition root uses the fully injected constructor below.
    public LocalAnalysisEngine(
        ILocalObjectDetector objectDetector,
        IStructuralRegionDetector structuralDetector,
        IColorRegionAnalyzer colorAnalyzer,
        IHardwareCandidateDetector hardwareDetector,
        ILocalContractBuilder contractBuilder,
        IOptions<OpenImgOptions> options,
        ILogger<LocalAnalysisEngine> logger)
        : this(
            objectDetector,
            structuralDetector,
            colorAnalyzer,
            hardwareDetector,
            contractBuilder,
            options,
            logger,
            new OpenCvProtectedAbsenceDetector(),
            new OpenCvPersistentSceneDetailDetector(),
            new OpenCvSceneColorAnalyzer(),
            new LocalContractConflictDetector())
    {
    }

    public LocalAnalysisEngine(
        ILocalObjectDetector objectDetector,
        IStructuralRegionDetector structuralDetector,
        IColorRegionAnalyzer colorAnalyzer,
        IHardwareCandidateDetector hardwareDetector,
        ILocalContractBuilder contractBuilder,
        IOptions<OpenImgOptions> options,
        ILogger<LocalAnalysisEngine> logger,
        IProtectedAbsenceDetector absenceDetector,
        IPersistentSceneDetailDetector sceneDetailDetector)
        : this(
            objectDetector,
            structuralDetector,
            colorAnalyzer,
            hardwareDetector,
            contractBuilder,
            options,
            logger,
            absenceDetector,
            sceneDetailDetector,
            new OpenCvSceneColorAnalyzer(),
            new LocalContractConflictDetector())
    {
    }

    public LocalAnalysisEngine(
        ILocalObjectDetector objectDetector,
        IStructuralRegionDetector structuralDetector,
        IColorRegionAnalyzer colorAnalyzer,
        IHardwareCandidateDetector hardwareDetector,
        ILocalContractBuilder contractBuilder,
        IOptions<OpenImgOptions> options,
        ILogger<LocalAnalysisEngine> logger,
        IProtectedAbsenceDetector absenceDetector,
        IPersistentSceneDetailDetector sceneDetailDetector,
        ISceneColorAnalyzer sceneColorAnalyzer,
        ILocalContractConflictDetector conflictDetector)
    {
        _objectDetector = objectDetector;
        _structuralDetector = structuralDetector;
        _colorAnalyzer = colorAnalyzer;
        _hardwareDetector = hardwareDetector;
        _contractBuilder = contractBuilder;
        _options = options.Value;
        _logger = logger;
        _absenceDetector = absenceDetector;
        _sceneDetailDetector = sceneDetailDetector;
        _sceneColorAnalyzer = sceneColorAnalyzer;
        _conflictDetector = conflictDetector;
    }

    public async Task<LocalFurnitureContract> AnalyzeAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var fileName = Path.GetFileName(imagePath);
        var warnings = new List<string>();

        if (!File.Exists(imagePath))
        {
            _logger.LogWarning("Local analysis failed: image file not found at {Path}", imagePath);
            return new LocalFurnitureContract
            {
                SourceFileName = fileName,
                Status = LocalAnalysisStatus.Failed,
                Warnings = new List<string> { $"Image file not found at {imagePath}" },
                AnalysisDuration = sw.Elapsed
            };
        }

        string sourceSha256 = string.Empty;
        try
        {
            using var fileStream = File.OpenRead(imagePath);
            sourceSha256 = Convert.ToHexString(await SHA256.HashDataAsync(fileStream, cancellationToken)).ToUpperInvariant();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to compute SHA256 for {Path}", imagePath);
        }

        Mat? workingImage = null;
        try
        {
            // 1. Read input image and normalize resolution
            using var fullImage = Cv2.ImRead(imagePath, ImreadModes.Color);
            if (fullImage.Empty())
            {
                return new LocalFurnitureContract
                {
                    SourceFileName = fileName,
                    SourceSha256 = sourceSha256,
                    Status = LocalAnalysisStatus.Failed,
                    Warnings = new List<string> { "Image could not be decoded by OpenCV." },
                    AnalysisDuration = sw.Elapsed
                };
            }

            var maxDim = Math.Max(640, _options.LocalAnalysisMaxDimension);
            var currentMax = Math.Max(fullImage.Width, fullImage.Height);

            if (currentMax > maxDim)
            {
                var resizeScale = (double)maxDim / currentMax;
                var newW = (int)Math.Round(fullImage.Width * resizeScale);
                var newH = (int)Math.Round(fullImage.Height * resizeScale);
                workingImage = new Mat();
                Cv2.Resize(fullImage, workingImage, new Size(newW, newH), 0, 0, InterpolationFlags.Area);
            }
            else
            {
                workingImage = fullImage.Clone();
            }

            cancellationToken.ThrowIfCancellationRequested();

            // 2. ONNX Scene Object Detection (Sequential step 1)
            var status = LocalAnalysisStatus.Success;
            var detectedObjects = new List<LocalDetectedObject>();
            var modelInfo = _objectDetector.GetModelInfo();

            if (_objectDetector.IsModelAvailable)
            {
                try
                {
                    detectedObjects = await _objectDetector.DetectObjectsAsync(workingImage, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ONNX object detection failed for {FileName}", fileName);
                    warnings.Add($"ONNX object detection error: {ex.Message}");
                    status = LocalAnalysisStatus.Partial;
                }
            }
            else
            {
                status = LocalAnalysisStatus.ModelUnavailable;
                warnings.Add("ModelUnavailable: ONNX model file is missing or session failed to initialize. General scene detection skipped.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            // 3. OpenCV Structural Region Detection (Sequential step 2)
            var structuralRegions = _structuralDetector.DetectStructuralRegions(workingImage);

            cancellationToken.ThrowIfCancellationRequested();

            // Estimate preliminary bounding envelope to focus color and hardware detectors
            NormalizedRectangle? preliminaryBounds = null;
            if (structuralRegions.Count > 0)
            {
                var minX = structuralRegions.Min(r => r.Region.X);
                var minY = structuralRegions.Min(r => r.Region.Y);
                var maxX = structuralRegions.Max(r => r.Region.Right);
                var maxY = structuralRegions.Max(r => r.Region.Bottom);
                preliminaryBounds = new NormalizedRectangle(minX, minY, maxX - minX, maxY - minY).Clamp();
            }

            // 4. OpenCV Color Region Analysis in Lab space (Sequential step 3)
            var colorRegions = _colorAnalyzer.AnalyzeColorRegions(workingImage, preliminaryBounds);

            cancellationToken.ThrowIfCancellationRequested();

            // 5. OpenCV Hardware Candidate Analysis (Sequential step 4)
            var hardwareCandidates = _hardwareDetector.DetectHardwareCandidates(workingImage, preliminaryBounds);

            // 6. Assemble Contract (Sequential step 5)
            sw.Stop();
            var contract = _contractBuilder.BuildContract(
                fileName,
                sourceSha256,
                workingImage,
                detectedObjects,
                structuralRegions,
                hardwareCandidates,
                colorRegions,
                sw.Elapsed,
                modelInfo,
                status,
                warnings);

            _logger.LogInformation(
                "Local analysis completed for {FileName} in {DurationMs:F0}ms with confidence {Confidence:F2} (Status={Status})",
                fileName, sw.Elapsed.TotalMilliseconds, contract.LocalConfidence, status);

            var absence = _absenceDetector.Detect(imagePath, contract, string.Empty);
            var details = _sceneDetailDetector.Detect(imagePath, contract, string.Empty);
            var sceneColors = _sceneColorAnalyzer.ExtractSceneColorReferences(workingImage, preliminaryBounds);
            var finalContract = contract with
            {
                ProtectedAbsenceRegions = absence.ToList(),
                PersistentSceneDetails = details.ToList(),
                SceneColorReferences = sceneColors
            };

            var conflictReport = _conflictDetector.DetectAndResolveConflicts(finalContract);
            return finalContract with
            {
                AuthorityPolicyVersion = LocalContractConflictDetector.CurrentAuthorityPolicyVersion,
                FurnitureEnvelopeAuthority = conflictReport.EnvelopeAuthority,
                SceneColorReferences = conflictReport.ResolvedSceneColorReferences,
                AuthorityRecords = conflictReport.AuthorityRecords,
                Conflicts = conflictReport.Conflicts
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception during local analysis of {FileName}", fileName);
            return new LocalFurnitureContract
            {
                SourceFileName = fileName,
                SourceSha256 = sourceSha256,
                Status = LocalAnalysisStatus.Failed,
                Warnings = new List<string> { $"Unhandled exception: {ex.Message}" },
                AnalysisDuration = sw.Elapsed
            };
        }
        finally
        {
            workingImage?.Dispose();
        }
    }
}
