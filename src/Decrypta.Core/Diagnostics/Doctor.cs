using Decrypta.Core.Devices;
using Decrypta.Core.Tools;
using Decrypta.Core.Tunnel;
using Decrypta.Core.Usb;

namespace Decrypta.Core.Diagnostics;

public enum CheckStatus { Ok, Warn, Fail }

public sealed record Check(CheckStatus Status, string Name, string Detail);

/// <summary>End-to-end environment check: binaries -> usbmuxd -> device -> USB SSH tunnel ->
/// stored credentials. The on-device prerequisites (AppSync/appinst) are surfaced as a note
/// because they can only be verified with the SSH password during sign-in/decrypt.</summary>
public sealed class Doctor
{
    private readonly UsbmuxClient _usbmux;
    private readonly DeviceService _devices;

    public Doctor(UsbmuxClient? usbmux = null)
    {
        _usbmux = usbmux ?? new UsbmuxClient();
        _devices = new DeviceService(_usbmux);
    }

    public List<Check> Run(string? udid = null)
    {
        var checks = new List<Check>();

        checks.Add(File.Exists(AppPaths.IpadecryptExe)
            ? new Check(CheckStatus.Ok, "ipadecrypt", new Ipadecrypt().Version())
            : new Check(CheckStatus.Fail, "ipadecrypt", $"missing at {AppPaths.IpadecryptExe}"));

        if (!_devices.ServiceReachable())
        {
            checks.Add(new Check(CheckStatus.Fail, "usbmuxd",
                "cannot reach Apple Mobile Device Service on 127.0.0.1:27015 (install the 'Apple Devices' app or iTunes)"));
            return checks;
        }

        List<DeviceInfo> devices;
        try
        {
            devices = _devices.ListDevices();
        }
        catch (UsbmuxException ex)
        {
            checks.Add(new Check(CheckStatus.Fail, "usbmuxd", ex.Message));
            return checks;
        }

        if (devices.Count == 0)
        {
            checks.Add(new Check(CheckStatus.Fail, "device",
                "no iOS device visible (plug in via USB, unlock, tap Trust)"));
            return checks;
        }

        checks.Add(new Check(CheckStatus.Ok, "usbmuxd", $"{devices.Count} device(s) visible"));

        var device = udid is null
            ? devices[0]
            : devices.FirstOrDefault(d => d.Udid.Replace("-", string.Empty)
                .Equals(udid.Replace("-", string.Empty), StringComparison.OrdinalIgnoreCase));
        if (device is null)
        {
            checks.Add(new Check(CheckStatus.Fail, "device", $"udid {udid} not connected"));
            return checks;
        }
        checks.Add(new Check(CheckStatus.Ok, "device", device.Summary));

        int port = UsbTunnel.FindFreePort();
        using (var tunnel = new UsbTunnel(_usbmux, device.DeviceId, 22, port).Start())
        {
            var banner = tunnel.VerifySshBanner(TimeSpan.FromSeconds(6));
            var via = device.ConnectionSummary;
            if (banner is not null && banner.StartsWith("SSH-", StringComparison.Ordinal))
            {
                checks.Add(new Check(CheckStatus.Ok, "ssh tunnel",
                    $"127.0.0.1:{tunnel.LocalPort} -> device:22 via {via} ({banner})"));
            }
            else if (banner is not null)
            {
                checks.Add(new Check(CheckStatus.Warn, "ssh tunnel", $"port 22 answered: {banner}"));
            }
            else
            {
                checks.Add(new Check(CheckStatus.Fail, "ssh tunnel",
                    "tunnel up but device:22 not answering (install OpenSSH from Sileo)"));
            }
        }

        var cfg = new IpadecryptConfig();
        checks.Add(cfg.IsAppleConfigured()
            ? new Check(CheckStatus.Ok, "sign-in", $"Apple ID {cfg.AppleEmail()} configured")
            : new Check(CheckStatus.Warn, "sign-in", "not signed in yet (use the Sign in tab)"));

        checks.Add(new Check(CheckStatus.Warn, "device prereqs",
            "AppSync Unified + appinst are verified during sign-in/decrypt; install from https://lukezgd.github.io/repo"));

        return checks;
    }
}
