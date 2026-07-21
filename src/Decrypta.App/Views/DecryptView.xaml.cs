using System.Windows.Controls;
using Decrypta.App.ViewModels;

namespace Decrypta.App.Views;

public partial class DecryptView : UserControl
{
    public DecryptView() => InitializeComponent();

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void OnSourceAppStore(object sender, System.Windows.RoutedEventArgs e)
    {
        if (Vm is not null)
        {
            Vm.SourceFromAppStore = true;
        }
    }

    private void OnSourceInstalled(object sender, System.Windows.RoutedEventArgs e)
    {
        if (Vm is not null)
        {
            Vm.SourceFromAppStore = false;
        }
    }

    private void OnLogChanged(object sender, TextChangedEventArgs e)
    {
        Log.CaretIndex = Log.Text.Length;
        Log.ScrollToEnd();
    }
}
