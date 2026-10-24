using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using OpenIMG.App.Helpers;
using OpenIMG.App.Services;
using OpenIMG.Application.Abstractions.AI;
using OpenIMG.Application.Abstractions.Orchestration;
using OpenIMG.Application.Abstractions.Persistence;
using OpenIMG.Application.Abstractions.Storage;
using OpenIMG.Application.Configuration;
using OpenIMG.Application.Importing;
using OpenIMG.Application.Restoration;
using OpenIMG.Infrastructure.Storage;
using OpenIMG.Core.Entities;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;

namespace OpenIMG.App.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    public string ReleaseVersion => OpenImgBuildIdentity.DisplayedVersion;

    private readonly IProjectRepository _projectRepository;
    private readonly IImageAssetRepository _assetRepository;
    private readonly IFileSystemService _fileService;
    private readonly IProcessingQueue _queue;
    private readonly IGptWebClient _webClient;
    private readonly ProjectSourceFolderMonitor _sourceFolderMonitor;
    private readonly ProjectSourceImageImporter _sourceImageImporter;
    private readonly OpenImgOptions _options;
    private readonly ILogger<MainViewModel> _logger;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private bool _disposed;

    [ObservableProperty]
    private Project? _currentProject;

    [ObservableProperty]
    private string _selectedFilter = "All";

    [ObservableProperty]
    private AssetItemViewModel? _selectedAsset;

    [ObservableProperty]
    private BitmapSource? _selectedOriginalBitmap;

    [ObservableProperty]
    private BitmapSource? _selectedRestoredBitmap;

    [ObservableProperty]
    private string _originalPreviewMessage = "Selecciona una foto para comenzar.";

    [ObservableProperty]
    private string _restoredPreviewMessage = "El resultado GPT aparecerá aquí cuando termine el procesamiento.";

    [ObservableProperty]
    private bool _isBrowserVisible = true;

    [ObservableProperty]
    private string _gptStatusText = "Connecting...";

    [ObservableProperty]
    private bool _isGptReady;

    [ObservableProperty]
    private bool _isQueueRunning;

    [ObservableProperty]
    private bool _isQueuePaused;

    [ObservableProperty]
    private bool _isResettingProcessingStates;

    [ObservableProperty]
    private string _queueStatusText = "Ready";

    [ObservableProperty]
    private double _queueProgress;

    private int _previewRequestVersion;

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private int _pendingCount;

    [ObservableProperty]
    private int _processingCount;

    [ObservableProperty]
    private int _acceptedCount;

    [ObservableProperty]
    private int _reviewCount;

    [ObservableProperty]
    private int _failedCount;

    public ObservableCollection<AssetItemViewModel> AllAssets { get; } = new();
    public ObservableCollection<AssetItemViewModel> FilteredAssets { get; } = new();
    public ObservableCollection<ValidationEvidence> CurrentEvidences { get; } = new();

    public MainViewModel(
        IProjectRepository projectRepository,
        IImageAssetRepository assetRepository,
        IFileSystemService fileService,
        IProcessingQueue queue,
        IGptWebClient webClient,
        ProjectSourceFolderMonitor sourceFolderMonitor,
        ProjectSourceImageImporter sourceImageImporter,
        OpenImgOptions options,
        ILogger<MainViewModel> logger,
        ImageRetryService retryService,
        ResultCopyService resultCopies)
    {
        _projectRepository = projectRepository;
        _assetRepository = assetRepository;
        _fileService = fileService;
        _queue = queue;
        _webClient = webClient;
        _sourceFolderMonitor = sourceFolderMonitor;
        _sourceImageImporter = sourceImageImporter;
        _options = options;
        _logger = logger;
        _retryService = retryService;
        _resultCopies = resultCopies;

        _queue.StateChanged += OnQueueStateChanged;
        _queue.AssetProgress += OnAssetProgress;
        _sourceFolderMonitor.FolderChanged += OnSourceFolderChanged;
    }

    public async Task InitializeAsync()
    {
        // Load default or last project
        var projects = await _projectRepository.GetAllAsync();
        if (projects.Count > 0)
        {
            CurrentProject = projects[0];
            await LoadAssetsForProjectAsync(CurrentProject.Id);
            await WatchAndReconcileSourceFolderAsync(CurrentProject);
        }
        else
        {
            // Create default project
            var defaultProj = new Project
            {
                Name = "Mi biblioteca",
                SourceDirectory = string.Empty,
                OutputDirectory = _options.DefaultOutputDirectory
            };
            CurrentProject = await _projectRepository.CreateAsync(defaultProj);
            _fileService.EnsureProjectOutputDirectories(CurrentProject.OutputDirectory);
        }

        _ = MonitorGptStatusAsync(_lifetimeCts.Token);
        _ = RestoreAutomaticCopiesAsync();
    }

    private async Task MonitorGptStatusAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var report = await _webClient.AssessReadinessAsync(cancellationToken);
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (!report.PanelActivated || !report.IsVisible || !report.WebViewLoaded || !report.CoreWebView2Initialized)
                    {
                        GptStatusText = "GPT Web Loading...";
                        IsGptReady = false;
                    }
                    else if (string.Equals(report.AuthenticationState, "AuthenticationRequired", StringComparison.OrdinalIgnoreCase))
                    {
                        GptStatusText = "Sign in to GPT Web";
                        IsGptReady = false;
                    }
                    else if (report.IsReady)
                    {
                        GptStatusText = "GPT Connected & Ready";
                        IsGptReady = true;
                    }
                    else
                    {
                        GptStatusText = $"GPT not ready ({report.FailureStage})";
                        IsGptReady = false;
                    }
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // Browser might still be initializing
            }

            try
            {
                await Task.Delay(5000, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    public async Task LoadAssetsForProjectAsync(Guid projectId)
    {
        var assets = await _assetRepository.GetByProjectIdAsync(projectId);
        AllAssets.Clear();
        _sourceImageImporter.ResetKnownSourcePaths(assets.Select(asset => asset.OriginalFilePath));

        var projectOutputDirectory = CurrentProject?.OutputDirectory ?? _options.DefaultOutputDirectory;
        var thumbDir = Path.Combine(projectOutputDirectory, "Temp", "Thumbnails");

        foreach (var a in assets)
        {
            var vm = new AssetItemViewModel(a);
            AllAssets.Add(vm);

            // Lazy thumbnail generation if missing
            if (string.IsNullOrEmpty(vm.ThumbnailPath) && File.Exists(a.OriginalFilePath))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var thumb = await _fileService.GenerateThumbnailAsync(a.OriginalFilePath, thumbDir);
                        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            a.ThumbnailPath = thumb;
                            vm.ThumbnailPath = thumb;
                        });
                        await _assetRepository.UpdateAsync(a);
                    }
                    catch
                    {
                        // Ignore thumbnail generation errors
                    }
                });
            }
        }

        UpdateCounts();
        var resumableCount = assets.Count(asset => asset.Status is ImageProcessingStatus.Pending or
            ImageProcessingStatus.Analyzing or ImageProcessingStatus.Restoring or
            ImageProcessingStatus.LocalValidation or ImageProcessingStatus.RemoteValidation);
        QueueStatusText = resumableCount > 0
            ? $"{resumableCount} foto(s) pendientes o recuperables. Iniciar cola continúa el trabajo guardado; las fotos terminadas no se repiten."
            : "No hay fotos pendientes. Los resultados e historial guardados se mantienen.";
        ApplyFilter(SelectedFilter);
    }

    [RelayCommand]
    public async Task ImportFolderAsync(string? folderPath = null)
    {
        if (CurrentProject == null) return;

        var targetFolder = folderPath;
        if (string.IsNullOrEmpty(targetFolder))
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select Folder Containing Furniture Photographs"
            };
            if (dialog.ShowDialog() == true)
            {
                targetFolder = dialog.FolderName;
            }
        }

        if (string.IsNullOrEmpty(targetFolder) || !Directory.Exists(targetFolder))
            return;

        targetFolder = Path.GetFullPath(targetFolder);
        CurrentProject.SourceDirectory = targetFolder;
        await _projectRepository.UpdateAsync(CurrentProject);
        _sourceFolderMonitor.Watch(targetFolder);

        var files = _fileService.GetSupportedImageFiles(targetFolder);
        var result = await ImportFilesIntoLibraryAsync(targetFolder, files, waitForStability: false);
        _logger.LogInformation(
            "Folder import complete: {Imported} new assets added, {Duplicates} duplicates skipped, {Skipped} files deferred or invalid.",
            result.Imported,
            result.Duplicates,
            result.Skipped);
    }

    private async Task WatchAndReconcileSourceFolderAsync(Project project)
    {
        var folder = project.SourceDirectory;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return;

        try
        {
            folder = Path.GetFullPath(folder);
            _sourceFolderMonitor.Watch(folder);

            // Reconcile after installing the watcher so additions racing with this scan
            // are observed either here or by the watcher (duplicate paths are guarded).
            var untrackedFiles = _fileService.GetSupportedImageFiles(folder)
                .Where(path => !_sourceImageImporter.IsKnownSourcePath(path))
                .ToArray();
            if (untrackedFiles.Length > 0)
            {
                var result = await ImportFilesIntoLibraryAsync(folder, untrackedFiles, waitForStability: false);
                _logger.LogInformation(
                    "Source-folder reconciliation imported {Imported} new files and skipped {Duplicates} duplicates.",
                    result.Imported,
                    result.Duplicates);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not watch or reconcile project source folder {SourceFolder}", folder);
        }
    }

    private async void OnSourceFolderChanged(object? sender, SourceFolderChangedEventArgs args)
    {
        try
        {
            var project = CurrentProject;
            if (project == null || !AreSamePath(project.SourceDirectory, args.FolderPath))
                return;

            var supportedFiles = _fileService.GetSupportedImageFiles(args.FolderPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var paths = args.RequiresRescan
                ? supportedFiles
                : args.ChangedPaths.Where(supportedFiles.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
            paths.RemoveWhere(_sourceImageImporter.IsKnownSourcePath);

            if (paths.Count == 0)
                return;

            var result = await ImportFilesIntoLibraryAsync(args.FolderPath, paths, waitForStability: true);
            if (result.Imported > 0)
            {
                _logger.LogInformation(
                    "Automatically imported {Imported} new image(s) from {SourceFolder}.",
                    result.Imported,
                    args.FolderPath);
            }
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // The application is closing.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not import newly added images from {SourceFolder}", args.FolderPath);
        }
    }

    private async Task<SourceImageImportResult> ImportFilesIntoLibraryAsync(
        string sourceFolder,
        IEnumerable<string> filePaths,
        bool waitForStability)
    {
        var project = CurrentProject;
        if (project == null)
            return new SourceImageImportResult([], 0, 0);

        var result = await _sourceImageImporter.ImportAsync(
            project,
            sourceFolder,
            filePaths,
            waitForStability,
            _lifetimeCts.Token);
        if (result.ImportedAssets.Count > 0)
        {
            await InvokeOnUiThreadAsync(() =>
            {
                foreach (var asset in result.ImportedAssets)
                    AllAssets.Add(new AssetItemViewModel(asset));

                UpdateCounts();
                ApplyFilter(SelectedFilter);
            });
        }

        return result;
    }

    private static bool AreSamePath(string first, string second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
            return false;

        try
        {
            return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static async Task InvokeOnUiThreadAsync(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        await dispatcher.InvokeAsync(action, DispatcherPriority.DataBind).Task;
    }

    [RelayCommand]
    public void SetFilter(string filter)
    {
        SelectedFilter = filter;
        ApplyFilter(filter);
    }

    private void ApplyFilter(string filter)
    {
        FilteredAssets.Clear();

        IEnumerable<AssetItemViewModel> query = filter switch
        {
            "Pending" => AllAssets.Where(a => a.Status == ImageProcessingStatus.Pending),
            "Processing" => AllAssets.Where(a => a.Status == ImageProcessingStatus.Analyzing ||
                                                a.Status == ImageProcessingStatus.Restoring ||
                                                a.Status == ImageProcessingStatus.LocalValidation ||
                                                a.Status == ImageProcessingStatus.RemoteValidation),
            "Accepted" => AllAssets.Where(a => a.Status == ImageProcessingStatus.Accepted),
            "Review" => AllAssets.Where(a => a.Status == ImageProcessingStatus.NeedsReview && a.WorkState.HasCurrentResult),
            "Failed" => AllAssets.Where(a => a.Status == ImageProcessingStatus.Failed),
            "Missing" => AllAssets.Where(a => a.WorkState.MissingResult),
            "Recoverable" => AllAssets.Where(a => a.WorkState.Recoverable),
            "Manual" => AllAssets.Where(a => a.WorkState.ManualRecoveryRequired),
            "Saved" => AllAssets.Where(a => a.RestoredImagePath is not null),
            _ => AllAssets
        };

        if (!string.IsNullOrWhiteSpace(SearchText))
            query = query.Where(item => item.FileName.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase));

        foreach (var item in query)
        {
            FilteredAssets.Add(item);
        }

        if (SelectedAsset == null || !FilteredAssets.Contains(SelectedAsset))
            SelectedAsset = FilteredAssets.FirstOrDefault();
    }

    partial void OnSelectedAssetChanged(AssetItemViewModel? value)
    {
        LoadSelectedAssetImages(value);
        NotifyAutomationControls();
    }

    private void LoadSelectedAssetImages(AssetItemViewModel? vm)
    {
        var requestVersion = Interlocked.Increment(ref _previewRequestVersion);
        CurrentEvidences.Clear();
        SelectedOriginalBitmap = null;
        SelectedRestoredBitmap = null;

        if (vm == null)
        {
            OriginalPreviewMessage = "Selecciona una foto para comenzar.";
            RestoredPreviewMessage = "El resultado GPT aparecerá aquí cuando termine el procesamiento.";
            return;
        }

        var originalPath = vm.OriginalFilePath;
        var restoredPath = vm.RestoredImagePath;
        OriginalPreviewMessage = "Cargando foto original…";
        RestoredPreviewMessage = string.IsNullOrWhiteSpace(restoredPath)
            ? "Aún no hay un resultado restaurado para esta foto."
            : "Cargando resultado GPT…";
        _ = LoadSelectedAssetImagesAsync(vm, originalPath, restoredPath, requestVersion);
    }

    private async Task LoadSelectedAssetImagesAsync(
        AssetItemViewModel vm,
        string originalPath,
        string? restoredPath,
        int requestVersion)
    {
        var previews = await Task.Run(() => (
            Original: LoadPreview(originalPath, "No se encontró la foto original.", "original"),
            Restored: LoadPreview(restoredPath, "Aún no hay un resultado restaurado para esta foto.", "restaurada")));

        if (requestVersion != Volatile.Read(ref _previewRequestVersion) || !ReferenceEquals(SelectedAsset, vm))
            return;

        SelectedOriginalBitmap = previews.Original.Bitmap;
        OriginalPreviewMessage = previews.Original.Message;
        SelectedRestoredBitmap = previews.Restored.Bitmap;
        RestoredPreviewMessage = previews.Restored.Message;
    }

    private (BitmapSource? Bitmap, string Message) LoadPreview(string? path, string missingMessage, string description)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return (null, missingMessage);

        try
        {
            return (BitmapHelper.LoadBitmapWithExifOrientation(path, 1600), string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load the {ImageDescription} preview", description);
            return (null, $"No se pudo cargar la imagen {description}.");
        }
    }

    private async Task RefreshAssetFromPersistenceAsync(Guid assetId)
    {
        try
        {
            var persistedAsset = await _assetRepository.GetByIdAsync(assetId);
            if (persistedAsset == null)
                return;

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var assetViewModel = AllAssets.FirstOrDefault(asset => asset.Id == assetId);
                if (assetViewModel == null)
                    return;

                assetViewModel.RefreshState(persistedAsset);
                UpdateCounts();
                if (SelectedAsset?.Id == assetId)
                    LoadSelectedAssetImages(assetViewModel);
            });
            if (_options.AutomaticResultCopyEnabled && ImageWorkState.Read(persistedAsset).HasCurrentResult)
            {
                try
                {
                    await Task.Run(() => _resultCopies.SaveAsync(persistedAsset, _options.AutomaticResultCopyDirectory, _lifetimeCts.Token));
                    await InvokeOnUiThreadAsync(() => QuickActionMessage = "Copia automática guardada; revisión humana pendiente de tu decisión.");
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    _logger.LogWarning(error, "Automatic review copy failed for asset {AssetId}", assetId);
                    await InvokeOnUiThreadAsync(() => QuickActionMessage = "No se pudo guardar la copia automática. El resultado de OpenIMG se conserva; revisa la carpeta de copias.");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not refresh completed image state for asset {AssetId}", assetId);
        }
    }

    private void UpdateCounts()
    {
        TotalCount = AllAssets.Count;
        PendingCount = AllAssets.Count(a => a.Status == ImageProcessingStatus.Pending);
        ProcessingCount = AllAssets.Count(a => a.Status == ImageProcessingStatus.Analyzing ||
                                               a.Status == ImageProcessingStatus.Restoring ||
                                               a.Status == ImageProcessingStatus.LocalValidation ||
                                               a.Status == ImageProcessingStatus.RemoteValidation);
        AcceptedCount = AllAssets.Count(a => a.Status == ImageProcessingStatus.Accepted);
        ReviewCount = AllAssets.Count(a => a.Status == ImageProcessingStatus.NeedsReview && a.WorkState.HasCurrentResult);
        FailedCount = AllAssets.Count(a => a.Status == ImageProcessingStatus.Failed);
        UpdateQuickCounts();
        ApplyFilter(SelectedFilter);
    }

    [RelayCommand]
    public async Task StartQueueAsync()
    {
        if (CurrentProject == null || !CanUseQuickActions) return;
        if (!IsGptReady)
        {
            IsBrowserVisible = true;
            QueueStatusText = "Inicia sesión en GPT Web. La cola esperará hasta que la sesión esté lista.";
            return;
        }
        try
        {
            if (_queue.IsRunning)
            {
                if (_queue.IsPaused) await _queue.ResumeAsync();
            }
            else
            {
                await _queue.StartAsync(CurrentProject.Id);
            }
        }
        catch (Exception error)
        {
            _logger.LogWarning("Could not start or resume processing queue. Error type: {ErrorType}", error.GetType().Name);
            QueueStatusText = "No se pudo iniciar la cola. Revisa el estado de GPT Web e inténtalo de nuevo.";
        }
        finally
        {
            SyncQueueUiState();
        }
    }

    [RelayCommand]
    public async Task StopQueueAsync()
    {
        try { await _queue.StopAsync(); }
        finally { SyncQueueUiState(); }
    }

    [RelayCommand]
    public async Task PauseQueueAsync()
    {
        try { await _queue.PauseAsync(); }
        finally { SyncQueueUiState(); }
    }

    [RelayCommand]
    public async Task ResumeQueueAsync()
    {
        if (CurrentProject is null || !IsGptReady || !CanUseQuickActions) return;
        try
        {
            if (_queue.IsRunning)
            {
                if (_queue.IsPaused) await _queue.ResumeAsync();
            }
            else
            {
                await _queue.StartAsync(CurrentProject.Id);
            }
        }
        catch (Exception error)
        {
            _logger.LogWarning("Could not resume processing queue. Error type: {ErrorType}", error.GetType().Name);
            QueueStatusText = "La cola sigue detenida. Revisa GPT Web e inténtalo de nuevo.";
        }
        finally { SyncQueueUiState(); }
    }

    [RelayCommand]
    public async Task CancelCurrentAsync()
    {
        await _queue.CancelCurrentAsync();
    }

    [RelayCommand]
    public async Task RetrySelectedAsync()
    {
        if (!CanRetrySelected || SelectedAsset == null) return;
        try { await RetryAssetsAsync([SelectedAsset.Id], false); }
        catch (Exception error)
        {
            _logger.LogWarning(error, "Could not requeue the selected asset");
            QuickActionMessage = "No se pudo preparar el reintento; el historial se conserva. Actualiza los estados y revisa la foto.";
        }
    }

    public async Task ResetAllProcessingStatesAsync()
    {
        if (IsResettingProcessingStates || IsQuickActionRunning)
            return;

        var project = CurrentProject;
        if (project == null)
            return;

        var selectedAssetId = SelectedAsset?.Id;
        IsResettingProcessingStates = true;
        QueueStatusText = "Deteniendo la cola y esperando la imagen activa antes de reiniciar…";
        try
        {
            var resetCount = await _queue.ResetProjectAsync(project.Id, _lifetimeCts.Token);
            SelectedAsset = null;
            await LoadAssetsForProjectAsync(project.Id);
            SelectedAsset = selectedAssetId.HasValue
                ? AllAssets.FirstOrDefault(asset => asset.Id == selectedAssetId.Value)
                : AllAssets.FirstOrDefault();
            QueueStatusText = $"{resetCount} foto(s) listas para comenzar desde el inicio. Historial y resultados conservados.";
        }
        finally
        {
            IsResettingProcessingStates = false;
        }
    }

    [RelayCommand]
    public void ToggleBrowser()
    {
        IsBrowserVisible = !IsBrowserVisible;
    }

    private void OnQueueStateChanged(object? sender, QueueStateChangedEventArgs e)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            IsQueueRunning = e.IsRunning;
            IsQueuePaused = e.IsPaused;
            QueueStatusText = e.Message;
            UpdateCounts();
        });
    }

    private void OnAssetProgress(object? sender, AssetProgressEventArgs e)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            QueueStatusText = $"{e.FileName}: {e.Step}";
            NotifyAutomationControls();
        });

        if (e.IsTerminal)
            _ = RefreshAssetFromPersistenceAsync(e.AssetId);
    }

    private void SyncQueueUiState()
    {
        IsQueueRunning = _queue.IsRunning;
        IsQueuePaused = _queue.IsPaused;
        NotifyAutomationControls();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _lifetimeCts.Cancel();
        _queue.StateChanged -= OnQueueStateChanged;
        _queue.AssetProgress -= OnAssetProgress;
        _sourceFolderMonitor.FolderChanged -= OnSourceFolderChanged;
        _sourceFolderMonitor.Stop();
        _lifetimeCts.Dispose();
    }
}
