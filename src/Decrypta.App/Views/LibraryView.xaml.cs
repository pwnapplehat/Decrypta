using System.Windows;
using System.Windows.Controls;
using Decrypta.App.ViewModels;

namespace Decrypta.App.Views;

public partial class LibraryView : UserControl
{
    public LibraryView() => InitializeComponent();

    private void OnReveal(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path } && DataContext is MainViewModel vm)
        {
            vm.RevealFile(path);
        }
    }
}
