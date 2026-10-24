using CommunityToolkit.Mvvm.Input;

namespace OpenIMG.App.ViewModels;

public partial class MainViewModel
{
    partial void OnCurrentProjectChanged(OpenIMG.Core.Entities.Project? value) => NotifyAutomationControls();
    public string QueuePhaseLabel => IsQueuePaused ? "COLA EN PAUSA" : IsQueueRunning ? "OPERACIÓN EN CURSO" : "COLA DETENIDA";
    private QueueActionPresentation QueueActionState => QueueActionPresentation.Create(
        CurrentProject is not null, IsQueueRunning, IsQueuePaused, IsGptReady, CanUseQuickActions,
        _queue.CurrentProcessingAsset is not null, PendingCount);
    public string QueuePrimaryActionLabel => QueueActionState.PrimaryLabel;
    public string QueuePrimaryActionHint => QueueActionState.PrimaryHint;
    public string PauseResumeLabel => QueueActionState.PauseLabel;
    public bool CanStartQueue => QueueActionState.CanRunPrimary;
    public bool CanPauseResumeQueue => QueueActionState.CanPause;
    public bool CanCancelCurrent => QueueActionState.CanCancel;
    public bool CanRetrySelected => SelectedAsset is not null && !IsQueueActivelyProcessing && CanUseQuickActions &&
        !SelectedAsset.WorkState.ManualRecoveryRequired;
    public string SelectedActionLabel => SelectedAsset?.WorkState.Recoverable == true ? "Recuperar descarga" : "Reintentar foto";
    public string SelectedActionHint => SelectedAsset?.WorkState.ManualRecoveryRequired == true
        ? "No hay enlace verificable. Revisa la conversación; una nueva generación requiere autorización en Acciones de biblioteca."
        : SelectedAsset?.WorkState.Recoverable == true
            ? "Recupera el resultado de la operación guardada. No vuelve a enviar el prompt."
            : "Acción sobre la foto seleccionada. Conserva originales, intentos y resultados anteriores.";

    public void SetProviderStageText(string stage)
    {
        var active = _queue.CurrentProcessingAsset;
        QueueStatusText = active is null ? stage : $"{active.OriginalFileName}: {stage}";
    }

    [RelayCommand]
    private async Task ToggleQueuePauseAsync()
    {
        if (!CanPauseResumeQueue) return;
        if (IsQueuePaused) await ResumeQueueAsync();
        else await PauseQueueAsync();
    }

    private void NotifyAutomationControls()
    {
        foreach (var name in new[] { nameof(QueuePhaseLabel), nameof(QueuePrimaryActionLabel), nameof(QueuePrimaryActionHint),
            nameof(PauseResumeLabel), nameof(CanStartQueue), nameof(CanPauseResumeQueue), nameof(CanCancelCurrent),
            nameof(CanRetrySelected), nameof(SelectedActionLabel), nameof(SelectedActionHint) })
            OnPropertyChanged(name);
    }
}

internal readonly record struct QueueActionPresentation(
    string PrimaryLabel,
    string PrimaryHint,
    bool CanRunPrimary,
    string PauseLabel,
    bool CanPause,
    bool CanCancel)
{
    public static QueueActionPresentation Create(
        bool hasProject,
        bool running,
        bool paused,
        bool gptReady,
        bool actionsAvailable,
        bool hasCurrentAsset,
        int pendingCount)
    {
        var primaryLabel = running
            ? paused ? "Continuar cola" : "Procesando cola…"
            : !hasProject ? "Importar fotos"
            : !gptReady ? "Conectar GPT"
            : pendingCount == 0 ? "Todo procesado"
            : "Procesar pendientes";
        var primaryHint = !hasProject
            ? "Importa una carpeta de fotos para iniciar la cola."
            : !gptReady
                ? "Abre GPT Web e inicia sesión. La cola no enviará imágenes hasta que la sesión esté lista."
                : paused
                    ? "Continúa desde el punto guardado; no vuelve a enviar una generación ya aceptada."
                    : running
                        ? "La cola está trabajando con la foto actual."
                        : pendingCount == 0
                            ? "No hay fotos pendientes. Usa las acciones de biblioteca para recuperar o reintentar casos concretos."
                        : "Procesa las fotos pendientes de esta biblioteca.";

        return new QueueActionPresentation(
            primaryLabel,
            primaryHint,
            hasProject && actionsAvailable && (running ? paused : !gptReady || pendingCount > 0),
            paused ? "En pausa" : "Pausar cola",
            running && !paused && actionsAvailable,
            running && hasCurrentAsset);
    }
}
