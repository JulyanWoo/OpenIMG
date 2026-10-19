using System.IO;
using System.Text.Json;
using OpenIMG.Application.Configuration;
using OpenIMG.Core.Enums;
using OpenIMG.Infrastructure.Configuration;
using Xunit;

namespace OpenIMG.Tests;

[Trait("Category", TestCategories.Unit)]
public sealed class SettingsMigrationTests
{
    [Fact]
    public void LegacySettings_MigrateToOneTypedCleanupPolicyAndPersistVersionTwo()
    {
        var workspace = TestArtifactWorkspace.Create(nameof(LegacySettings_MigrateToOneTypedCleanupPolicyAndPersistVersionTwo));
        try
        {
            var settingsPath = Path.Combine(workspace, "settings.json");
            File.WriteAllText(settingsPath, """
                {
                  "restorationProfile": "Conservative",
                  "allowSceneCleanup": false,
                  "allowPersonRemoval": false
                }
                """);

            var options = SettingsService.LoadOrCreateSettings(settingsPath);

            Assert.Equal(OpenImgOptions.CurrentSettingsSchemaVersion, options.SettingsSchemaVersion);
            Assert.Equal(PhotographicEnhancementProfile.Strong, options.PhotographicEnhancementProfile);
            Assert.Equal(SceneCleanupMode.SafeTemporaryOnly, options.SceneCleanupMode);
            Assert.Equal(PersonRemovalMode.Disabled, options.PersonRemovalMode);

            using var persisted = JsonDocument.Parse(File.ReadAllText(settingsPath));
            Assert.Equal(OpenImgOptions.CurrentSettingsSchemaVersion,
                persisted.RootElement.GetProperty("settingsSchemaVersion").GetInt32());
            Assert.Equal((int)SceneCleanupMode.SafeTemporaryOnly,
                persisted.RootElement.GetProperty("sceneCleanupMode").GetInt32());
            Assert.Equal((int)PersonRemovalMode.Disabled,
                persisted.RootElement.GetProperty("personRemovalMode").GetInt32());
            Assert.True(persisted.RootElement.GetProperty("allowSceneCleanup").GetBoolean());
            Assert.False(persisted.RootElement.GetProperty("allowPersonRemoval").GetBoolean());
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void LegacyPersonRemovalOptIn_MigratesToExplicitSafeMode()
    {
        var workspace = TestArtifactWorkspace.Create(nameof(LegacyPersonRemovalOptIn_MigratesToExplicitSafeMode));
        try
        {
            var settingsPath = Path.Combine(workspace, "settings.json");
            File.WriteAllText(settingsPath, """
                {
                  "settingsSchemaVersion": 1,
                  "allowPersonRemoval": true
                }
                """);

            var options = SettingsService.LoadOrCreateSettings(settingsPath);

            Assert.Equal(PersonRemovalMode.SafeWhenClearlyTemporary, options.PersonRemovalMode);
            Assert.Equal(SceneCleanupMode.SafeTemporaryOnly, options.SceneCleanupMode);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void CurrentTypedSettings_OverrideConflictingLegacyBoolean()
    {
        var workspace = TestArtifactWorkspace.Create(nameof(CurrentTypedSettings_OverrideConflictingLegacyBoolean));
        try
        {
            var settingsPath = Path.Combine(workspace, "settings.json");
            File.WriteAllText(settingsPath, """
                {
                  "settingsSchemaVersion": 2,
                  "sceneCleanupMode": 0,
                  "personRemovalMode": 0,
                  "allowSceneCleanup": true
                }
                """);

            var options = SettingsService.LoadOrCreateSettings(settingsPath);

            Assert.Equal(SceneCleanupMode.Disabled, options.SceneCleanupMode);
            Assert.Equal(PersonRemovalMode.Disabled, options.PersonRemovalMode);
            Assert.False(options.AllowSceneCleanup);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }
}
