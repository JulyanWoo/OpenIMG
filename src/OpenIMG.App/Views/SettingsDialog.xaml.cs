using System.IO;
using OpenIMG.App.ViewModels;
using System.Windows;

namespace OpenIMG.App.Views;

public partial class SettingsDialog : Window
{
    public SettingsDialog(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.RequestClose += () => Close();
    }

    private void OnBrowseOutputFolderClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel)
            return;

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose where restored images will be saved",
            Multiselect = false
        };

        if (Directory.Exists(viewModel.DefaultOutputDirectory))
            dialog.InitialDirectory = viewModel.DefaultOutputDirectory;

        if (dialog.ShowDialog(this) == true)
            viewModel.DefaultOutputDirectory = Path.GetFullPath(dialog.FolderName);
    }
}
