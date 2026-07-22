namespace Decrypta.Core.AppStore;

/// <summary>
/// One selectable App Store version of an app. We intentionally don't carry a release date:
/// the download-info endpoint returns a stale one (always the app's original release), and the
/// accurate date only lives in each IPA's Info.plist, which we don't fetch just to label a row.
/// </summary>
public sealed record AppVersion(string ExternalId, string? DisplayVersion)
{
    public bool IsLatest { get; init; }

    /// <summary>Human label for the dropdown, e.g. "v439.0.0  (latest)".</summary>
    public string Label
    {
        get
        {
            string v = string.IsNullOrEmpty(DisplayVersion) ? $"id {ExternalId}" : $"v{DisplayVersion}";
            if (IsLatest)
            {
                v += "  (latest)";
            }
            return v;
        }
    }
}
