using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using OpenIMG.Application.Abstractions.Analysis;
using OpenIMG.Application.Configuration;
using OpenIMG.Core.Models;

namespace OpenIMG.Infrastructure.Analysis;

public class OnnxObjectDetector : ILocalObjectDetector, IDisposable
{
    private readonly ILogger<OnnxObjectDetector> _logger;
    private readonly OpenImgOptions _options;
    private readonly InferenceSession? _session;
    private readonly LocalModelInfo? _modelInfo;
    private readonly List<string> _classNames = new();
    private bool _disposed;

    public bool IsModelAvailable => _session != null;

    public OnnxObjectDetector(
        IOptions<OpenImgOptions> options,
        ILogger<OnnxObjectDetector> logger)
    {
        _logger = logger;
        _options = options.Value;

        var modelsDir = ResolveModelsDirectory(_options.ModelsDirectory);
        var detectionDir = Path.Combine(modelsDir, "detection");
        var modelPath = Path.Combine(detectionDir, "yolo11n.onnx");
        var classesPath = Path.Combine(detectionDir, "classes.json");
        var manifestPath = Path.Combine(detectionDir, "model_manifest.json");

        if (!File.Exists(modelPath))
        {
            _logger.LogWarning("ONNX object detection model not found at {ModelPath}. Local analysis will report ModelUnavailable.", modelPath);
            return;
        }

        try
        {
            LoadClasses(classesPath);

            var sessionOptions = new SessionOptions
            {
                IntraOpNumThreads = Math.Max(1, _options.OnnxIntraOpThreads),
                InterOpNumThreads = Math.Max(1, _options.OnnxInterOpThreads),
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
            };

            _session = new InferenceSession(modelPath, sessionOptions);

            _modelInfo = LoadModelInfo(modelPath, manifestPath);
            _logger.LogInformation("ONNX object detection session initialized successfully: {ModelName} ({Precision})",
                _modelInfo?.ModelName ?? "yolo11n", _modelInfo?.Precision ?? "FP32");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize ONNX object detection session from {ModelPath}", modelPath);
            _session?.Dispose();
            _session = null;
        }
    }

    public LocalModelInfo? GetModelInfo() => _modelInfo;

    public Task<List<LocalDetectedObject>> DetectObjectsAsync(Mat image, CancellationToken cancellationToken = default)
    {
        if (_session == null || image.Empty())
        {
            return Task.FromResult(new List<LocalDetectedObject>());
        }

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var origW = image.Width;
            var origH = image.Height;
            const int targetSize = 640;

            // 1. Aspect-ratio preserving letterbox
            var scale = Math.Min((float)targetSize / origW, (float)targetSize / origH);
            var scaledW = (int)Math.Round(origW * scale);
            var scaledH = (int)Math.Round(origH * scale);
            var padX = (targetSize - scaledW) / 2;
            var padY = (targetSize - scaledH) / 2;

            using var resized = new Mat();
            Cv2.Resize(image, resized, new Size(scaledW, scaledH), 0, 0, InterpolationFlags.Linear);

            using var letterbox = new Mat(targetSize, targetSize, MatType.CV_8UC3, new Scalar(114, 114, 114));
            var roi = new Rect(padX, padY, scaledW, scaledH);
            using var subLetterbox = new Mat(letterbox, roi);
            resized.CopyTo(subLetterbox);

            // 2. Convert to RGB and normalize float32 [0.0, 1.0] in CHW layout
            using var rgb = new Mat();
            Cv2.CvtColor(letterbox, rgb, ColorConversionCodes.BGR2RGB);

            var tensorData = new float[3 * targetSize * targetSize];
            unsafe
            {
                var ptr = (byte*)rgb.Data.ToPointer();
                var channelStride = targetSize * targetSize;
                for (var y = 0; y < targetSize; y++)
                {
                    var rowPtr = ptr + (y * rgb.Step());
                    for (var x = 0; x < targetSize; x++)
                    {
                        var pixelPtr = rowPtr + (x * 3);
                        var idx = y * targetSize + x;
                        tensorData[idx] = pixelPtr[0] / 255.0f;                       // R
                        tensorData[channelStride + idx] = pixelPtr[1] / 255.0f;       // G
                        tensorData[(2 * channelStride) + idx] = pixelPtr[2] / 255.0f; // B
                    }
                }
            }

            var inputTensor = new DenseTensor<float>(tensorData, new[] { 1, 3, targetSize, targetSize });
            var inputs = new[] { NamedOnnxValue.CreateFromTensor("images", inputTensor) };

            // 3. Inference
            using var results = _session.Run(inputs);
            cancellationToken.ThrowIfCancellationRequested();

            var outputValue = results.FirstOrDefault();
            if (outputValue == null) return new List<LocalDetectedObject>();

            var outputTensor = outputValue.AsTensor<float>();
            var dims = outputTensor.Dimensions;
            // Expected dims: [1, 84, 8400] or [1, 8400, 84]
            int numChannels;
            int numPredictions;
            bool channelsFirst;

            if (dims.Length == 3 && dims[1] < dims[2])
            {
                numChannels = dims[1];
                numPredictions = dims[2];
                channelsFirst = true;
            }
            else if (dims.Length == 3)
            {
                numPredictions = dims[1];
                numChannels = dims[2];
                channelsFirst = false;
            }
            else
            {
                return new List<LocalDetectedObject>();
            }

            var numClasses = Math.Max(0, numChannels - 4);
            const float scoreThreshold = 0.25f;
            var rawDetections = new List<(NormalizedRectangle Box, float Score, int ClassId)>();

            for (var p = 0; p < numPredictions; p++)
            {
                var maxScore = 0.0f;
                var maxClass = -1;

                for (var c = 0; c < numClasses; c++)
                {
                    var score = channelsFirst
                        ? outputTensor[0, 4 + c, p]
                        : outputTensor[0, p, 4 + c];

                    if (score > maxScore)
                    {
                        maxScore = score;
                        maxClass = c;
                    }
                }

                if (maxScore < scoreThreshold || maxClass < 0) continue;

                var cx = channelsFirst ? outputTensor[0, 0, p] : outputTensor[0, p, 0];
                var cy = channelsFirst ? outputTensor[0, 1, p] : outputTensor[0, p, 1];
                var w = channelsFirst ? outputTensor[0, 2, p] : outputTensor[0, p, 2];
                var h = channelsFirst ? outputTensor[0, 3, p] : outputTensor[0, p, 3];

                // Un-pad and un-scale back to original image normalized coords
                var origX = ((cx - w / 2.0f) - padX) / scale / origW;
                var origY = ((cy - h / 2.0f) - padY) / scale / origH;
                var origWidth = (w / scale) / origW;
                var origHeight = (h / scale) / origH;

                var rect = new NormalizedRectangle(origX, origY, origWidth, origHeight).Clamp();
                if (rect.Area > 0.001)
                {
                    rawDetections.Add((rect, maxScore, maxClass));
                }
            }

            // 4. Non-Maximum Suppression (NMS)
            const double iouThreshold = 0.45;
            var ordered = rawDetections.OrderByDescending(d => d.Score).ToList();
            var kept = new List<(NormalizedRectangle Box, float Score, int ClassId)>();

            foreach (var candidate in ordered)
            {
                var suppressed = false;
                foreach (var existing in kept)
                {
                    if (existing.ClassId == candidate.ClassId &&
                        existing.Box.CalculateIoU(candidate.Box) > iouThreshold)
                    {
                        suppressed = true;
                        break;
                    }
                }
                if (!suppressed)
                {
                    kept.Add(candidate);
                }
            }

            // 5. Convert to LocalDetectedObject
            var resultsList = new List<LocalDetectedObject>();
            var detId = 1;
            foreach (var (box, score, classId) in kept)
            {
                var className = classId < _classNames.Count ? _classNames[classId] : $"class_{classId}";
                resultsList.Add(new LocalDetectedObject
                {
                    Id = $"obj_{detId++}",
                    ClassName = className,
                    Confidence = Math.Round(score, 3),
                    Region = box,
                    Source = "OnnxDetector"
                });
            }

            return resultsList;
        }, cancellationToken);
    }

    private void LoadClasses(string classesPath)
    {
        if (File.Exists(classesPath))
        {
            try
            {
                var json = File.ReadAllText(classesPath);
                var classes = JsonSerializer.Deserialize<List<string>>(json);
                if (classes != null && classes.Count > 0)
                {
                    _classNames.AddRange(classes);
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse classes.json from {Path}", classesPath);
            }
        }

        // Default 80 COCO classes if file missing
        _classNames.AddRange(new[]
        {
            "person", "bicycle", "car", "motorcycle", "airplane", "bus", "train", "truck", "boat",
            "traffic light", "fire hydrant", "stop sign", "parking meter", "bench", "bird", "cat",
            "dog", "horse", "sheep", "cow", "elephant", "bear", "zebra", "giraffe", "backpack",
            "umbrella", "handbag", "tie", "suitcase", "frisbee", "skis", "snowboard", "sports ball",
            "kite", "baseball bat", "baseball glove", "skateboard", "surfboard", "tennis racket",
            "bottle", "wine glass", "cup", "fork", "knife", "spoon", "bowl", "banana", "apple",
            "sandwich", "orange", "broccoli", "carrot", "hot dog", "pizza", "donut", "cake",
            "chair", "couch", "potted plant", "bed", "dining table", "toilet", "tv", "laptop",
            "mouse", "remote", "keyboard", "cell phone", "microwave", "oven", "toaster", "sink",
            "refrigerator", "book", "clock", "vase", "scissors", "teddy bear", "hair drier", "toothbrush"
        });
    }

    private LocalModelInfo LoadModelInfo(string modelPath, string manifestPath)
    {
        string modelName = "yolo11n";
        string version = "8.3.0";
        string inputSize = "640x640";
        string execProvider = "CPUExecutionProvider";
        string precision = "FP32";

        if (File.Exists(manifestPath))
        {
            try
            {
                var json = File.ReadAllText(manifestPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("ModelName", out var n)) modelName = n.GetString() ?? modelName;
                if (doc.RootElement.TryGetProperty("ModelVersion", out var v)) version = v.GetString() ?? version;
                if (doc.RootElement.TryGetProperty("InputSize", out var s)) inputSize = s.GetString() ?? inputSize;
                if (doc.RootElement.TryGetProperty("ExecutionProvider", out var e)) execProvider = e.GetString() ?? execProvider;
                if (doc.RootElement.TryGetProperty("Precision", out var p)) precision = p.GetString() ?? precision;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read model manifest from {Path}", manifestPath);
            }
        }

        var sha256 = string.Empty;
        try
        {
            using var stream = File.OpenRead(modelPath);
            sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToUpperInvariant();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to compute model SHA256 for {Path}", modelPath);
        }

        return new LocalModelInfo
        {
            ModelName = modelName,
            ModelVersion = version,
            ModelSha256 = sha256,
            InputSize = inputSize,
            ExecutionProvider = execProvider,
            Precision = precision
        };
    }

    private static string ResolveModelsDirectory(string configuredDir)
    {
        if (Path.IsPathRooted(configuredDir) && Directory.Exists(configuredDir))
            return configuredDir;

        var startDirs = new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
        foreach (var start in startDirs)
        {
            var current = start;
            while (!string.IsNullOrEmpty(current))
            {
                var candidate = Path.Combine(current, configuredDir);
                if (Directory.Exists(candidate))
                    return candidate;

                if (File.Exists(Path.Combine(current, "OpenIMG.slnx")))
                {
                    var slnCandidate = Path.Combine(current, configuredDir);
                    if (Directory.Exists(slnCandidate))
                        return slnCandidate;
                }

                var parent = Directory.GetParent(current);
                if (parent == null) break;
                current = parent.FullName;
            }
        }

        return Path.Combine(AppContext.BaseDirectory, configuredDir);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session?.Dispose();
    }
}
