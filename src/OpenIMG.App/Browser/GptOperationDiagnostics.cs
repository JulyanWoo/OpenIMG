using System.Text.Json;
using OpenIMG.Application.Restoration;

namespace OpenIMG.App.Browser;

/// <summary>Allowlisted operation evidence for a user-copyable report; never stores page text or URLs.</summary>
internal sealed class GptOperationDiagnostics
{
    private readonly object _gate = new();
    private string _stage = "Idle";
    private DateTimeOffset _changedAt = DateTimeOffset.UtcNow;
    private object? _response;
    private string? _nativeDownloadState;

    public void Reset(string stage)
    {
        lock (_gate) { _response = null; _nativeDownloadState = null; _stage = stage; _changedAt = DateTimeOffset.UtcNow; }
    }

    public void Stage(string stage)
    {
        lock (_gate) { if (_stage == stage) return; _stage = stage; _changedAt = DateTimeOffset.UtcNow; }
    }

    public void ObserveResponse(JsonElement state)
    {
        int Count(string name) => state.TryGetProperty(name, out var value) && value.TryGetInt32(out var count) ? count : 0;
        bool Flag(string name) => state.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
        lock (_gate) _response = new
        {
            responseCount = Count("responseCount"), ownedImageCount = Count("currentResponseImageCount"),
            ownedImage = Flag("currentResponseOwnedImage"), generationActive = Flag("generationActive"),
            rateLimited = Flag("rateLimited"), providerWarning = Flag("providerWarning")
        };
    }

    public void NativeDownload(string state) { lock (_gate) _nativeDownloadState = state; }

    public static string? ResultStageDisplayText(string stage) => stage switch
    {
        "GeneratedResultDetected" => "GPT: imagen generada detectada; preparando el visor…",
        "OpeningGeneratedImageViewer" => "GPT: abriendo y comprobando el visor de la imagen…",
        "VerifyingResult" => "GPT: imagen disponible; verificando la operación antes de descargar…",
        "RecoveryRequired" => "GPT: el resultado requiere recuperación/verificación. No se descargará ni se enviará otra generación automáticamente.",
        "GenerationSubmitted" => "GPT: solicitud enviada una vez; guardando el vínculo de conversación…",
        "ConversationBound" => "GPT: conversación vinculada y guardada; esperando el resultado…",
        "Generating" => "GPT: generando la imagen de esta operación…",
        "CandidateDetected" => "GPT: resultado detectado; comprobando su identidad…",
        "CandidateOwned" => "GPT: resultado verificado; preparando la descarga…",
        "ViewerOpened" => "GPT: visor abierto; preparando el archivo…",
        "DownloadStarted" => "GPT: descarga iniciada; esperando confirmación del navegador…",
        "CandidateDownloaded" => "GPT: archivo descargado y guardado; comprobando integridad…",
        "FileVerified" => "GPT: archivo verificado; continúa la validación local.",
        "NeedsRecovery" => "GPT: operación pausada; recuperar su descarga no enviará otra generación.",
        _ => null
    };

    public object Snapshot() { lock (_gate) return new { stage = _stage, stageChangedAt = _changedAt, lastResponse = _response, nativeDownloadState = _nativeDownloadState }; }

    public static string ConversationRouteValidationReason(string? path)
    {
        if (GenerationRecoveryCheckpoint.IsConversationPath(path)) return "Valid";
        if (string.IsNullOrEmpty(path)) return "Missing";
        if (!path.StartsWith("/c/", StringComparison.Ordinal)) return "UnexpectedPrefix";
        if (path.Length <= 3) return "MissingIdentifier";
        if (path.EndsWith('/')) return "TrailingSlash";
        if (path.Contains('?') || path.Contains('#')) return "QueryOrFragment";
        if (path.Contains('\\')) return "Backslash";
        if (path.Any(char.IsWhiteSpace)) return "Whitespace";
        if (path[3..].Contains('/')) return "AdditionalPathSegments";
        return "UnsupportedIdentifierCharacters";
    }

    public static object DescribeConversationRoute(string? path) => new
    {
        present = !string.IsNullOrEmpty(path),
        validForRecovery = GenerationRecoveryCheckpoint.IsConversationPath(path),
        validationReason = ConversationRouteValidationReason(path),
        length = path?.Length ?? 0,
        segmentCount = path?.Split('/', StringSplitOptions.RemoveEmptyEntries).Length ?? 0,
        trailingSlash = path?.EndsWith('/') == true,
        hasUnderscore = path?.Contains('_') == true,
        hasPercentEncoding = path?.Contains('%') == true,
        percentEncodedIdentifierAccepted = path?.Contains('%') == true && GenerationRecoveryCheckpoint.NormalizeConversationPath(path) is not null,
        canonicalLength = GenerationRecoveryCheckpoint.NormalizeConversationPath(path)?.Length ?? 0,
        hasQueryOrFragment = path?.Contains('?') == true || path?.Contains('#') == true,
        hasWhitespace = path?.Any(char.IsWhiteSpace) == true,
        hasNonAscii = path?.Any(character => character > 127) == true
    };

    public static object DescribeSavedCheckpoint(string? checkpoint)
    {
        if (string.IsNullOrWhiteSpace(checkpoint)) return new { state = "Missing" };
        try
        {
            using var document = JsonDocument.Parse(checkpoint);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Format", out var format) ||
                format.ValueKind != JsonValueKind.String || format.GetString() != "OpenIMG.GptRecovery.v1")
                return new { state = "Unrecognized" };
            var path = root.TryGetProperty("ConversationPath", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
            return new { state = "Recognized", conversationRoute = DescribeConversationRoute(path) };
        }
        catch (JsonException) { return new { state = "InvalidJson" }; }
    }

    public const string DomSnapshotScript = """
    JSON.stringify((function(){
      function visible(el){var r=el.getBoundingClientRect(),s=getComputedStyle(el);return r.width>0&&r.height>0&&s.display!=='none'&&s.visibility!=='hidden';}
      var root=document.querySelector('main')||document.body;
      var images=root?Array.from(root.querySelectorAll('img')):[];
      var controls=Array.from(document.querySelectorAll('button,a,[role="button"]')).filter(function(el){return /download|descargar|guardar imagen|save image/i.test([el.getAttribute('aria-label'),el.title,el.innerText,el.getAttribute('data-testid')].filter(Boolean).join(' '));});
      return {documentReadyState:document.readyState,isConversationRoute:location.pathname.startsWith('/c/'),
        userMessageCount:document.querySelectorAll('[data-message-author-role="user"]').length,
        assistantMessageCount:document.querySelectorAll('[data-message-author-role="assistant"]').length,
        generatedGalleryCount:document.querySelectorAll('[data-testid="generated-image-gallery"]').length,
        downloadControlCount:controls.length,visibleDownloadControlCount:controls.filter(visible).length,
        dialogCount:document.querySelectorAll('[role="dialog"]').length,
        images:images.slice(-20).map(function(image){var r=image.getBoundingClientRect(),source=image.currentSrc||image.src||'';return {
          naturalWidth:image.naturalWidth,naturalHeight:image.naturalHeight,renderedWidth:r.width,renderedHeight:r.height,
          complete:image.complete,visible:visible(image),inUserMessage:!!image.closest('[data-message-author-role="user"]'),
          inAssistantMessage:!!image.closest('[data-message-author-role="assistant"]'),inGeneratedGallery:!!image.closest('[data-testid="generated-image-gallery"]'),
          sourceScheme:source.startsWith('blob:')?'blob':source.startsWith('data:')?'data':source.startsWith('https:')?'https':'other'};})};
    })())
    """;
}
