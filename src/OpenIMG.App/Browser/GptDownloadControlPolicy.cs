using System.Text.Json;

namespace OpenIMG.App.Browser;

internal sealed record GptDownloadControlCandidate(
    string? AriaLabel,
    string? Title,
    string? Text,
    string? TestId,
    bool IsVisible,
    bool IsInDialog,
    double X,
    double Y,
    string? ElementToken = null);

internal static class GptDownloadControlPolicy
{
    public static GptDownloadControlCandidate? SelectSnapshot(string snapshot)
    {
        using var document = JsonDocument.Parse(snapshot);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return null;
        return Select(document.RootElement.EnumerateArray().Select(element => new GptDownloadControlCandidate(
            ReadString(element, "ariaLabel"), ReadString(element, "title"), ReadString(element, "text"), ReadString(element, "testId"),
            element.TryGetProperty("visible", out var visible) && visible.ValueKind == JsonValueKind.True,
            element.TryGetProperty("inDialog", out var dialog) && dialog.ValueKind == JsonValueKind.True,
            element.GetProperty("x").GetDouble(), element.GetProperty("y").GetDouble(), ReadString(element, "elementToken"))));
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static GptDownloadControlCandidate? Select(IEnumerable<GptDownloadControlCandidate> candidates)
    {
        var visibleMatches = candidates
            .Where(candidate => candidate.IsVisible && IsDownloadControl(candidate))
            .ToArray();

        var dialogMatches = visibleMatches.Where(candidate => candidate.IsInDialog).ToArray();
        if (dialogMatches.Length > 0)
            return dialogMatches.Length == 1 ? dialogMatches[0] : null;

        return visibleMatches.Length == 1 ? visibleMatches[0] : null;
    }

    private static bool IsDownloadControl(GptDownloadControlCandidate candidate)
    {
        var label = string.Join(' ', candidate.AriaLabel, candidate.Title, candidate.Text, candidate.TestId);
        return label.Contains("download", StringComparison.OrdinalIgnoreCase) ||
               label.Contains("descargar", StringComparison.OrdinalIgnoreCase) ||
               label.Contains("guardar imagen", StringComparison.OrdinalIgnoreCase) ||
               label.Contains("save image", StringComparison.OrdinalIgnoreCase);
    }
}
