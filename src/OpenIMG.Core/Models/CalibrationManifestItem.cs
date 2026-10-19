namespace OpenIMG.Core.Models;

public class CalibrationManifestItem
{
    public string CalibrationId { get; set; } = string.Empty;
    public string SourceFileName { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string SourceSha256 { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string Category { get; set; } = string.Empty;
    public string? CategoryStrength { get; set; }
    public string Notes { get; set; } = string.Empty;
    public DateTimeOffset SelectedAt { get; set; } = DateTimeOffset.UtcNow;
    public string CalibrationDatasetVersion { get; set; } = "phase3-v2";
}
