namespace OpenIMG.Application.Abstractions.Storage;

public interface IFileSystemService
{
    Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken = default);
    bool VerifySourceIntegrity(string filePath, string expectedSha256);
    void NormalizeOrientation(string filePath);
    (int Width, int Height)? GetImageDimensions(string filePath);
    IReadOnlyList<string> GetSupportedImageFiles(string directory);
    void EnsureProjectOutputDirectories(string projectOutputDirectory);
    Task<string> GenerateThumbnailAsync(string imagePath, string destinationDirectory, int maxWidth = 240, int maxHeight = 240, CancellationToken cancellationToken = default);
    Task SaveSidecarMetadataAsync<T>(string metadataDirectory, string baseFileName, T sidecarData, CancellationToken cancellationToken = default);
}
