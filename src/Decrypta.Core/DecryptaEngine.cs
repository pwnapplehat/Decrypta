using Decrypta.Core.Devices;
using Decrypta.Core.Tools;
using Decrypta.Core.Tunnel;
using Decrypta.Core.Usb;

namespace Decrypta.Core;

/// <summary>
/// The application core: device discovery plus the two long-running operations - sign-in
/// (ipadecrypt bootstrap, interactive for 2FA) and decrypt - each wired over a fresh USB
/// tunnel. UI and CLI front-ends drive this and just render the streamed output.
/// </summary>
public sealed class DecryptaEngine
{
    private readonly UsbmuxClient _usbmux;
    private readonly DeviceService _devices;
    private readonly Ipadecrypt _ipadecrypt;

    public DecryptaEngine()
    {
        _usbmux = new UsbmuxClient();
        _devices = new DeviceService(_usbmux);
        _ipadecrypt = new Ipadecrypt();
    }

    public DeviceService Devices => _devices;
    public Ipadecrypt Ipadecrypt => _ipadecrypt;

    public bool IsSignedIn => _ipadecrypt.Config.IsAppleConfigured();
    public string? SignedInEmail => _ipadecrypt.Config.AppleEmail();

    public RunningJob StartSignIn(DeviceInfo device, string email, string applePassword,
        string sshUser, string sshPassword, Action<string> onOutput)
    {
        EnsureToolsPresent();
        var (tunnel, banner) = OpenTunnel(device);
        onOutput($"[tunnel] 127.0.0.1:{tunnel.LocalPort} -> device:22 via {device.ConnectionSummary} ({banner ?? "no SSH banner"})\n");

        _ipadecrypt.Config.SetAppleCredentials(email, applePassword);
        _ipadecrypt.Config.SetDeviceFull(sshUser, sshPassword, "127.0.0.1", tunnel.LocalPort);

        var runner = _ipadecrypt.Bootstrap();
        runner.Output += onOutput;
        return new RunningJob(runner, tunnel);
    }

    public RunningJob StartDecrypt(DeviceInfo device, string target, string? output,
        IEnumerable<string> flags, Action<string> onOutput)
    {
        EnsureToolsPresent();
        bool localIpa = IsLocalIpa(target);
        if (!localIpa && !IsSignedIn)
        {
            throw new DecryptaException("Not signed in. Use the Sign in tab first.");
        }

        var (tunnel, banner) = OpenTunnel(device);
        if (banner is null || !banner.StartsWith("SSH-", StringComparison.Ordinal))
        {
            tunnel.Stop();
            tunnel.Dispose();
            throw new DecryptaException(
                "Device SSH is not reachable. Make sure OpenSSH is installed and running on the device.");
        }
        onOutput($"[tunnel] 127.0.0.1:{tunnel.LocalPort} -> device:22 via {device.ConnectionSummary} ({banner})\n");
        _ipadecrypt.Config.SetDeviceEndpoint("127.0.0.1", tunnel.LocalPort);
        if (!string.IsNullOrEmpty(output))
        {
            onOutput($"[output] {output}\n");
        }

        var runner = _ipadecrypt.Decrypt(target, output, flags);
        runner.Output += onOutput;
        return new RunningJob(runner, tunnel);
    }

    public void ResetSignIn() => _ipadecrypt.Config.ClearApple();

    public static string DefaultOutputPath(string outputDir, string target)
    {
        Directory.CreateDirectory(outputDir);
        return Path.Combine(outputDir, $"{SafeName(target)}.decrypted.ipa");
    }

    public static bool IsLocalIpa(string target)
        => target.EndsWith(".ipa", StringComparison.OrdinalIgnoreCase) && File.Exists(target);

    private (UsbTunnel Tunnel, string? Banner) OpenTunnel(DeviceInfo device)
    {
        int port = UsbTunnel.FindFreePort();
        var tunnel = new UsbTunnel(_usbmux, device.DeviceId, 22, port).Start();
        var banner = tunnel.VerifySshBanner(TimeSpan.FromSeconds(6));
        return (tunnel, banner);
    }

    private void EnsureToolsPresent()
    {
        if (!_ipadecrypt.Exists)
        {
            throw new DecryptaException($"ipadecrypt.exe not found under {AppPaths.ToolsDir}.");
        }
    }

    private static string SafeName(string target)
    {
        var name = target;
        if (name.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            var parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
            name = parts.LastOrDefault(p => p.StartsWith("id", StringComparison.OrdinalIgnoreCase)) ?? parts[^1];
        }
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return name;
    }
}
