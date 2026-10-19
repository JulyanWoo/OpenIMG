using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using OpenCvSharp;
using OpenIMG.Application.Abstractions.Storage;

namespace OpenIMG.Infrastructure.Storage;

public class FileSystemService : IFileSystemService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("File not found for hash calculation.", filePath);

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        var hashBytes = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    public bool VerifySourceIntegrity(string filePath, string expectedSha256)
    {
        if (!File.Exists(filePath)) return false;

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hashBytes = SHA256.HashData(stream);
        var currentHash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        return string.Equals(currentHash, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    public void NormalizeOrientation(string filePath)
    {
        // Safe check without modifying source file:
        // Original files are immutable! We do NOT overwrite or re-save the original image.
        // If an orientation tag exists, we decode it rotated in memory when processing.
    }

    public (int Width, int Height)? GetImageDimensions(string filePath)
    {
        try
        {
            using var mat = Cv2.ImRead(filePath, ImreadModes.Grayscale);
            if (mat.Empty()) return null;
            return (mat.Width, mat.Height);
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyList<string> GetSupportedImageFiles(string directory)
    {
        if (!Directory.Exists(directory))
            return Array.Empty<string>();

        return Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)))
            .OrderBy(f => f)
            .ToList();
    }

    public void EnsureProjectOutputDirectories(string projectOutputDirectory)
    {
        Directory.CreateDirectory(projectOutputDirectory);
        Directory.CreateDirectory(Path.Combine(projectOutputDirectory, "Accepted"));
        Directory.CreateDirectory(Path.Combine(projectOutputDirectory, "Review"));
        Directory.CreateDirectory(Path.Combine(projectOutputDirectory, "Failed"));
        Directory.CreateDirectory(Path.Combine(projectOutputDirectory, "Metadata"));
        Directory.CreateDirectory(Path.Combine(projectOutputDirectory, "Diagnostics"));
        Directory.CreateDirectory(Path.Combine(projectOutputDirectory, "Temp"));
    }

    public async Task<string> GenerateThumbnailAsync(
        string imagePath,
        string destinationDirectory,
        int maxWidth = 240,
        int maxHeight = 240,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destinationDirectory);
        var thumbFileName = $"{Path.GetFileNameWithoutExtension(imagePath)}_thumb.jpg";
        var thumbPath = Path.Combine(destinationDirectory, thumbFileName);

        if (File.Exists(thumbPath))
            return thumbPath;

        await Task.Run(() =>
        {
            using var src = Cv2.ImRead(imagePath, ImreadModes.Color);
            if (src.Empty()) return;

            var scale = Math.Min((double)maxWidth / src.Width, (double)maxHeight / src.Height);
            if (scale >= 1.0) scale = 1.0;

            var newWidth = Math.Max(1, (int)(src.Width * scale));
            var newHeight = Math.Max(1, (int)(src.Height * scale));

            using var dst = new Mat();
            Cv2.Resize(src, dst, new Size(newWidth, newHeight), 0, 0, InterpolationFlags.Area);

            var encodeParams = new ImageEncodingParam(ImwriteFlags.JpegQuality, 85);
            Cv2.ImWrite(thumbPath, dst, encodeParams);
        }, cancellationToken);

        return thumbPath;
    }

    public async Task SaveSidecarMetadataAsync<T>(
        string metadataDirectory,
        string baseFileName,
        T sidecarData,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(metadataDirectory);
        var sidecarPath = Path.Combine(metadataDirectory, $"{baseFileName}.json");

        var json = JsonSerializer.Serialize(sidecarData, JsonOptions);
        await File.WriteAllTextAsync(sidecarPath, json, cancellationToken);
    }
}
