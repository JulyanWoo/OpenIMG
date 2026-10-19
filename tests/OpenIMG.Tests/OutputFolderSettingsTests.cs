using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenIMG.App.ViewModels;
using OpenIMG.Application.Abstractions.Persistence;
using OpenIMG.Application.Configuration;
using OpenIMG.Core.Entities;
using OpenIMG.Core.Enums;
using OpenIMG.Infrastructure.Configuration;

namespace OpenIMG.Tests;

[Trait("Category", TestCategories.Unit)]
public sealed class OutputFolderSettingsTests
{
    [Fact]
    public async Task SavePersistsSelectedFolderForActiveAndFutureLibrariesWithoutMovingExistingResults()
    {
        var workspace = TestArtifactWorkspace.Create(nameof(SavePersistsSelectedFolderForActiveAndFutureLibrariesWithoutMovingExistingResults));
        try
        {
            var sourceDirectory = Directory.CreateDirectory(Path.Combine(workspace, "originals")).FullName;
            var previousOutputDirectory = Directory.CreateDirectory(Path.Combine(workspace, "previous-output")).FullName;
            var selectedOutputDirectory = Directory.CreateDirectory(Path.Combine(workspace, "restored-images")).FullName;
            var existingResultPath = Path.Combine(previousOutputDirectory, "existing-result.png");
            await File.WriteAllTextAsync(existingResultPath, "historical result");

            var project = new Project
            {
                Name = "My library",
                SourceDirectory = sourceDirectory,
                OutputDirectory = previousOutputDirectory
            };
            var repository = new InMemoryProjectRepository(project);
            var settingsPath = Path.Combine(workspace, "settings.json");
            var options = new OpenImgOptions
            {
                SettingsPath = settingsPath,
                DefaultOutputDirectory = previousOutputDirectory
            };
            var viewModel = new SettingsViewModel(options, repository, project)
            {
                DefaultOutputDirectory = selectedOutputDirectory
            };

            var closed = false;
            viewModel.RequestClose += () => closed = true;

            await viewModel.Save();

            Assert.Equal(selectedOutputDirectory, project.OutputDirectory);
            Assert.Equal(selectedOutputDirectory, options.DefaultOutputDirectory);
            Assert.Equal(1, repository.UpdateCount);
            Assert.True(closed);
            Assert.Empty(viewModel.StorageErrorMessage);
            Assert.True(File.Exists(existingResultPath));
            Assert.Equal("historical result", await File.ReadAllTextAsync(existingResultPath));

            using var savedSettings = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath));
            Assert.Equal(
                selectedOutputDirectory,
                savedSettings.RootElement.GetProperty("defaultOutputDirectory").GetString());
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task SaveRejectsOutputInsideOriginalFolderToPreventReimportingGeneratedImages()
    {
        var workspace = TestArtifactWorkspace.Create(nameof(SaveRejectsOutputInsideOriginalFolderToPreventReimportingGeneratedImages));
        try
        {
            var sourceDirectory = Directory.CreateDirectory(Path.Combine(workspace, "originals")).FullName;
            var unsafeOutputDirectory = Directory.CreateDirectory(Path.Combine(sourceDirectory, "restored")).FullName;
            var existingOutputDirectory = Directory.CreateDirectory(Path.Combine(workspace, "previous-output")).FullName;
            var project = new Project
            {
                Name = "My library",
                SourceDirectory = sourceDirectory,
                OutputDirectory = existingOutputDirectory
            };
            var repository = new InMemoryProjectRepository(project);
            var options = new OpenImgOptions
            {
                SettingsPath = Path.Combine(workspace, "settings.json"),
                DefaultOutputDirectory = existingOutputDirectory
            };
            var viewModel = new SettingsViewModel(options, repository, project)
            {
                DefaultOutputDirectory = unsafeOutputDirectory
            };

            var closed = false;
            viewModel.RequestClose += () => closed = true;

            await viewModel.Save();

            Assert.Contains("outside the original-image folder", viewModel.StorageErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(existingOutputDirectory, project.OutputDirectory);
            Assert.Equal(existingOutputDirectory, options.DefaultOutputDirectory);
            Assert.Equal(0, repository.UpdateCount);
            Assert.False(closed);
            Assert.False(File.Exists(options.SettingsPath));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task SavePersistsIndependentPhotoCleanupAndPersonPolicies()
    {
        var workspace = TestArtifactWorkspace.Create(nameof(SavePersistsIndependentPhotoCleanupAndPersonPolicies));
        try
        {
            var outputDirectory = Directory.CreateDirectory(Path.Combine(workspace, "restored")).FullName;
            var settingsPath = Path.Combine(workspace, "settings.json");
            var options = new OpenImgOptions
            {
                SettingsPath = settingsPath,
                DefaultOutputDirectory = outputDirectory
            };
            var viewModel = new SettingsViewModel(options, new InMemoryProjectRepository(null), null)
            {
                PhotographicEnhancementProfile = PhotographicEnhancementProfile.Moderate,
                SceneCleanupMode = SceneCleanupMode.Disabled,
                PersonRemovalMode = PersonRemovalMode.SafeWhenClearlyTemporary
            };

            await viewModel.Save();

            var reloaded = SettingsService.LoadOrCreateSettings(settingsPath);
            Assert.Equal(PhotographicEnhancementProfile.Moderate, reloaded.PhotographicEnhancementProfile);
            Assert.Equal(SceneCleanupMode.Disabled, reloaded.SceneCleanupMode);
            Assert.Equal(PersonRemovalMode.SafeWhenClearlyTemporary, reloaded.PersonRemovalMode);
            Assert.False(reloaded.AllowSceneCleanup);
            Assert.True(reloaded.AllowPersonRemoval);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    private sealed class InMemoryProjectRepository(Project? project) : IProjectRepository
    {
        public int UpdateCount { get; private set; }

        public Task<Project?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Project?>(project?.Id == id ? project : null);

        public Task<IReadOnlyList<Project>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Project>>(project == null ? [] : [project]);

        public Task<Project> CreateAsync(Project value, CancellationToken cancellationToken = default) =>
            Task.FromResult(value);

        public Task UpdateAsync(Project value, CancellationToken cancellationToken = default)
        {
            UpdateCount++;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
