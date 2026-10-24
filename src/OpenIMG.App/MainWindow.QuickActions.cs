using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OpenIMG.App.Browser;

namespace OpenIMG.App;

public partial class MainWindow
{
    private void OnGptResultStageChanged(string stage)
    {
        var text = GptOperationDiagnostics.ResultStageDisplayText(stage);
        if (text is null || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(() => _viewModel.SetProviderStageText(text)));
    }

    private async void OnCopyGptDiagnosticsClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Control button) return;
        button.IsEnabled = false;
        try
        {
            var report = JsonNode.Parse(await _gptWebClient.GetOperationDiagnosticsJsonAsync())!.AsObject();
            var attempt = _viewModel.SelectedAsset?.WorkState.LatestAttempt;
            report["selectedAttempt"] = JsonSerializer.SerializeToNode(new
            {
                selectedPhoto = _viewModel.SelectedAsset is not null,
                operationState = attempt?.ProviderOperationState.ToString(),
                checkpoint = GptOperationDiagnostics.DescribeSavedCheckpoint(attempt?.RawResponse)
            });
            Clipboard.SetText(report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            _viewModel.QuickActionMessage = "Diagnóstico de sesión y foto seleccionada copiado. No hace falta reintentar ni generar otra imagen.";
        }
        catch (Exception error)
        {
            Serilog.Log.Warning(error, "Could not copy safe GPT diagnostics");
            _viewModel.QuickActionMessage = "No se pudo copiar el diagnóstico GPT. Vuelve a pulsar el botón.";
        }
        finally { button.IsEnabled = true; }
    }

    private async void OnRetryGroupClicked(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsQuickActionRunning || _viewModel.IsResettingProcessingStates || sender is not MenuItem { Tag: string group }) return;
        var count = _viewModel.RetryGroupCount(group);
        if (count == 0) { _viewModel.QuickActionMessage = "No hay fotos que coincidan con esta acción."; return; }
        var explanation = group switch
        {
            "ReplaceMissing" => "Estas fotos no tienen un enlace de conversación recuperable. GPT podría haber generado ya una imagen. Esta acción autoriza expresamente una NUEVA generación para cada foto sin resultado y conserva todos los intentos anteriores.",
            "Review" => "Estas fotos ya tienen un resultado para revisar. Volverán a la cola y podrán generar una nueva imagen. Los resultados y revisiones anteriores se conservan.",
            "Visible" => "Se reintentarán las fotos de la lista visible que ya terminaron. Las que tengan resultado podrán generar uno nuevo; las recuperables se descargarán sin otro envío. El historial se conserva y los casos sin enlace se omitirán.",
            _ => "Se encolarán las fotos elegidas. Si hay una operación recuperable, se intentará descargar su imagen sin otro envío. Las que necesiten intervención manual se omitirán. La cola también continuará con sus fotos pendientes."
        };
        if (MessageBox.Show(this, $"{count} foto(s).\n\n{explanation}\n\n¿Continuar?", "Reintento por grupo", MessageBoxButton.YesNo,
            group == "ReplaceMissing" ? MessageBoxImage.Warning : MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunQuickActionAsync(() => _viewModel.RetryGroupAsync(group));
    }

    private string? SelectCopiesFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Carpeta para copias de resultados (sin aprobar automáticamente)" };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private async void OnSaveVisibleCopiesClicked(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsQuickActionRunning) return;
        var directory = SelectCopiesFolder();
        if (directory is not null) await RunQuickActionAsync(() => _viewModel.SaveVisibleCopiesAsync(directory));
    }

    private async void OnEnableAutomaticCopiesClicked(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsQuickActionRunning) return;
        var directory = SelectCopiesFolder();
        if (directory is null) return;
        await RunQuickActionAsync(async () =>
        {
            _viewModel.ConfigureAutomaticCopies(directory);
            await _viewModel.RefreshLibraryAsync();
            await _viewModel.SaveAllCopiesAsync(directory);
        });
    }

    private async void OnDisableAutomaticCopiesClicked(object sender, RoutedEventArgs e) =>
        await RunQuickActionAsync(() => { _viewModel.ConfigureAutomaticCopies(null); return Task.CompletedTask; });

    private async void OnOpenResultsFolderClicked(object sender, RoutedEventArgs e) => await RunQuickActionAsync(() =>
    {
        var directory = Path.GetFullPath(_viewModel.ResultsDirectory);
        Directory.CreateDirectory(directory);
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        start.ArgumentList.Add(directory);
        Process.Start(start);
        return Task.CompletedTask;
    });

    private async Task RunQuickActionAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error)
        {
            Serilog.Log.Warning(error, "Library quick action failed");
            _viewModel.QuickActionMessage = "La acción no se completó. Los originales y resultados se conservaron.";
            MessageBox.Show(this, error.Message, "Acción no completada", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
