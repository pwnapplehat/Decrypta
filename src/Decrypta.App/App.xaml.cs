using System.Windows;
using System.Windows.Threading;
using Decrypta.Core.Tools;

namespace Decrypta.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private bool _ownsMutex;

    /// <summary>Set when launched with --minimized (e.g. the Windows-startup entry): boot quietly
    /// for the Telegram bot instead of popping the window up.</summary>
    public static bool StartMinimized { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        StartMinimized = e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));

        _singleInstance = new Mutex(initiallyOwned: true, "Decrypta.App.SingleInstance", out bool isNew);
        _ownsMutex = isNew;
        if (!isNew)
        {
            MessageBox.Show("Decrypta is already running.", "Decrypta",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        AppPaths.EnsureBaseDirs();
        DispatcherUnhandledException += OnUnhandled;

        // Brand accent for Fluent controls (Primary buttons, toggles, selection).
        Wpf.Ui.Appearance.ApplicationAccentColorManager.Apply(
            System.Windows.Media.Color.FromRgb(0x8B, 0x5C, 0xF6),
            Wpf.Ui.Appearance.ApplicationTheme.Dark);

        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        MessageBox.Show("Decrypta hit an unexpected error:\n\n" + args.Exception.Message,
            "Decrypta", MessageBoxButton.OK, MessageBoxImage.Error);
        args.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex)
        {
            _singleInstance?.ReleaseMutex();
        }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
