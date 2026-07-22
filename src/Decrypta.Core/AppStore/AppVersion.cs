namespace Decrypta.Core.AppStore;

/// <summary>One selectable App Store version of an app.</summary>
public sealed record AppVersion(string ExternalId, string? DisplayVersion, DateTime? ReleaseDate)
{
    public bool IsLatest { get; init; }

    /// <summary>Human label for the dropdown, e.g. "26.28.1  ·  2024-05-01  (latest)".</summary>
    public string Label
    {
        get
        {
            string v = string.IsNullOrEmpty(DisplayVersion) ? $"id {ExternalId}" : $"v{DisplayVersion}";
            if (ReleaseDate is { } d)
            {
                v += $"  ·  {d:yyyy-MM-dd}";
            }
            if (IsLatest)
            {
                v += "  (latest)";
            }
            return v;
        }
    }
}

/// <summary>Result of an ipatool auth attempt.</summary>
public sealed record IpatoolAuthResult(bool Ok, bool Needs2Fa, string? Error);
