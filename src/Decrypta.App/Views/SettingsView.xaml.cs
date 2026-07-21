using System.Windows.Controls;
using Decrypta.App.ViewModels;
using Microsoft.Win32;

namespace Decrypta.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private void OnBrowse(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }
        var dialog = new OpenFolderDialog
        {
            Title = "Choose where decrypted IPAs are saved",
            InitialDirectory = vm.OutputDirectory,
        };
        if (dialog.ShowDialog() == true)
        {
            vm.OutputDirectory = dialog.FolderName;
        }
    }
}
