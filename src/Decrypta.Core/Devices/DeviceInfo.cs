namespace Decrypta.Core.Devices;

/// <summary>A connected iOS device, deduplicated across USB/Wi-Fi and enriched with
/// lockdown values when available.</summary>
public sealed record DeviceInfo(
    string Udid,
    int DeviceId,
    string Name,
    string ProductType,
    string ProductVersion,
    string BuildVersion,
    string Architecture,
    IReadOnlyList<string> ConnectionTypes)
{
    public string ConnectionSummary => ConnectionTypes.Count > 0 ? string.Join("+", ConnectionTypes) : "?";

    public string DisplayLine =>
        $"{Name} — {ProductType} · iOS {ProductVersion}";

    public string Summary =>
        $"{Name} ({ProductType}, iOS {ProductVersion} build {BuildVersion}, {Architecture}) " +
        $"[{ConnectionSummary}] udid={Udid}";
}
