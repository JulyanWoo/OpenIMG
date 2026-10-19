using OpenIMG.Core.Entities;

namespace OpenIMG.Application.Abstractions.Orchestration;

public interface IProcessingQueue
{
    bool IsRunning { get; }
    bool IsPaused { get; }
    ImageAsset? CurrentProcessingAsset { get; }
    string CurrentStatusMessage { get; }
    int TotalItems { get; }
    int ProcessedItems { get; }

    event EventHandler<QueueStateChangedEventArgs>? StateChanged;
    event EventHandler<AssetProgressEventArgs>? AssetProgress;

    Task StartAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task PauseAsync();
    Task ResumeAsync();
    Task CancelCurrentAsync();
    Task RetryAssetAsync(Guid assetId, CancellationToken cancellationToken = default);
    Task SkipAssetAsync(Guid assetId, CancellationToken cancellationToken = default);
    Task StopAsync();
    Task<int> ResetProjectAsync(Guid projectId, CancellationToken cancellationToken = default);
}

public class QueueStateChangedEventArgs : EventArgs
{
    public bool IsRunning { get; init; }
    public bool IsPaused { get; init; }
    public string Message { get; init; } = string.Empty;
}

public class AssetProgressEventArgs : EventArgs
{
    public Guid AssetId { get; init; }
    public string FileName { get; init; } = string.Empty;
    public string Step { get; init; } = string.Empty;
    public int AttemptNumber { get; init; }
    public bool IsTerminal { get; init; }
}
