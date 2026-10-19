using Microsoft.Win32;
using System.Windows;
using OpenIMG.App.ViewModels;

namespace OpenIMG.App.Views;

public partial class ReviewDialog : Window
{
    public ReviewDialog(ReviewViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.RequestClose += () => Close();
        viewModel.RequestExport += async () => await ExportCandidateAsync(viewModel);
    }

    private async Task ExportCandidateAsync(ReviewViewModel viewModel)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export approved restoration",
            FileName = System.IO.Path.GetFileNameWithoutExtension(viewModel.Asset.OriginalFileName) + "_restored.png",
            DefaultExt = ".png",
            Filter = "PNG image (*.png)|*.png",
            AddExtension = true,
            OverwritePrompt = false
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            await viewModel.ExportCandidateAsync(dialog.FileName);
            MessageBox.Show(this, "The approved image and its provenance sidecar were exported.", "Export complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
