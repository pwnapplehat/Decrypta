using System.Windows;
using System.Windows.Media;
using Decrypta.App.Services;
using Decrypta.App.ViewModels;
using Decrypta.App.Views;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Wpf.Ui.Interop;

namespace Decrypta.App;

public partial class MainWindow : FluentWindow
{
    private static readonly Type[] TabPages =
    [
        typeof(DecryptView),
        typeof(SignInView),
        typeof(LibraryView),
        typeof(TelegramView),
        typeof(DoctorView),
        typeof(SettingsView),
    ];

    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        FitToWorkArea();
        RootNavigation.SetPageProviderService(new PageService(_viewModel));
        _viewModel.NavigateRequested += NavigateToTab;

        Loaded += (_, _) =>
        {
            NavigateToTab(0);
            _viewModel.StartDevicePolling();
            _viewModel.StartTelegramOnLaunch();
        };
    }

    private void FitToWorkArea()
    {
        Rect work = SystemParameters.WorkArea;
        if (work.Width <= 0 || work.Height <= 0)
        {
            return;
        }
        Width = Math.Max(MinWidth, Math.Min(Width, work.Width * 0.96));
        Height = Math.Max(MinHeight, Math.Min(Height, work.Height * 0.96));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Mica: a subtle desktop-tinted material (not a wallpaper blur). Apply dark theme +
        // Mica, then clear the window background so the material shows through. When Mica
        // can't apply (Windows 10, or transparency effects disabled) keep the stock solid
        // dark background. Driven from code (XAML says None) so DWM never paints its own
        // washed-out fallback over a solid background.
        bool wantMica = IsSystemTransparencyEnabled();
        ApplicationThemeManager.Apply(
            ApplicationTheme.Dark,
            wantMica ? WindowBackdropType.Mica : WindowBackdropType.None,
            updateAccent: false);

        if (wantMica && WindowBackdrop.ApplyBackdrop(this, WindowBackdropType.Mica))
        {
            WindowBackdropType = WindowBackdropType.Mica;
            Background = Brushes.Transparent;
            MicaTint.Visibility = Visibility.Visible;
        }
        else if (TryFindResource("ApplicationBackgroundBrush") is Brush solid)
        {
            Background = solid;
        }
    }

    /// <summary>Settings → Personalization → Colors → "Transparency effects".</summary>
    private static bool IsSystemTransparencyEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("EnableTransparency") is not int enabled || enabled != 0;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private void NavigateToTab(int tab)
    {
        if (tab >= 0 && tab < TabPages.Length)
        {
            RootNavigation.Navigate(TabPages[tab]);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.ShutdownTelegram();
        base.OnClosed(e);
    }
}
