using System.Text.Json;
using OpenIMG.Application.Abstractions.Storage;
using OpenIMG.Application.Restoration;
using OpenIMG.Core.Entities;

namespace OpenIMG.Infrastructure.Storage;

/// <summary>Saves idempotent review copies; does not approve or promote a candidate.</summary>
public sealed class ResultCopyService(IFileSystemService files)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public static string ValidateDirectory(string directory, string sourceDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("Selecciona una carpeta absoluta.");
        var full = Path.GetFullPath(directory);
        if (!string.IsNullOrWhiteSpace(sourceDirectory))
        {
            var relative = Path.GetRelativePath(Path.GetFullPath(sourceDirectory), full);
            if (relative == "." || (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar)))
                throw new InvalidOperationException("La carpeta de copias debe estar fuera de la carpeta de originales para evitar importaciones repetidas.");
        }
        return full;
    }

    public async Task<bool> SaveAsync(ImageAsset asset, string directory, CancellationToken cancellationToken = default)
    {
        var state = ImageWorkState.Read(asset);
        var attempt = ImageWorkState.LatestAvailableAttempt(asset);
        if (attempt is null) return false;
        var source = Path.GetFullPath(attempt.GeneratedImagePath!);
        if (Path.GetExtension(source).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg" or ".webp"))
            throw new InvalidDataException("El resultado no tiene un formato de imagen compatible.");
        var root = ValidateDirectory(directory, Path.GetDirectoryName(Path.GetFullPath(asset.OriginalFilePath))!);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (files.GetImageDimensions(source) is null) throw new InvalidDataException("El resultado guardado no es una imagen legible.");
            var hash = await files.ComputeSha256Async(source, cancellationToken);
            if (attempt.GeneratedSha256 is { Length: > 0 } expected && !string.Equals(hash, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("El resultado no coincide con su SHA-256 guardado.");
            var stem = Path.GetFileNameWithoutExtension(asset.OriginalFileName);
            stem = new string(stem.Take(70).Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character).ToArray());
            var target = Path.Combine(root, $"{stem}__{asset.Id:N}_{attempt.Id:N}{Path.GetExtension(source)}");
            var metadata = target + ".openimg.json";
            Directory.CreateDirectory(root);
            if (File.Exists(target))
            {
                if (!File.Exists(metadata) || !string.Equals(await files.ComputeSha256Async(target, cancellationToken), hash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Ya existe una copia diferente o incompleta; se conservó sin sobrescribirla.");
                await using var existingMetadata = File.OpenRead(metadata);
                using var saved = await JsonDocument.ParseAsync(existingMetadata, cancellationToken: cancellationToken);
                if (!saved.RootElement.TryGetProperty("attemptId", out var id) || !id.TryGetGuid(out var savedId) || savedId != attempt.Id)
                    throw new IOException("La copia existente pertenece a otro intento; se conservó sin sobrescribirla.");
                return false;
            }
            if (File.Exists(metadata)) throw new IOException("Ya existe metadata con este nombre; no se sobrescribió.");
            var createdImage = false;
            var createdMetadata = false;
            try
            {
                await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
                await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                {
                    createdImage = true;
                    await input.CopyToAsync(output, cancellationToken);
                }
                if (!string.Equals(await files.ComputeSha256Async(target, cancellationToken), hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("No se pudo verificar la copia.");
                await using var sidecar = new FileStream(metadata, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true);
                createdMetadata = true;
                await JsonSerializer.SerializeAsync(sidecar, new
                {
                    purpose = "ReviewCopy", assetId = asset.Id, attemptId = attempt.Id,
                    sourceFileName = Path.GetFileName(asset.OriginalFileName), sourceSha256 = asset.Sha256Hash,
                    generatedSha256 = hash, operationId = attempt.ProviderOperationId,
                    automatedDecision = attempt.FinalDecision?.ToString(), humanReview = attempt.HumanReviewJson,
                    isLatestAttempt = state.LatestAttempt?.Id == attempt.Id,
                    copiedAt = DateTimeOffset.UtcNow
                }, cancellationToken: cancellationToken);
                return true;
            }
            catch
            {
                if (createdImage) File.Delete(target);
                if (createdMetadata) File.Delete(metadata);
                throw;
            }
        }
        finally { _gate.Release(); }
    }
}
