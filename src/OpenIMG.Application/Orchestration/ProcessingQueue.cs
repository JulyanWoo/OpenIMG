using Microsoft.Extensions.Logging;
using OpenIMG.Application.Abstractions.Orchestration;
using OpenIMG.Application.Abstractions.Persistence;
using OpenIMG.Core.Entities;
using OpenIMG.Core.Enums;
using OpenIMG.Application.Policies;

namespace OpenIMG.Application.Orchestration;

public class ProcessingQueue : IProcessingQueue
{
    private readonly IImageProcessingOrchestrator _orchestrator;
    private readonly IImageAssetRepository _assetRepository;
    private readonly IProjectRepository _projectRepository;
    private readonly ILogger<ProcessingQueue> _logger;

    private readonly SemaphoreSlim _loopLock = new(1, 1);
    private CancellationTokenSource? _queueCts;
    private CancellationTokenSource? _currentItemCts;
    private Task? _queueLoopTask;
    private bool _isRunning;
    private bool _isPaused;
    private bool _isResetting;
    private ImageAsset? _currentProcessingAsset;
    private string _currentStatusMessage = "Queue idle";
    private int _totalItems;
    private int _processedItems;

    public bool IsRunning => _isRunning;
    public bool IsPaused => _isPaused;
    public ImageAsset? CurrentProcessingAsset => _currentProcessingAsset;
    public string CurrentStatusMessage => _currentStatusMessage;
    public int TotalItems => _totalItems;
    public int ProcessedItems => _processedItems;

    public event EventHandler<QueueStateChangedEventArgs>? StateChanged;
    public event EventHandler<AssetProgressEventArgs>? AssetProgress;

    public ProcessingQueue(
        IImageProcessingOrchestrator orchestrator,
        IImageAssetRepository assetRepository,
        IProjectRepository projectRepository,
        ILogger<ProcessingQueue> logger)
    {
        _orchestrator = orchestrator;
        _assetRepository = assetRepository;
        _projectRepository = projectRepository;
        _logger = logger;
    }

    public async Task StartAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await _loopLock.WaitAsync(cancellationToken);
        try
        {
            if (_isResetting)
                throw new InvalidOperationException("The processing queue cannot start while its project is being reset.");

            if (_isRunning)
            {
                if (_isPaused)
                {
                    _isPaused = false;
                    NotifyStateChanged("Queue resumed.");
                }
                return;
            }

            _isRunning = true;
            _isPaused = false;
            _queueCts = new CancellationTokenSource();
            var queueToken = _queueCts.Token;
            NotifyStateChanged("Queue started.");

            _queueLoopTask = Task.Run(() => ProcessQueueLoopAsync(projectId, queueToken), CancellationToken.None);
        }
        finally
        {
            _loopLock.Release();
        }
    }

    public async Task PauseAsync()
    {
        await _loopLock.WaitAsync();
        try
        {
            if (!_isRunning || _isPaused) return;

            _isPaused = true;
            NotifyStateChanged("Queue paused. Finishing current step...");
        }
        finally
        {
            _loopLock.Release();
        }
    }

    public async Task ResumeAsync()
    {
        await _loopLock.WaitAsync();
        try
        {
            if (!_isRunning || !_isPaused) return;

            _isPaused = false;
            NotifyStateChanged("Queue resumed.");
        }
        finally
        {
            _loopLock.Release();
        }
    }

    public async Task CancelCurrentAsync()
    {
        await _loopLock.WaitAsync();
        try
        {
            _currentItemCts?.Cancel();
            NotifyStateChanged("Cancelling current item...");
        }
        finally
        {
            _loopLock.Release();
        }
    }

    public async Task RetryAssetAsync(Guid assetId, CancellationToken cancellationToken = default)
    {
        var asset = await _assetRepository.GetByIdAsync(assetId, cancellationToken);
        if (asset != null)
        {
            asset.Status = ImageProcessingStatus.Pending;
            await _assetRepository.UpdateAsync(asset, cancellationToken);
            NotifyStateChanged($"Asset {asset.OriginalFileName} marked for retry.");
        }
    }

    public async Task SkipAssetAsync(Guid assetId, CancellationToken cancellationToken = default)
    {
        var asset = await _assetRepository.GetByIdAsync(assetId, cancellationToken);
        if (asset != null)
        {
            asset.Status = ImageProcessingStatus.NeedsReview;
            await _assetRepository.UpdateAsync(asset, cancellationToken);
            NotifyStateChanged($"Asset {asset.OriginalFileName} skipped and sent to Review.");
        }
    }

    public async Task StopAsync()
    {
        Task? queueLoopTask;
        await _loopLock.WaitAsync();
        try
        {
            _queueCts?.Cancel();
            _currentItemCts?.Cancel();
            _isPaused = false;
            queueLoopTask = _queueLoopTask;
            if (_isRunning)
                NotifyStateChanged("Stopping queue and waiting for the current image...");
        }
        finally
        {
            _loopLock.Release();
        }

        if (queueLoopTask != null)
            await queueLoopTask;

        await _loopLock.WaitAsync();
        try
        {
            if (ReferenceEquals(queueLoopTask, _queueLoopTask))
            {
                _queueLoopTask = null;
                _queueCts?.Dispose();
                _queueCts = null;
                _currentItemCts?.Dispose();
                _currentItemCts = null;
            }

            _isRunning = false;
            _isPaused = false;
            _currentProcessingAsset = null;
            NotifyStateChanged("Queue stopped.");
        }
        finally
        {
            _loopLock.Release();
        }
    }

    public async Task<int> ResetProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        Task? queueLoopTask;
        await _loopLock.WaitAsync(cancellationToken);
        try
        {
            if (_isResetting)
                throw new InvalidOperationException("A processing-state reset is already in progress.");

            _isResetting = true;
            _isPaused = false;
            _queueCts?.Cancel();
            _currentItemCts?.Cancel();
            queueLoopTask = _queueLoopTask;
            NotifyStateChanged("Waiting for the active image to stop safely before resetting states...");
        }
        finally
        {
            _loopLock.Release();
        }

        try
        {
            // Once cancellation starts, always wait for the current operation to
            // finish its persistence/recovery path before resetting database state.
            if (queueLoopTask != null)
                await queueLoopTask;

            cancellationToken.ThrowIfCancellationRequested();
            var resetCount = await _assetRepository.ResetProjectProcessingStateAsync(projectId, cancellationToken);

            await _loopLock.WaitAsync(CancellationToken.None);
            try
            {
                _queueLoopTask = null;
                _queueCts?.Dispose();
                _queueCts = null;
                _currentItemCts?.Dispose();
                _currentItemCts = null;
                _isRunning = false;
                _isPaused = false;
                _currentProcessingAsset = null;
                _totalItems = 0;
                _processedItems = 0;
                NotifyStateChanged($"Reset complete: {resetCount} image(s) pending. Existing attempts and outputs were preserved.");
            }
            finally
            {
                _loopLock.Release();
            }

            return resetCount;
        }
        finally
        {
            await _loopLock.WaitAsync(CancellationToken.None);
            try
            {
                _isResetting = false;
            }
            finally
            {
                _loopLock.Release();
            }
        }
    }

    private async Task ProcessQueueLoopAsync(Guid projectId, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Processing queue loop started for project {ProjectId}", projectId);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_isPaused)
                {
                    await Task.Delay(500, cancellationToken);
                    continue;
                }

                var project = await _projectRepository.GetByIdAsync(projectId, cancellationToken);
                if (project == null)
                {
                    _logger.LogError("Project {ProjectId} not found in database", projectId);
                    break;
                }

                // Query pending or in-progress items that were interrupted across restarts
                var pendingAssets = await _assetRepository.GetByProjectIdAsync(projectId, ImageProcessingStatus.Pending, cancellationToken);
                var analyzingAssets = await _assetRepository.GetByProjectIdAsync(projectId, ImageProcessingStatus.Analyzing, cancellationToken);
                var restoringAssets = await _assetRepository.GetByProjectIdAsync(projectId, ImageProcessingStatus.Restoring, cancellationToken);
                var localValAssets = await _assetRepository.GetByProjectIdAsync(projectId, ImageProcessingStatus.LocalValidation, cancellationToken);
                var remoteValAssets = await _assetRepository.GetByProjectIdAsync(projectId, ImageProcessingStatus.RemoteValidation, cancellationToken);

                var allEligible = analyzingAssets
                    .Concat(restoringAssets)
                    .Concat(localValAssets)
                    .Concat(remoteValAssets)
                    .Concat(pendingAssets)
                    .GroupBy(a => a.Id)
                    .Select(g => g.First())
                    .ToList();

                _totalItems = allEligible.Count;
                if (allEligible.Count == 0)
                {
                    _logger.LogInformation("Queue completed: no further pending items for project {ProjectId}", projectId);
                    _currentStatusMessage = "All images processed.";
                    break;
                }

                var nextAsset = allEligible.First();
                _currentProcessingAsset = nextAsset;
                _currentItemCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                try
                {
                    NotifyProgress(nextAsset.Id, nextAsset.OriginalFileName, "Starting processing", 1);

                    var result = await _orchestrator.ProcessAssetAsync(
                        project,
                        nextAsset,
                        step => NotifyProgress(nextAsset.Id, nextAsset.OriginalFileName, step, 1),
                        _currentItemCts.Token);

                    _processedItems++;
                    _currentProcessingAsset = null;
                    NotifyProgress(
                        nextAsset.Id,
                        nextAsset.OriginalFileName,
                        $"Processing finished: {result.Decision}",
                        1,
                        isTerminal: true);
                    if (result.QueuePauseRequested)
                    {
                        _isPaused = true;
                        NotifyStateChanged(result.ErrorMessage ?? "GPT reported a warning or provider failure; the queue is paused before the next image.");
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Item {FileName} was cancelled", nextAsset.OriginalFileName);
                    var generationMayExist = nextAsset.ProcessingJobs
                        .SelectMany(job => job.Attempts)
                        .Any(attempt => ProviderOperationRecoveryPolicy.MayHaveSubmittedGeneration(attempt.ProviderOperationState));
                    nextAsset.Status = generationMayExist
                        ? ImageProcessingStatus.NeedsReview
                        : cancellationToken.IsCancellationRequested
                            ? ImageProcessingStatus.Pending
                            : ImageProcessingStatus.Cancelled;
                    await _assetRepository.UpdateAsync(nextAsset, CancellationToken.None);
                    var progressMessage = generationMayExist
                        ? "GPT operation requires recovery review"
                        : cancellationToken.IsCancellationRequested
                            ? "Interrupted work saved for resume"
                            : "Processing cancelled";
                    NotifyProgress(nextAsset.Id, nextAsset.OriginalFileName, progressMessage, 1, isTerminal: true);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error processing {FileName}", nextAsset.OriginalFileName);
                    nextAsset.Status = ImageProcessingStatus.Failed;
                    await _assetRepository.UpdateAsync(nextAsset, CancellationToken.None);
                    NotifyProgress(nextAsset.Id, nextAsset.OriginalFileName, "Processing failed; see logs", 1, isTerminal: true);
                }
                finally
                {
                    _currentItemCts?.Dispose();
                    _currentItemCts = null;
                    _currentProcessingAsset = null;
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Processing queue loop cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in processing queue loop");
        }
        finally
        {
            _isRunning = false;
            _isPaused = false;
            _currentProcessingAsset = null;
            NotifyStateChanged("Queue finished or idle.");
        }
    }

    private void NotifyStateChanged(string message)
    {
        _currentStatusMessage = message;
        StateChanged?.Invoke(this, new QueueStateChangedEventArgs
        {
            IsRunning = _isRunning,
            IsPaused = _isPaused,
            Message = message
        });
    }

    private void NotifyProgress(Guid assetId, string fileName, string step, int attempt, bool isTerminal = false)
    {
        _currentStatusMessage = $"{fileName}: {step}";
        AssetProgress?.Invoke(this, new AssetProgressEventArgs
        {
            AssetId = assetId,
            FileName = fileName,
            Step = step,
            AttemptNumber = attempt,
            IsTerminal = isTerminal
        });
    }
}
