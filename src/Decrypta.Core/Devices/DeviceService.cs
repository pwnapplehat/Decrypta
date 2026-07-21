using Decrypta.Core.Usb;

namespace Decrypta.Core.Devices;

/// <summary>Discovers connected devices via usbmuxd and enriches them with lockdown values.</summary>
public sealed class DeviceService
{
    private static readonly string[] LockdownKeys =
        ["DeviceName", "ProductType", "ProductVersion", "BuildVersion", "CPUArchitecture"];

    private readonly UsbmuxClient _usbmux;

    public DeviceService(UsbmuxClient? usbmux = null) => _usbmux = usbmux ?? new UsbmuxClient();

    public bool ServiceReachable() => _usbmux.ServiceReachable();

    /// <summary>Fast presence check with no lockdown queries: (udid, connection types).</summary>
    public List<(string Udid, List<string> Connections)> QuickList()
    {
        var byUdid = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in _usbmux.ListDevices())
        {
            if (string.IsNullOrEmpty(d.Udid))
            {
                continue;
            }
            if (!byUdid.TryGetValue(d.Udid, out var list))
            {
                byUdid[d.Udid] = list = [];
            }
            if (!list.Contains(d.ConnectionType))
            {
                list.Add(d.ConnectionType);
            }
        }
        return byUdid.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary>One <see cref="DeviceInfo"/> per unique device, preferring the USB transport for
    /// lockdown enrichment.</summary>
    public List<DeviceInfo> ListDevices()
    {
        var muxDevices = _usbmux.ListDevices();
        var grouped = muxDevices
            .Where(d => !string.IsNullOrEmpty(d.Udid))
            .GroupBy(d => d.Udid, StringComparer.OrdinalIgnoreCase);

        var result = new List<DeviceInfo>();
        foreach (var group in grouped)
        {
            var connections = group.Select(d => d.ConnectionType).Distinct().OrderBy(c => c).ToList();
            var preferred = group.FirstOrDefault(d => d.IsUsb) ?? group.First();
            var values = TryReadLockdown(preferred.DeviceId);

            result.Add(new DeviceInfo(
                Udid: group.Key,
                DeviceId: preferred.DeviceId,
                Name: values.GetValueOrDefault("DeviceName", "iOS device"),
                ProductType: values.GetValueOrDefault("ProductType", "?"),
                ProductVersion: values.GetValueOrDefault("ProductVersion", "?"),
                BuildVersion: values.GetValueOrDefault("BuildVersion", "?"),
                Architecture: values.GetValueOrDefault("CPUArchitecture", "?"),
                ConnectionTypes: connections));
        }
        return result;
    }

    public DeviceInfo? Select(string? udid)
    {
        var devices = ListDevices();
        if (devices.Count == 0)
        {
            return null;
        }
        if (udid is null)
        {
            return devices[0];
        }
        var norm = udid.Replace("-", string.Empty);
        return devices.FirstOrDefault(d => d.Udid.Replace("-", string.Empty)
            .Equals(norm, StringComparison.OrdinalIgnoreCase));
    }

    private Dictionary<string, string> TryReadLockdown(int deviceId)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var lockdown = LockdownClient.Open(_usbmux, deviceId);
            lockdown.QueryType();
            foreach (var key in LockdownKeys)
            {
                var v = lockdown.GetValue(key);
                if (!string.IsNullOrEmpty(v))
                {
                    values[key] = v;
                }
            }
        }
        catch (Exception ex) when (ex is UsbmuxException or IOException or System.Net.Sockets.SocketException)
        {
            // Device locked / busy - fall back to whatever we have (may be empty).
        }
        return values;
    }
}
