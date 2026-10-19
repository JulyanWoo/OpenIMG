using System.IO;
using System.Text.Json;
using OpenIMG.Application.Configuration;

namespace OpenIMG.Infrastructure.Configuration;

public class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string DefaultBaseDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenIMG");

    public static string DefaultSettingsPath =>
        Path.Combine(DefaultBaseDirectory, "settings.json");

    public static OpenImgOptions LoadOrCreateSettings(string? customPath = null)
    {
        var path = customPath ?? DefaultSettingsPath;
        var dir = Path.GetDirectoryName(path) ?? DefaultBaseDirectory;
        Directory.CreateDirectory(dir);

        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                var options = JsonSerializer.Deserialize<OpenImgOptions>(json, JsonOptions);
                if (options != null)
                {
                    var schemaVersion = TryGetInt32(root, "settingsSchemaVersion") ?? 0;
                    var needsMigration = schemaVersion < OpenImgOptions.CurrentSettingsSchemaVersion;
                    var legacyFlagsOutOfSync = schemaVersion <= OpenImgOptions.CurrentSettingsSchemaVersion &&
                        (options.AllowSceneCleanup != (options.SceneCleanupMode != OpenIMG.Core.Enums.SceneCleanupMode.Disabled) ||
                         options.AllowPersonRemoval != (options.PersonRemovalMode != OpenIMG.Core.Enums.PersonRemovalMode.Disabled));

                    if (needsMigration)
                    {
                        // Old JSON had overlapping booleans and profile names. New
                        // typed fields become the runtime authority. Default missing
                        // cleanup to the product's safe temporary-cleanup behavior;
                        // preserve the historical person-removal opt-in explicitly.
                        if (!HasProperty(root, "photographicEnhancementProfile"))
                            options.PhotographicEnhancementProfile = OpenIMG.Core.Enums.PhotographicEnhancementProfile.Strong;

                        if (!HasProperty(root, "sceneCleanupMode"))
                            options.SceneCleanupMode = OpenIMG.Core.Enums.SceneCleanupMode.SafeTemporaryOnly;

                        if (!HasProperty(root, "personRemovalMode"))
                        {
                            options.PersonRemovalMode = options.AllowPersonRemoval
                                ? OpenIMG.Core.Enums.PersonRemovalMode.SafeWhenClearlyTemporary
                                : OpenIMG.Core.Enums.PersonRemovalMode.Disabled;
                        }

                    }

                    if (schemaVersion <= OpenImgOptions.CurrentSettingsSchemaVersion)
                    {
                        options.SettingsSchemaVersion = OpenImgOptions.CurrentSettingsSchemaVersion;
                        SyncLegacyCleanupFlags(options);
                    }

                    EnsureDefaultPaths(options, dir);
                    if (needsMigration || legacyFlagsOutOfSync)
                        SaveSettings(options, path);

                    return options;
                }
            }
            catch
            {
                // In case of parsing error, return new default instance
            }
        }

        var newOptions = new OpenImgOptions();
        EnsureDefaultPaths(newOptions, dir);
        SaveSettings(newOptions, path);
        return newOptions;
    }

    public static void SaveSettings(OpenImgOptions options, string? customPath = null)
    {
        options.SettingsSchemaVersion = OpenImgOptions.CurrentSettingsSchemaVersion;
        SyncLegacyCleanupFlags(options);

        var path = customPath ?? DefaultSettingsPath;
        var dir = Path.GetDirectoryName(path) ?? DefaultBaseDirectory;
        Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(options, JsonOptions);
        File.WriteAllText(path, json);
    }

    private static bool HasProperty(JsonElement root, string propertyName) =>
        root.ValueKind == JsonValueKind.Object &&
        root.EnumerateObject().Any(property =>
            property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase));

    private static int? TryGetInt32(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var property in root.EnumerateObject())
        {
            if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase) &&
                property.Value.TryGetInt32(out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static void SyncLegacyCleanupFlags(OpenImgOptions options)
    {
        options.AllowSceneCleanup = options.SceneCleanupMode != OpenIMG.Core.Enums.SceneCleanupMode.Disabled;
        options.AllowPersonRemoval = options.PersonRemovalMode != OpenIMG.Core.Enums.PersonRemovalMode.Disabled;
    }

    private static void EnsureDefaultPaths(OpenImgOptions options, string baseDir)
    {
        options.DualModelRestoration ??= new DualModelRestorationOptions();
        options.DualModelRestoration.GptBudget ??= new ProviderUsageBudgetOptions
        {
            Enabled = true,
            MaxOperationsPerWindow = 40,
            WindowDurationMinutes = 180,
            SafetyReserve = 5,
            OptionalMaxOperationsPerDay = 120
        };
        // GPT quota enforcement is a product safety invariant, not an optional
        // toggle. The configured values remain operator-editable.
        options.DualModelRestoration.GptBudget.Enabled = true;

        if (string.IsNullOrWhiteSpace(options.DatabasePath))
            options.DatabasePath = Path.Combine(baseDir, "openimg.db");

        if (string.IsNullOrWhiteSpace(options.WebView2ProfilePath))
            options.WebView2ProfilePath = Path.Combine(baseDir, "WebView2Profile");

        if (string.IsNullOrWhiteSpace(options.GptWebView2ProfilePath))
            options.GptWebView2ProfilePath = Path.Combine(baseDir, "WebView2Profile-GPT");

        if (string.IsNullOrWhiteSpace(options.LogsDirectory))
            options.LogsDirectory = Path.Combine(baseDir, "Logs");

        if (string.IsNullOrWhiteSpace(options.OperationsDirectory))
            options.OperationsDirectory = Path.Combine(baseDir, "operations");

        if (string.IsNullOrWhiteSpace(options.DefaultOutputDirectory))
            options.DefaultOutputDirectory = Path.Combine(options.OperationsDirectory, "DefaultProject");

        options.SettingsPath = Path.Combine(baseDir, "settings.json");
    }
}
