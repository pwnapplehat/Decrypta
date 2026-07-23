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
    private System.Windows.Forms.NotifyIcon? _tray;
    private bool _reallyQuit;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        FitToWorkArea();
        if (App.StartMinimized)
        {
            WindowState = WindowState.Minimized;
        }
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

    // While the Telegram bot is running, closing the window keeps Decrypta alive in the tray
    // (so the bot stays reachable). Without the bot running, closing quits normally.
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_reallyQuit && _viewModel.BotRunning)
        {
            e.Cancel = true;
            Hide();
            EnsureTray();
            _tray!.ShowBalloonTip(3000, "Decrypta",
                "Still running for the Telegram bot. Right-click the tray icon to quit.",
                System.Windows.Forms.ToolTipIcon.Info);
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.ShutdownTelegram();
        _tray?.Dispose();
        _tray = null;
        base.OnClosed(e);
    }

    private void EnsureTray()
    {
        if (_tray is not null)
        {
            return;
        }
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Text = "Decrypta — Telegram bot running",
            Visible = true,
        };
        try
        {
            if (Environment.ProcessPath is { } exe)
            {
                _tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
            }
        }
        catch (Exception)
        {
            // no icon available; the tray entry still works
        }
        _tray.DoubleClick += (_, _) => RestoreFromTray();
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open Decrypta", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Quit Decrypta", null, (_, _) => { _reallyQuit = true; Close(); });
        _tray.ContextMenuStrip = menu;
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }
}
