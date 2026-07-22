using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text;
using System.Xml.Linq;

namespace Decrypta.Core.AppStore;

/// <summary>
/// Minimal client for Apple's private <c>volumeStoreDownloadProduct</c> endpoint — the same call
/// the App Store app, ipatool and ipadecrypt all use to enumerate an app's releases. We only ever
/// read metadata (never fetch bytes), reusing ipadecrypt's session (<see cref="StoreKitSession"/>)
/// so there is no extra sign-in.
///
/// One call with an empty external version id returns the full ordered list of every release's
/// identifier (oldest→newest) plus metadata for the latest. A call with a specific id returns that
/// release's human version + date. These calls are independent, so many resolve in parallel.
/// </summary>
public sealed class StoreKitClient : IDisposable
{
    private const string DownloadPath = "/WebObjects/MZFinance.woa/wa/volumeStoreDownloadProduct";
    private const string UserAgent = "Configurator/2.17 (Macintosh; OS X 15.2; 24C5089c) AppleWebKit/0620.1.16.11.6";

    private readonly HttpClient _http;
    private readonly StoreKitSession _session;
    private readonly string _guid;
    private readonly string _baseUrl;

    public StoreKitClient(StoreKitSession session)
    {
        _session = session;
        _guid = DeviceGuid();
        string podPrefix = string.IsNullOrEmpty(session.Pod) ? "" : $"p{session.Pod}-";
        _baseUrl = $"https://{podPrefix}buy.itunes.apple.com{DownloadPath}?guid={_guid}";

        var handler = new HttpClientHandler
        {
            CookieContainer = session.Cookies,
            UseCookies = true,
            MaxConnectionsPerServer = 16,
            AutomaticDecompression = DecompressionMethods.All,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    /// <summary>
    /// The list call: returns every release identifier (newest first) and metadata for the latest.
    /// </summary>
    public async Task<StoreKitInfo> ListAsync(long adamId, CancellationToken ct = default)
        => Parse(await PostAsync(adamId, null, ct).ConfigureAwait(false));

    /// <summary>Metadata (human version + release date) for one specific release identifier.</summary>
    public async Task<AppVersion?> ResolveAsync(long adamId, string externalId, CancellationToken ct = default)
    {
        var info = Parse(await PostAsync(adamId, externalId, ct).ConfigureAwait(false));
        if (info.Error is not null || string.IsNullOrEmpty(info.DisplayVersion))
        {
            return null;
        }
        return new AppVersion(externalId, info.DisplayVersion);
    }

    private async Task<string> PostAsync(long adamId, string? externalId, CancellationToken ct)
    {
        var dict = new StringBuilder();
        dict.Append("<key>creditDisplay</key><string></string>");
        dict.Append("<key>guid</key><string>").Append(_guid).Append("</string>");
        dict.Append("<key>salableAdamId</key><integer>").Append(adamId).Append("</integer>");
        if (!string.IsNullOrEmpty(externalId))
        {
            dict.Append("<key>externalVersionId</key><string>").Append(externalId).Append("</string>");
        }
        string body =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">" +
            "<plist version=\"1.0\"><dict>" + dict + "</dict></plist>";

        using var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl);
        req.Content = new StringContent(body, Encoding.UTF8);
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-apple-plist");
        req.Headers.TryAddWithoutValidation("iCloud-DSID", _session.Dsid);
        req.Headers.TryAddWithoutValidation("X-Dsid", _session.Dsid);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new StoreKitException("Apple rate-limited the request (HTTP 429). Wait a bit and try again.");
        }
        return text;
    }

    /// <summary>Parse Apple's plist response for the bits we need (robust to nesting: key/value are
    /// always sibling elements inside a &lt;dict&gt;, so "value after key" works at any depth).</summary>
    public static StoreKitInfo Parse(string xml)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return new StoreKitInfo { Error = "unreadable response from App Store" };
        }

        string? failure = ScalarAfterKey(doc, "failureType");
        if (!string.IsNullOrEmpty(failure) && failure != "0")
        {
            string? msg = ScalarAfterKey(doc, "customerMessage");
            return new StoreKitInfo { Error = string.IsNullOrEmpty(msg) ? $"App Store error {failure}" : msg };
        }

        var ids = new List<string>();
        var arr = ValueAfterKey(doc, "softwareVersionExternalIdentifiers");
        if (arr is { } a && a.Name.LocalName == "array")
        {
            foreach (var el in a.Elements())
            {
                string v = el.Value.Trim();
                if (v.Length > 0)
                {
                    ids.Add(v);
                }
            }
        }

        return new StoreKitInfo
        {
            OrderedVersionIds = ids,
            LatestVersionId = ScalarAfterKey(doc, "softwareVersionExternalIdentifier"),
            DisplayVersion = ScalarAfterKey(doc, "bundleShortVersionString"),
        };
    }

    private static XElement? ValueAfterKey(XDocument doc, string key)
    {
        foreach (var k in doc.Descendants("key"))
        {
            if (k.Value == key)
            {
                return k.ElementsAfterSelf().FirstOrDefault();
            }
        }
        return null;
    }

    private static string? ScalarAfterKey(XDocument doc, string key)
    {
        var v = ValueAfterKey(doc, key);
        return v is null ? null : v.Value.Trim();
    }

    /// <summary>Configurator-shaped GUID (uppercase MAC, no separators). Apple does not validate it,
    /// but we use a real NIC MAC when available for realism, falling back to a stable constant.</summary>
    private static string DeviceGuid()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                byte[] mac = ni.GetPhysicalAddress().GetAddressBytes();
                if (mac.Length == 6 && mac.Any(b => b != 0))
                {
                    return Convert.ToHexString(mac);
                }
            }
        }
        catch (NetworkInformationException)
        {
            // fall through to constant
        }
        return "DECABDECAB01";
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Parsed result of a StoreKit download-info call.</summary>
public sealed class StoreKitInfo
{
    public IReadOnlyList<string> OrderedVersionIds { get; init; } = [];
    public string? LatestVersionId { get; init; }
    public string? DisplayVersion { get; init; }
    public string? Error { get; init; }
}

public sealed class StoreKitException : Exception
{
    public StoreKitException(string message) : base(message) { }
}
