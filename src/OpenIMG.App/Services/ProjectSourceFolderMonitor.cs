using System.IO;
using Microsoft.Extensions.Logging;

namespace OpenIMG.App.Services;

public sealed class SourceFolderChangedEventArgs : EventArgs
{
    public string FolderPath { get; }
    public IReadOnlyCollection<string> ChangedPaths { get; }
    public bool RequiresRescan { get; }

    public SourceFolderChangedEventArgs(
        string folderPath,
        IReadOnlyCollection<string> changedPaths,
        bool requiresRescan)
    {
        FolderPath = folderPath;
        ChangedPaths = changedPaths;
        RequiresRescan = requiresRescan;
    }
}

/// <summary>
/// Debounces file-system notifications for the currently selected source folder.
/// The consumer decides how to import the reported paths.
/// </summary>
public sealed class ProjectSourceFolderMonitor : IDisposable
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(650);

    private readonly object _gate = new();
    private readonly ILogger<ProjectSourceFolderMonitor> _logger;
    private readonly HashSet<string> _changedPaths = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private string? _folderPath;
    private bool _requiresRescan;
    private bool _disposed;

    public event EventHandler<SourceFolderChangedEventArgs>? FolderChanged;

    public ProjectSourceFolderMonitor(ILogger<ProjectSourceFolderMonitor> logger)
    {
        _logger = logger;
    }

    public string? WatchedFolder
    {
        get
        {
            lock (_gate)
                return _folderPath;
        }
    }

    public void Watch(string folderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        var fullPath = Path.GetFullPath(folderPath);
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"The source folder does not exist: {fullPath}");

        var watcher = new FileSystemWatcher(fullPath)
        {
            IncludeSubdirectories = false,
            Filter = "*.*",
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = false
        };
        watcher.Created += OnFileChanged;
        watcher.Changed += OnFileChanged;
        watcher.Renamed += OnFileRenamed;
        watcher.Error += OnWatcherError;

        FileSystemWatcher? previousWatcher;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (string.Equals(_folderPath, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                watcher.Dispose();
                return;
            }

            previousWatcher = _watcher;
            _watcher = watcher;
            _folderPath = fullPath;
            _changedPaths.Clear();
            _requiresRescan = false;
            _debounceTimer ??= new Timer(OnDebounceElapsed);
        }

        previousWatcher?.Dispose();
        watcher.EnableRaisingEvents = true;
        _logger.LogInformation("Watching project source folder {SourceFolder}", fullPath);
    }

    public void Stop()
    {
        FileSystemWatcher? watcher;
        lock (_gate)
        {
            watcher = _watcher;
            _watcher = null;
            _folderPath = null;
            _changedPaths.Clear();
            _requiresRescan = false;
            _debounceTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        watcher?.Dispose();
    }

    private void OnFileChanged(object sender, FileSystemEventArgs args) => QueueChange(sender, args.FullPath);

    private void OnFileRenamed(object sender, RenamedEventArgs args) => QueueChange(sender, args.FullPath);

    private void QueueChange(object sender, string fullPath)
    {
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(sender, _watcher))
                return;

            _changedPaths.Add(fullPath);
            _debounceTimer?.Change(DebounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs args)
    {
        _logger.LogWarning(args.GetException(), "Source folder watcher reported an error; a reconciliation scan is required.");
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(sender, _watcher))
                return;

            _requiresRescan = true;
            _debounceTimer?.Change(DebounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnDebounceElapsed(object? state)
    {
        SourceFolderChangedEventArgs? notification;
        lock (_gate)
        {
            if (_disposed || _folderPath is null)
                return;

            notification = new SourceFolderChangedEventArgs(
                _folderPath,
                _changedPaths.ToArray(),
                _requiresRescan);
            _changedPaths.Clear();
            _requiresRescan = false;
        }

        try
        {
            FolderChanged?.Invoke(this, notification);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Source folder change handler failed for {SourceFolder}", notification.FolderPath);
        }
    }

    public void Dispose()
    {
        FileSystemWatcher? watcher;
        Timer? timer;
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            watcher = _watcher;
            timer = _debounceTimer;
            _watcher = null;
            _debounceTimer = null;
            _folderPath = null;
            _changedPaths.Clear();
        }

        watcher?.Dispose();
        timer?.Dispose();
    }
}
