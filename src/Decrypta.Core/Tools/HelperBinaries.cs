using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

namespace Decrypta.Core.Tools;

/// <summary>
/// Fetches the optional helper binaries the Telegram large-file features need, on demand, into
/// the tools folder — so the installer stays small and users never hand-install anything:
///   • cloudflared (official Cloudflare build) — for the zero-config download-link path.
///   • telegram-bot-api (a pinned, hash-verified third-party Windows build of tdlight's server)
///     — for the in-chat "up to 2 GB" path.
/// </summary>
public static class HelperBinaries
{
    // Official Cloudflare release (auto-updated); ~52 MB.
    private const string CloudflaredUrl =
        "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe";

    // Pinned third-party tdlight telegram-bot-api Windows build (exe + OpenSSL/zlib DLLs), hash-verified.
    private const string BotApiZipUrl =
        "https://github.com/std-microblock/tg-botapi-build/releases/download/nightly-tdlight-20260608/botapi-20260608.zip";
    private const string BotApiZipSha256 =
        "12b3a42dcd44d431469c2c63890f1dbd6370fc639dd7d8a133ac8b57b6731ffd";

    public static string CloudflaredExe => Path.Combine(AppPaths.ToolsDir, "cloudflared.exe");
    public static string BotApiDir => Path.Combine(AppPaths.ToolsDir, "botapi");
    public static string BotApiExe => Path.Combine(BotApiDir, "telegram-bot-api.exe");

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 8,
    })
    { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>Ensure cloudflared.exe is present (download once), returning its path.</summary>
    public static async Task<string> EnsureCloudflaredAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        if (File.Exists(CloudflaredExe) && new FileInfo(CloudflaredExe).Length > 1_000_000)
        {
            return CloudflaredExe;
        }
        Directory.CreateDirectory(AppPaths.ToolsDir);
        log?.Invoke("downloading cloudflared (one-time, ~52 MB)…");
        await DownloadAsync(CloudflaredUrl, CloudflaredExe, ct).ConfigureAwait(false);
        log?.Invoke("cloudflared ready.");
        return CloudflaredExe;
    }

    /// <summary>Ensure the telegram-bot-api server (+ its DLLs) is present, returning the exe path.
    /// The download is hash-verified against a pinned build.</summary>
    public static async Task<string> EnsureBotApiServerAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        if (File.Exists(BotApiExe) && new FileInfo(BotApiExe).Length > 1_000_000)
        {
            return BotApiExe;
        }
        Directory.CreateDirectory(BotApiDir);
        string zip = Path.Combine(BotApiDir, "botapi.zip");
        log?.Invoke("downloading Telegram Bot API server (one-time, ~12 MB)…");
        await DownloadAsync(BotApiZipUrl, zip, ct).ConfigureAwait(false);

        string actual = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(zip, ct).ConfigureAwait(false)))
            .ToLowerInvariant();
        if (!string.Equals(actual, BotApiZipSha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(zip);
            throw new DecryptaException(
                "Telegram Bot API server download failed integrity check (unexpected hash) — not extracting.");
        }

        ZipFile.ExtractToDirectory(zip, BotApiDir, overwriteFiles: true);
        File.Delete(zip);
        if (!File.Exists(BotApiExe))
        {
            throw new DecryptaException("Telegram Bot API server archive did not contain telegram-bot-api.exe.");
        }
        log?.Invoke("Telegram Bot API server ready.");
        return BotApiExe;
    }

    private static async Task DownloadAsync(string url, string destPath, CancellationToken ct)
    {
        string tmp = destPath + ".part";
        using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = File.Create(tmp);
            await src.CopyToAsync(dst, ct).ConfigureAwait(false);
        }
        if (File.Exists(destPath))
        {
            File.Delete(destPath);
        }
        File.Move(tmp, destPath);
    }
}
