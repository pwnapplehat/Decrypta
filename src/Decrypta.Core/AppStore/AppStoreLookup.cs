using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Decrypta.Core.AppStore;

/// <summary>
/// Resolves an App Store id / URL to an app's bundle identifier using Apple's public
/// <c>itunes.apple.com/lookup</c> endpoint (no sign-in, no account needed).
///
/// This is what lets "Use installed build" work when the user pastes an App Store link or
/// numeric id: ipadecrypt only matches an already-installed app by bundle id, so we convert
/// the id -> bundle id first and hand it that.
/// </summary>
public static partial class AppStoreLookup
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Decrypta/1.0 (+https://github.com/pwnapplehat/Decrypta)");
        return c;
    }

    /// <summary>True when the target is already a bundle identifier (not a URL, numeric id or
    /// local .ipa) - i.e. nothing to resolve.</summary>
    public static bool LooksLikeBundleId(string target)
    {
        target = target.Trim();
        if (target.Length == 0)
        {
            return false;
        }
        if (target.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (target.EndsWith(".ipa", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return !IsAllDigits(target);
    }

    /// <summary>Extract the numeric App Store id and (if present) the storefront country from a
    /// target. Returns (null, null) when the target isn't an id/URL (e.g. it's a bundle id).</summary>
    public static (string? AppId, string? Country) ParseAppStoreRef(string target)
    {
        target = target.Trim();
        if (IsAllDigits(target))
        {
            return (target, null);
        }
        if (target.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            var id = IdInUrl().Match(target);
            string? appId = id.Success ? id.Groups[1].Value : null;
            var cc = CountryInUrl().Match(target); // apps.apple.com/<cc>/app/...
            string? country = cc.Success ? cc.Groups[1].Value : null;
            return (appId, country);
        }
        return (null, null);
    }

    /// <summary>Look up the bundle id for an App Store numeric id, trying each storefront
    /// country in order (a region-locked app may only resolve in its own store). Returns null
    /// if none resolve.</summary>
    public static async Task<string?> LookupBundleIdAsync(
        string appId, IEnumerable<string?> countries, CancellationToken ct = default)
    {
        foreach (var country in countries.Where(c => !string.IsNullOrWhiteSpace(c)).Append(null).Distinct())
        {
            string url = $"https://itunes.apple.com/lookup?id={Uri.EscapeDataString(appId)}";
            if (!string.IsNullOrWhiteSpace(country))
            {
                url += $"&country={Uri.EscapeDataString(country!)}";
            }
            try
            {
                using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    continue;
                }
                await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                if (doc.RootElement.TryGetProperty("results", out var results) &&
                    results.ValueKind == JsonValueKind.Array &&
                    results.GetArrayLength() > 0 &&
                    results[0].TryGetProperty("bundleId", out var bundle) &&
                    bundle.GetString() is { Length: > 0 } bundleId)
                {
                    return bundleId;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                // try next country / give up
            }
        }
        return null;
    }

    private static bool IsAllDigits(string s) => s.Length > 0 && s.All(char.IsDigit);

    [GeneratedRegex(@"/id(\d+)")]
    private static partial Regex IdInUrl();

    [GeneratedRegex(@"apps\.apple\.com/([a-z]{2})/", RegexOptions.IgnoreCase)]
    private static partial Regex CountryInUrl();
}
