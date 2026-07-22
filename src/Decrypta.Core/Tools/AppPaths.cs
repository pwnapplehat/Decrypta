namespace Decrypta.Core.Tools;

/// <summary>
/// Central filesystem layout. Bundled binaries live in a <c>tools\</c> folder next to the
/// running executable; per-user state (credentials, config) lives under LocalAppData; the
/// default output folder is under the user profile.
/// </summary>
public static class AppPaths
{
    public static string ToolsDir { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "tools");

    public static string IpadecryptExe => Path.Combine(ToolsDir, "ipadecrypt.exe");

    public static string StateDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Decrypta");

    /// <summary>Legacy single-account root (pre multi-account). Migrated into accounts\ on upgrade.</summary>
    public static string LegacyIpadecryptRoot => Path.Combine(StateDir, "ipadecrypt");

    public static string AccountsDir => Path.Combine(StateDir, "accounts");

    /// <summary>Per-account ipadecrypt root (its own config.json + cookies), isolated per Apple ID.</summary>
    public static string AccountRoot(string slug) => Path.Combine(AccountsDir, slug);

    public static string SettingsPath => Path.Combine(StateDir, "settings.json");

    public static string DefaultOutputDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Decrypta");

    public static void EnsureBaseDirs()
    {
        Directory.CreateDirectory(StateDir);
        Directory.CreateDirectory(AccountsDir);
    }
}
