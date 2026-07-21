using System.Windows.Controls;
using System.Windows.Input;
using Decrypta.App.ViewModels;

namespace Decrypta.App.Views;

public partial class SignInView : UserControl
{
    public SignInView() => InitializeComponent();

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void OnApplePwChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (Vm is not null)
        {
            Vm.ApplePassword = ApplePw.Password;
        }
    }

    private void OnSshPwChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (Vm is not null)
        {
            Vm.SshPassword = SshPw.Password;
        }
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Vm is not null)
        {
            Vm.SendConsoleCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnLogChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox tb)
        {
            tb.CaretIndex = tb.Text.Length;
            tb.ScrollToEnd();
        }
    }
}
