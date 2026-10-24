using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using OpenIMG.Application.Restoration;
using OpenIMG.Core.Enums;
using OpenIMG.Infrastructure.Configuration;
using OpenIMG.Infrastructure.Storage;

namespace OpenIMG.App.ViewModels;

public partial class MainViewModel
{
    private readonly ImageRetryService _retryService;
    private readonly ResultCopyService _resultCopies;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _quickActionMessage = "Los resultados descargados se guardan automáticamente en la biblioteca.";
    [ObservableProperty] private bool _isQuickActionRunning;
    [ObservableProperty] private int _missingResultCount;
    [ObservableProperty] private int _recoverableCount;
    [ObservableProperty] private int _savedResultCount;
    [ObservableProperty] private int _manualRecoveryCount;
    public bool IsQueueActivelyProcessing => IsQueueRunning && !IsQueuePaused;
    public bool CanUseQuickActions => !IsQuickActionRunning && !IsResettingProcessingStates;
    public string SelectedFilterLabel => SelectedFilter switch
    {
        "Pending" => "Pendientes", "Processing" => "En curso", "Review" => "Revisión con resultado",
        "Failed" => "Fallidas", "Missing" => "Sin resultado del último intento", "Recoverable" => "Descargas recuperables",
        "Manual" => "Sin enlace recuperable", "Saved" => "Resultados disponibles", "Accepted" => "Aceptadas", _ => "Toda la biblioteca"
    };
    public string AutomaticCopiesLabel => _options.AutomaticResultCopyEnabled
        ? "Copias automáticas: " + _options.AutomaticResultCopyDirectory : "Copias automáticas desactivadas";
    public string ResultsDirectory => CurrentProject?.OutputDirectory ?? _options.DefaultOutputDirectory;
    partial void OnSearchTextChanged(string value) => ApplyFilter(SelectedFilter);
    partial void OnSelectedFilterChanged(string value) => OnPropertyChanged(nameof(SelectedFilterLabel));
    partial void OnIsQuickActionRunningChanged(bool value) { OnPropertyChanged(nameof(CanUseQuickActions)); NotifyAutomationControls(); }
    partial void OnIsResettingProcessingStatesChanged(bool value) { OnPropertyChanged(nameof(CanUseQuickActions)); NotifyAutomationControls(); }
    partial void OnIsQueueRunningChanged(bool value) { OnPropertyChanged(nameof(IsQueueActivelyProcessing)); NotifyAutomationControls(); }
    partial void OnIsQueuePausedChanged(bool value) { OnPropertyChanged(nameof(IsQueueActivelyProcessing)); NotifyAutomationControls(); }
    partial void OnIsGptReadyChanged(bool value) => NotifyAutomationControls();
    private void UpdateQuickCounts()
    {
        MissingResultCount = AllAssets.Count(item => item.WorkState.MissingResult);
        RecoverableCount = AllAssets.Count(item => item.WorkState.Recoverable);
        SavedResultCount = AllAssets.Count(item => item.RestoredImagePath is not null);
        ManualRecoveryCount = AllAssets.Count(item => item.WorkState.ManualRecoveryRequired);
        NotifyAutomationControls();
    }

    private static bool MatchesRetryGroup(AssetItemViewModel item, string group) => group switch
    {
        "Failed" => item.Status == ImageProcessingStatus.Failed,
        "Review" => item.Status == ImageProcessingStatus.NeedsReview && item.WorkState.HasCurrentResult,
        "Missing" => item.WorkState.MissingResult,
        "ReplaceMissing" => item.WorkState.MissingResult && item.WorkState.ManualRecoveryRequired,
        _ => false
    };

    private IEnumerable<AssetItemViewModel> RetryGroup(string group) => group == "Visible"
        ? FilteredAssets.Where(item => !ImageWorkState.IsProcessing(item.Status) && item.Status != ImageProcessingStatus.Pending)
        : AllAssets.Where(item => MatchesRetryGroup(item, group));
    public int RetryGroupCount(string group) => RetryGroup(group).Count();

    public Task RetryGroupAsync(string group) => RetryAssetsAsync(
        RetryGroup(group).Select(item => item.Id).ToArray(), group == "ReplaceMissing");

    private async Task RetryAssetsAsync(IReadOnlyCollection<Guid> ids, bool authorizeReplacement)
    {
        if (IsQuickActionRunning || IsResettingProcessingStates || CurrentProject is null) return;
        IsQuickActionRunning = true;
        var queued = 0;
        var manual = 0;
        var skipped = 0;
        try
        {
            QuickActionMessage = $"Preparando reintentos para {ids.Count} foto(s) existentes…";
            foreach (var id in ids)
            {
                if (_queue.CurrentProcessingAsset?.Id == id) { skipped++; continue; }
                var result = await _retryService.EnqueueAsync(id, authorizeReplacement, _lifetimeCts.Token);
                if (result == ImageRetryResult.Queued) queued++;
                else if (result == ImageRetryResult.ManualRecoveryRequired) manual++;
                else skipped++;
            }
            await RefreshLibraryAsync();
            QuickActionMessage = $"{queued} foto(s) encoladas; {manual} requieren enlace o autorización de nueva generación; {skipped} omitidas (en curso, pendientes o sin original).";
            if (queued > 0)
            {
                if (_queue.IsPaused) await _queue.ResumeAsync();
                else if (!_queue.IsRunning) await _queue.StartAsync(CurrentProject.Id);
            }
        }
        finally { IsQuickActionRunning = false; }
    }

    public async Task RefreshLibraryAsync()
    {
        if (CurrentProject is null) return;
        var assets = await _assetRepository.GetByProjectIdAsync(CurrentProject.Id, cancellationToken: _lifetimeCts.Token);
        await InvokeOnUiThreadAsync(() =>
        {
            foreach (var asset in assets)
            {
                var item = AllAssets.FirstOrDefault(existing => existing.Id == asset.Id);
                if (item is not null) item.RefreshState(asset);
                else AllAssets.Add(new AssetItemViewModel(asset));
            }
            UpdateCounts();
            if (SelectedAsset is { } selected) LoadSelectedAssetImages(selected);
        });
    }

    [RelayCommand]
    private async Task RefreshLibrarySafelyAsync()
    {
        try
        {
            await RefreshLibraryAsync();
            QuickActionMessage = "Biblioteca completa comprobada contra los archivos actuales, incluidos los resultados históricos.";
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "Could not refresh library state");
            QuickActionMessage = "No se pudo actualizar la biblioteca. Los datos se conservaron.";
        }
    }

    public void ConfigureAutomaticCopies(string? directory)
    {
        var oldEnabled = _options.AutomaticResultCopyEnabled;
        var oldDirectory = _options.AutomaticResultCopyDirectory;
        try
        {
            if (directory is not null)
            {
                directory = ResultCopyService.ValidateDirectory(directory, CurrentProject?.SourceDirectory ?? "");
                Directory.CreateDirectory(directory);
                _options.AutomaticResultCopyDirectory = directory;
            }
            _options.AutomaticResultCopyEnabled = directory is not null;
            SettingsService.SaveSettings(_options, string.IsNullOrWhiteSpace(_options.SettingsPath) ? null : _options.SettingsPath);
        }
        catch
        {
            _options.AutomaticResultCopyEnabled = oldEnabled;
            _options.AutomaticResultCopyDirectory = oldDirectory;
            throw;
        }
        OnPropertyChanged(nameof(AutomaticCopiesLabel));
        QuickActionMessage = AutomaticCopiesLabel;
    }

    public Task SaveVisibleCopiesAsync(string directory) => SaveCopiesAsync(directory, false);
    public Task SaveAllCopiesAsync(string directory) => SaveCopiesAsync(directory, true);

    private async Task RestoreAutomaticCopiesAsync()
    {
        if (!_options.AutomaticResultCopyEnabled) return;
        try { await SaveAllCopiesAsync(_options.AutomaticResultCopyDirectory); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogWarning(error, "Could not resume historical result copies");
            QuickActionMessage = "No se pudieron completar las copias históricas; los resultados de la biblioteca se conservan.";
        }
        catch (OperationCanceledException) { }
    }

    private async Task SaveCopiesAsync(string directory, bool all)
    {
        if (IsQuickActionRunning) return;
        IsQuickActionRunning = true;
        var assets = (all ? AllAssets : FilteredAssets).Where(item => item.RestoredImagePath is not null).Select(item => item.Asset).ToArray();
        var saved = 0;
        var existing = 0;
        var failed = 0;
        try
        {
            QuickActionMessage = $"Guardando copias de {assets.Length} resultado(s) de la biblioteca…";
            foreach (var asset in assets)
            {
                try
                {
                    if (await Task.Run(() => _resultCopies.SaveAsync(asset, directory, _lifetimeCts.Token))) saved++;
                    else existing++;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    _logger.LogWarning(error, "Could not save review copy for asset {AssetId}", asset.Id);
                    failed++;
                }
                QuickActionMessage = $"Copias comprobadas: {saved + existing + failed}/{assets.Length}; {saved} nuevas, {existing} existentes, {failed} fallidas.";
            }
            QuickActionMessage = $"Copias: {saved} guardadas, {existing} ya existían, {failed} fallidas. No cambia la aprobación humana.";
        }
        finally { IsQuickActionRunning = false; }
    }
}
