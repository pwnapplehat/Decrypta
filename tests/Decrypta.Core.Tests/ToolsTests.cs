using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Decrypta.Core.Tools;
using Decrypta.Core.Tunnel;
using Xunit;

namespace Decrypta.Core.Tests;

public class AnsiTextTests
{
    [Fact]
    public void Strip_removes_color_codes_and_carriage_returns()
    {
        const string input = "\u001b[32m\u001b[1m\u2713\u001b[0m done\rprogress";
        Assert.Equal("\u2713 doneprogress", AnsiText.Strip(input));
    }

    [Fact]
    public void Strip_removes_cursor_and_erase_sequences()
    {
        const string input = "line\u001b[2K\u001b[1Aagain";
        Assert.Equal("lineagain", AnsiText.Strip(input));
    }
}

public class IpadecryptConfigTests
{
    private static string TempRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "decrypta-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Seeding_apple_and_device_marks_configured()
    {
        var root = TempRoot();
        try
        {
            var cfg = new IpadecryptConfig(root);
            Assert.False(cfg.IsAppleConfigured());
            Assert.False(cfg.IsDeviceConfigured());

            cfg.SetAppleCredentials("me@example.com", "pw");
            cfg.SetDeviceFull("root", "alpine", "127.0.0.1", 2222);

            Assert.True(cfg.IsAppleConfigured());
            Assert.True(cfg.IsDeviceConfigured());
            Assert.Equal("me@example.com", cfg.AppleEmail());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void SetDeviceEndpoint_preserves_user_and_password()
    {
        var root = TempRoot();
        try
        {
            var cfg = new IpadecryptConfig(root);
            cfg.SetDeviceFull("mobile", "secret", "127.0.0.1", 2222);
            cfg.SetDeviceEndpoint("127.0.0.1", 2250);

            var json = JsonNode.Parse(File.ReadAllText(cfg.ConfigPath))!;
            var device = json["device"]!;
            Assert.Equal(2250, (int)device["port"]!);
            Assert.Equal("mobile", (string)device["user"]!);
            Assert.Equal("secret", (string)device["auth"]!["password"]!);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ClearApple_unsets_email_but_keeps_device()
    {
        var root = TempRoot();
        try
        {
            var cfg = new IpadecryptConfig(root);
            cfg.SetAppleCredentials("me@example.com", "pw");
            cfg.SetDeviceFull("root", "alpine", "127.0.0.1", 2222);

            cfg.ClearApple();

            Assert.False(cfg.IsAppleConfigured());
            Assert.True(cfg.IsDeviceConfigured());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}

public class UsbTunnelTests
{
    [Fact]
    public void FindFreePort_returns_a_bindable_port()
    {
        int port = UsbTunnel.FindFreePort(23400);
        // Should be immediately bindable.
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        listener.Stop();
        Assert.InRange(port, 23400, 23440);
    }

    [Fact]
    public void FindFreePort_skips_a_port_already_in_use()
    {
        var blocker = new TcpListener(IPAddress.Loopback, 23500);
        blocker.Start();
        try
        {
            int port = UsbTunnel.FindFreePort(23500);
            Assert.NotEqual(23500, port);
        }
        finally
        {
            blocker.Stop();
        }
    }
}
