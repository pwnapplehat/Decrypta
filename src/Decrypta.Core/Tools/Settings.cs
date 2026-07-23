using System.Text.Json;

namespace Decrypta.Core.Tools;

/// <summary>An App Store account. Each has its own ipadecrypt root (config + cookies) under
/// accounts\&lt;slug&gt;, so multiple Apple IDs stay fully isolated.</summary>
public sealed class AccountEntry
{
    public string Email { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
}

/// <summary>User-facing app settings, persisted as JSON under LocalAppData.</summary>
public sealed class Settings
{
    public string OutputDirectory { get; set; } = AppPaths.DefaultOutputDir;
    public string SshUser { get; set; } = "root";
    public string Storefront { get; set; } = string.Empty;
    public bool VerboseLog { get; set; } = true;
    public bool AutoUpdateCheck { get; set; } = true;
    public List<AccountEntry> Accounts { get; set; } = [];
    public string? ActiveAccountSlug { get; set; }
    public string? LastUdid { get; set; }

    // ---- Telegram bot (control Decrypta from your phone) ----
    public bool TelegramEnabled { get; set; }
    public string TelegramBotToken { get; set; } = string.Empty;
    /// <summary>Chat ids allowed to control the bot (populated by /pair). Empty = nobody yet.</summary>
    public List<long> TelegramAllowedChatIds { get; set; } = [];
    /// <summary>Optional local Bot API server base URL (e.g. http://localhost:8081). Empty = Telegram's
    /// cloud API (50 MB upload cap). A local server raises the cap to ~2 GB so big IPAs can be sent.</summary>
    public string TelegramApiBaseUrl { get; set; } = string.Empty;

    public static Settings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsPath))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(AppPaths.SettingsPath));
                if (s is not null)
                {
                    if (string.IsNullOrWhiteSpace(s.OutputDirectory))
                    {
                        s.OutputDirectory = AppPaths.DefaultOutputDir;
                    }
                    return s;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // fall through to defaults
        }
        return new Settings();
    }

    public void Save()
    {
        AppPaths.EnsureBaseDirs();
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        var tmp = AppPaths.SettingsPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, AppPaths.SettingsPath, overwrite: true);
    }
}
