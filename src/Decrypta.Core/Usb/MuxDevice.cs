namespace Decrypta.Core.Usb;

/// <summary>A device as reported by usbmuxd. The same physical device can appear twice
/// (once as USB, once as Network) with the same <see cref="Udid"/>.</summary>
public sealed record MuxDevice(int DeviceId, string Udid, string ConnectionType)
{
    public bool IsUsb => string.Equals(ConnectionType, "USB", StringComparison.OrdinalIgnoreCase);
}
