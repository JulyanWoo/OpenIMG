using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenIMG.Application.Abstractions.Persistence;
using OpenIMG.Application.Configuration;
using OpenIMG.Core.Entities;
using OpenIMG.Core.Enums;
using OpenIMG.Infrastructure.Configuration;

namespace OpenIMG.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly OpenImgOptions _options;
    private readonly IProjectRepository _projectRepository;
    private readonly Project? _activeProject;

    [ObservableProperty]
    private string _databasePath;

    [ObservableProperty]
    private string _gptWebView2ProfilePath;

    [ObservableProperty]
    private string _defaultOutputDirectory;

    [ObservableProperty]
    private string _storageErrorMessage = string.Empty;

    [ObservableProperty]
    private int _maxAutomaticAttempts;

    [ObservableProperty]
    private PhotographicEnhancementProfile _photographicEnhancementProfile;

    [ObservableProperty]
    private SceneCleanupMode _sceneCleanupMode;

    [ObservableProperty]
    private PersonRemovalMode _personRemovalMode;

    [ObservableProperty]
    private double _minStructuralSimilarity;

    [ObservableProperty]
    private double _minEdgeSimilarity;

    [ObservableProperty]
    private double _minSilhouetteSimilarity;

    [ObservableProperty]
    private double _maxAllowedGlobalColorDifference;

    [ObservableProperty]
    private double _maxAllowedNormalizedColorDistributionDifference;

    [ObservableProperty]
    private double _maxAllowedChangedAreaPercentage;

    [ObservableProperty]
    private double _maxAllowedLargestChangedRegionPercentage;

    [ObservableProperty]
    private double _maxAllowedProtectedAreaChangedPercentage;

    [ObservableProperty]
    private double _minImageRegistrationConfidence;

    [ObservableProperty]
    private int _pageLoadTimeoutSeconds;

    [ObservableProperty]
    private int _responseTimeoutSeconds;

    [ObservableProperty]
    private int _gptMaxOperationsPerWindow;

    [ObservableProperty]
    private int _gptWindowDurationMinutes;

    [ObservableProperty]
    private int _gptSafetyReserve;

    [ObservableProperty]
    private int? _gptOptionalMaxOperationsPerDay;

    public event Action? RequestClose;

    public SettingsViewModel(OpenImgOptions options, IProjectRepository projectRepository, Project? activeProject)
    {
        _options = options;
        _projectRepository = projectRepository;
        _activeProject = activeProject;

        _databasePath = options.DatabasePath;
        _gptWebView2ProfilePath = options.GptWebView2ProfilePath;
        _defaultOutputDirectory = options.DefaultOutputDirectory;
        _maxAutomaticAttempts = options.MaxAutomaticAttempts;
        _photographicEnhancementProfile = options.PhotographicEnhancementProfile;
        _sceneCleanupMode = options.SceneCleanupMode;
        _personRemovalMode = options.PersonRemovalMode;

        _minStructuralSimilarity = options.MinStructuralSimilarity;
        _minEdgeSimilarity = options.MinEdgeSimilarity;
        _minSilhouetteSimilarity = options.MinSilhouetteSimilarity;
        _maxAllowedGlobalColorDifference = options.MaxAllowedGlobalColorDifference;
        _maxAllowedNormalizedColorDistributionDifference = options.MaxAllowedNormalizedColorDistributionDifference;
        _maxAllowedChangedAreaPercentage = options.MaxAllowedChangedAreaPercentage;
        _maxAllowedLargestChangedRegionPercentage = options.MaxAllowedLargestChangedRegionPercentage;
        _maxAllowedProtectedAreaChangedPercentage = options.MaxAllowedProtectedAreaChangedPercentage;
        _minImageRegistrationConfidence = options.MinImageRegistrationConfidence;

        _pageLoadTimeoutSeconds = options.PageLoadTimeoutSeconds;
        _responseTimeoutSeconds = options.ResponseTimeoutSeconds;
        var gptBudget = options.DualModelRestoration.GptBudget;
        _gptMaxOperationsPerWindow = gptBudget.MaxOperationsPerWindow;
        _gptWindowDurationMinutes = gptBudget.WindowDurationMinutes;
        _gptSafetyReserve = gptBudget.SafetyReserve;
        _gptOptionalMaxOperationsPerDay = gptBudget.OptionalMaxOperationsPerDay;
    }

    [RelayCommand]
    public async Task Save()
    {
        StorageErrorMessage = string.Empty;

        string outputDirectory;
        try
        {
            if (string.IsNullOrWhiteSpace(DefaultOutputDirectory))
            {
                StorageErrorMessage = "Select a folder for restored images.";
                return;
            }

            outputDirectory = Path.GetFullPath(DefaultOutputDirectory.Trim());
            if (!Directory.Exists(outputDirectory))
            {
                StorageErrorMessage = "The selected output folder no longer exists. Choose an existing folder.";
                return;
            }

            if (_activeProject != null && IsSameOrDescendantPath(_activeProject.SourceDirectory, outputDirectory))
            {
                StorageErrorMessage = "Choose a folder outside the original-image folder so restored results are not re-imported as originals.";
                return;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            StorageErrorMessage = $"The output folder is invalid or unavailable: {ex.Message}";
            return;
        }

        var previousDefaultOutputDirectory = _options.DefaultOutputDirectory;
        var previousPhotoProfile = _options.PhotographicEnhancementProfile;
        var previousCleanupMode = _options.SceneCleanupMode;
        var previousPersonRemovalMode = _options.PersonRemovalMode;
        var previousProjectOutputDirectory = _activeProject?.OutputDirectory;
        var projectOutputChanged = _activeProject != null &&
            !PathEquals(previousProjectOutputDirectory, outputDirectory);

        if (projectOutputChanged)
        {
            _activeProject!.OutputDirectory = outputDirectory;
            try
            {
                await _projectRepository.UpdateAsync(_activeProject);
            }
            catch (Exception ex)
            {
                _activeProject.OutputDirectory = previousProjectOutputDirectory!;
                StorageErrorMessage = $"Could not save the library's output folder: {ex.Message}";
                return;
            }
        }

        _options.DatabasePath = DatabasePath;
        _options.GptWebView2ProfilePath = GptWebView2ProfilePath;
        _options.DefaultOutputDirectory = outputDirectory;
        _options.MaxAutomaticAttempts = MaxAutomaticAttempts;
        _options.PhotographicEnhancementProfile = PhotographicEnhancementProfile;
        _options.SceneCleanupMode = SceneCleanupMode;
        _options.PersonRemovalMode = PersonRemovalMode;

        _options.MinStructuralSimilarity = MinStructuralSimilarity;
        _options.MinEdgeSimilarity = MinEdgeSimilarity;
        _options.MinSilhouetteSimilarity = MinSilhouetteSimilarity;
        _options.MaxAllowedGlobalColorDifference = MaxAllowedGlobalColorDifference;
        _options.MaxAllowedNormalizedColorDistributionDifference = MaxAllowedNormalizedColorDistributionDifference;
        _options.MaxAllowedChangedAreaPercentage = MaxAllowedChangedAreaPercentage;
        _options.MaxAllowedLargestChangedRegionPercentage = MaxAllowedLargestChangedRegionPercentage;
        _options.MaxAllowedProtectedAreaChangedPercentage = MaxAllowedProtectedAreaChangedPercentage;
        _options.MinImageRegistrationConfidence = MinImageRegistrationConfidence;

        _options.PageLoadTimeoutSeconds = PageLoadTimeoutSeconds;
        _options.ResponseTimeoutSeconds = ResponseTimeoutSeconds;

        var gptBudget = _options.DualModelRestoration.GptBudget;
        gptBudget.Enabled = true;
        gptBudget.MaxOperationsPerWindow = GptMaxOperationsPerWindow;
        gptBudget.WindowDurationMinutes = GptWindowDurationMinutes;
        gptBudget.SafetyReserve = GptSafetyReserve;
        gptBudget.OptionalMaxOperationsPerDay = GptOptionalMaxOperationsPerDay;

        try
        {
            SettingsService.SaveSettings(
                _options,
                string.IsNullOrWhiteSpace(_options.SettingsPath) ? null : _options.SettingsPath);
        }
        catch (Exception ex)
        {
            _options.DefaultOutputDirectory = previousDefaultOutputDirectory;
            _options.PhotographicEnhancementProfile = previousPhotoProfile;
            _options.SceneCleanupMode = previousCleanupMode;
            _options.PersonRemovalMode = previousPersonRemovalMode;
            if (projectOutputChanged)
            {
                _activeProject!.OutputDirectory = previousProjectOutputDirectory!;
                try
                {
                    await _projectRepository.UpdateAsync(_activeProject);
                }
                catch (Exception rollbackException)
                {
                    StorageErrorMessage = $"Could not save settings ({ex.Message}) and the previous library folder could not be restored ({rollbackException.Message}). Reopen Settings and verify both paths.";
                    return;
                }
            }

            StorageErrorMessage = $"Could not save settings: {ex.Message}";
            return;
        }

        RequestClose?.Invoke();
    }

    private static bool IsSameOrDescendantPath(string sourceDirectory, string outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory))
            return false;

        var source = Path.GetFullPath(sourceDirectory);
        var relative = Path.GetRelativePath(source, outputDirectory);
        return relative == "." ||
            (!Path.IsPathRooted(relative) &&
             !relative.Equals("..", StringComparison.Ordinal) &&
             !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
             !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private static bool PathEquals(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left))
            return false;

        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    [RelayCommand]
    public void Cancel()
    {
        RequestClose?.Invoke();
    }
}
