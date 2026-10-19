using System.Collections.Concurrent;
using System.IO;
using Microsoft.Extensions.Logging;
using OpenIMG.Application.Abstractions.Persistence;
using OpenIMG.Application.Abstractions.Storage;
using OpenIMG.Core.Entities;
using OpenIMG.Core.Enums;

namespace OpenIMG.Application.Importing;

public sealed record SourceImageImportResult(
    IReadOnlyList<ImageAsset> ImportedAssets,
    int Duplicates,
    int Skipped)
{
    public int Imported => ImportedAssets.Count;
}

/// <summary>
/// Imports supported source-folder images without accepting partial copies or
/// creating duplicate assets when file-system notifications arrive repeatedly.
/// </summary>
public sealed class ProjectSourceImageImporter
{
    private readonly IImageAssetRepository _assetRepository;
    private readonly IFileSystemService _fileService;
    private readonly ILogger<ProjectSourceImageImporter> _logger;
    private readonly SemaphoreSlim _importGate = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _knownSourcePaths = new(StringComparer.OrdinalIgnoreCase);

    public ProjectSourceImageImporter(
        IImageAssetRepository assetRepository,
        IFileSystemService fileService,
        ILogger<ProjectSourceImageImporter> logger)
    {
        _assetRepository = assetRepository;
        _fileService = fileService;
        _logger = logger;
    }

    public void ResetKnownSourcePaths(IEnumerable<string> paths)
    {
        _knownSourcePaths.Clear();
        foreach (var path in paths)
        {
            try
            {
                _knownSourcePaths.TryAdd(Path.GetFullPath(path), 0);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            {
                _logger.LogDebug(ex, "Ignoring an invalid stored source path while reconciling imports.");
            }
        }
    }

    public bool IsKnownSourcePath(string path)
    {
        try
        {
            return _knownSourcePaths.ContainsKey(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    public async Task<SourceImageImportResult> ImportAsync(
        Project project,
        string sourceFolder,
        IEnumerable<string> filePaths,
        bool waitForStability,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFolder);

        var addedAssets = new List<ImageAsset>();
        var duplicates = 0;
        var skipped = 0;
        await _importGate.WaitAsync(cancellationToken);
        try
        {
            foreach (var candidatePath in filePaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsDirectSupportedImage(sourceFolder, candidatePath, out var fullPath))
                {
                    skipped++;
                    continue;
                }

                var pathKey = Path.GetFullPath(fullPath);
                if (_knownSourcePaths.ContainsKey(pathKey))
                    continue;

                try
                {
                    if (waitForStability && !await WaitForStableFileAsync(fullPath, cancellationToken))
                    {
                        skipped++;
                        _logger.LogDebug("Deferring image import until the file copy is stable: {ImagePath}", fullPath);
                        continue;
                    }

                    if (!File.Exists(fullPath))
                    {
                        skipped++;
                        continue;
                    }

                    var beforeHash = new FileInfo(fullPath);
                    var lengthBeforeHash = beforeHash.Length;
                    var modifiedBeforeHash = beforeHash.LastWriteTimeUtc;
                    if (lengthBeforeHash <= 0)
                    {
                        skipped++;
                        continue;
                    }

                    var dimensions = _fileService.GetImageDimensions(fullPath);
                    if (dimensions == null)
                    {
                        skipped++;
                        _logger.LogWarning("Skipping an unreadable image added to the source folder: {ImagePath}", fullPath);
                        continue;
                    }

                    var hash = await _fileService.ComputeSha256Async(fullPath, cancellationToken);
                    var afterHash = new FileInfo(fullPath);
                    afterHash.Refresh();
                    if (!afterHash.Exists || afterHash.Length != lengthBeforeHash || afterHash.LastWriteTimeUtc != modifiedBeforeHash)
                    {
                        skipped++;
                        _logger.LogDebug("Deferring an image whose contents changed during import: {ImagePath}", fullPath);
                        continue;
                    }

                    var existing = await _assetRepository.GetByHashAsync(project.Id, hash, cancellationToken);
                    if (existing != null)
                    {
                        duplicates++;
                        _knownSourcePaths.TryAdd(pathKey, 0);
                        continue;
                    }

                    var asset = new ImageAsset
                    {
                        ProjectId = project.Id,
                        OriginalFileName = Path.GetFileName(fullPath),
                        OriginalFilePath = fullPath,
                        Sha256Hash = hash,
                        FileSizeBytes = lengthBeforeHash,
                        Width = dimensions.Value.Width,
                        Height = dimensions.Value.Height,
                        Status = ImageProcessingStatus.Pending
                    };

                    asset.ThumbnailPath = await _fileService.GenerateThumbnailAsync(
                        fullPath,
                        Path.Combine(project.OutputDirectory, "Temp", "Thumbnails"),
                        cancellationToken: cancellationToken);
                    await _assetRepository.AddAsync(asset, cancellationToken);
                    _knownSourcePaths.TryAdd(pathKey, 0);
                    addedAssets.Add(asset);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    skipped++;
                    _logger.LogWarning(ex, "Image import was deferred for {ImagePath}; a later file-system event can retry it.", fullPath);
                }
            }
        }
        finally
        {
            _importGate.Release();
        }

        return new SourceImageImportResult(addedAssets, duplicates, skipped);
    }

    private static bool IsDirectSupportedImage(string sourceFolder, string candidatePath, out string fullPath)
    {
        fullPath = string.Empty;
        try
        {
            var folderPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceFolder));
            fullPath = Path.GetFullPath(candidatePath);
            if (!string.Equals(Path.GetDirectoryName(fullPath), folderPath, StringComparison.OrdinalIgnoreCase))
                return false;

            return Path.GetExtension(fullPath).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".webp";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static async Task<bool> WaitForStableFileAsync(string path, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        long previousLength = -1;
        DateTime previousWriteTime = DateTime.MinValue;
        var stableObservations = 0;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(path);
                info.Refresh();
                if (info.Exists && info.Length > 0 && info.Length == previousLength && info.LastWriteTimeUtc == previousWriteTime)
                {
                    stableObservations++;
                    if (stableObservations >= 2)
                        return true;
                }
                else
                {
                    stableObservations = 0;
                }

                previousLength = info.Exists ? info.Length : -1;
                previousWriteTime = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue;
            }
            catch (IOException)
            {
                stableObservations = 0;
            }
            catch (UnauthorizedAccessException)
            {
                stableObservations = 0;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
        }

        return false;
    }
}
