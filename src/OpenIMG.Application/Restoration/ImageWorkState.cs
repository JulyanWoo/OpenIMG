using OpenIMG.Application.Policies;
using OpenIMG.Core.Entities;
using OpenIMG.Core.Enums;

namespace OpenIMG.Application.Restoration;

public sealed record ImageWorkState(RestorationAttempt? LatestAttempt, bool HasCurrentResult,
    bool MissingResult, bool Recoverable, bool ManualRecoveryRequired, string Hint)
{
    public static RestorationAttempt? LatestAvailableAttempt(ImageAsset asset) => asset.ProcessingJobs
        .SelectMany(job => job.Attempts)
        .Where(attempt => IsAvailableFile(attempt.GeneratedImagePath))
        .OrderByDescending(attempt => attempt.CompletedAt ?? attempt.CreatedAt).FirstOrDefault();

    private static bool IsAvailableFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return File.Exists(path) && new FileInfo(path).Length > 0; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    public static bool IsProcessing(ImageProcessingStatus status) => status is
        ImageProcessingStatus.Analyzing or ImageProcessingStatus.AnalysisCompleted or
        ImageProcessingStatus.Restoring or ImageProcessingStatus.RestorationCompleted or
        ImageProcessingStatus.LocalValidation or ImageProcessingStatus.RemoteValidation;

    public static ImageWorkState Read(ImageAsset asset)
    {
        var job = asset.ProcessingJobs.OrderByDescending(item => item.CreatedAt).FirstOrDefault();
        var latest = job?.Attempts.OrderByDescending(item => item.CreatedAt).FirstOrDefault();
        var available = IsAvailableFile(latest?.GeneratedImagePath);
        var missing = !available && asset.Status is ImageProcessingStatus.NeedsReview or
            ImageProcessingStatus.Failed or ImageProcessingStatus.Accepted or ImageProcessingStatus.Cancelled;
        var unresolved = job?.Attempts.FirstOrDefault(item => ProviderOperationRecoveryPolicy.MayHaveSubmittedGeneration(item.ProviderOperationState));
        var recoverable = unresolved != null && GenerationRecoveryCheckpoint.Restore(unresolved, asset, asset.Sha256Hash) != null;
        var manual = unresolved != null && !recoverable;
        var hint = recoverable ? "Descarga recuperable sin enviar otra generación."
            : manual ? "Sin enlace recuperable. Revisa GPT o autoriza una nueva generación desde Acciones rápidas."
            : missing && LatestAvailableAttempt(asset) is not null ? "Último intento sin archivo; se muestra un resultado anterior."
            : missing ? "No hay archivo del último intento. Puede volver a encolarse."
            : available ? "Resultado guardado. La revisión humana sigue siendo independiente."
            : "Pendiente de generar y guardar un resultado.";
        return new(latest, available, missing, recoverable, manual, hint);
    }
}
