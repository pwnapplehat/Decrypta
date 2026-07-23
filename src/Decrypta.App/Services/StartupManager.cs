using Microsoft.Win32;

namespace Decrypta.App.Services;

/// <summary>
/// Toggles "launch Decrypta when I sign in to Windows" via the per-user Run key (no admin needed).
/// The entry launches with <c>--minimized</c> so it boots quietly for the Telegram bot.
/// </summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Decrypta";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string s && s.Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static void Set(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null)
            {
                return;
            }
            if (enable)
            {
                string exe = Environment.ProcessPath ?? "";
                if (exe.Length > 0)
                {
                    key.SetValue(ValueName, $"\"{exe}\" --minimized");
                }
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception)
        {
            // registry unavailable / locked down — startup toggle just won't persist
        }
    }
}
