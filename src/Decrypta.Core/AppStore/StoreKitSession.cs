using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Decrypta.Core.AppStore;

/// <summary>
/// The App Store session that ipadecrypt establishes during <c>bootstrap</c>, loaded straight
/// from its config root so Decrypta can reuse it for version lookups — no second sign-in.
///
/// ipadecrypt persists two files under the account root:
///   • <c>config.json</c> — holds <c>apple.directoryServicesIdentifier</c> (dsid), <c>apple.pod</c>,
///     <c>apple.storeFront</c>.
///   • <c>cookies</c> — a JSON array (juju/persistent-cookiejar format) carrying the auth tokens
///     (<c>mz_at0_fr</c>, <c>X-Dsid</c>, …) that authorize the private StoreKit endpoint.
/// </summary>
public sealed class StoreKitSession
{
    public required string Dsid { get; init; }
    public required string Pod { get; init; }
    public string? StoreFront { get; init; }
    public required CookieContainer Cookies { get; init; }

    public bool IsUsable => !string.IsNullOrEmpty(Dsid) && Cookies.Count > 0;

    /// <summary>Load the session for an ipadecrypt config root, or null if the root/files are missing.</summary>
    public static StoreKitSession? Load(string? rootDir)
    {
        if (string.IsNullOrEmpty(rootDir))
        {
            return null;
        }
        string configPath = Path.Combine(rootDir, "config.json");
        string cookiesPath = Path.Combine(rootDir, "cookies");
        if (!File.Exists(configPath) || !File.Exists(cookiesPath))
        {
            return null;
        }

        string dsid = "", pod = "", storefront = "";
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            if (doc.RootElement.TryGetProperty("apple", out var apple) && apple.ValueKind == JsonValueKind.Object)
            {
                dsid = apple.TryGetProperty("directoryServicesIdentifier", out var d) ? d.GetString() ?? "" : "";
                pod = apple.TryGetProperty("pod", out var p) ? p.GetString() ?? "" : "";
                storefront = apple.TryGetProperty("storeFront", out var s) ? s.GetString() ?? "" : "";
            }
        }
        catch (JsonException)
        {
            return null;
        }

        var jar = LoadCookies(cookiesPath);
        if (jar is null)
        {
            return null;
        }

        return new StoreKitSession
        {
            Dsid = dsid,
            Pod = pod,
            StoreFront = string.IsNullOrEmpty(storefront) ? null : storefront,
            Cookies = jar,
        };
    }

    private static CookieContainer? LoadCookies(string path)
    {
        try
        {
            var entries = JsonSerializer.Deserialize<List<CookieEntry>>(File.ReadAllText(path));
            if (entries is null)
            {
                return null;
            }
            var jar = new CookieContainer();
            foreach (var e in entries)
            {
                if (string.IsNullOrEmpty(e.Name) || string.IsNullOrEmpty(e.Value))
                {
                    continue;
                }
                try
                {
                    // All the StoreKit auth cookies live under apple.com; scope them broadly so
                    // they're sent to buy.itunes.apple.com regardless of the original per-cookie host.
                    jar.Add(new Cookie(e.Name, e.Value, "/", ".apple.com"));
                }
                catch (CookieException)
                {
                    // skip a malformed cookie rather than fail the whole session
                }
            }
            return jar;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    private sealed class CookieEntry
    {
        [JsonPropertyName("Name")] public string? Name { get; set; }
        [JsonPropertyName("Value")] public string? Value { get; set; }
    }
}
