using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Decrypta.Core.AppStore;
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

public class AppStoreLookupTests
{
    [Theory]
    [InlineData("https://apps.apple.com/ee/app/vinted-shop-sell-pre-loved/id632064380", "632064380", "ee")]
    [InlineData("https://apps.apple.com/us/app/instagram/id389801252", "389801252", "us")]
    [InlineData("632064380", "632064380", null)]
    public void ParseAppStoreRef_extracts_id_and_country(string target, string expectedId, string? expectedCountry)
    {
        var (appId, country) = AppStoreLookup.ParseAppStoreRef(target);
        Assert.Equal(expectedId, appId);
        Assert.Equal(expectedCountry, country);
    }

    [Fact]
    public void ParseAppStoreRef_returns_none_for_a_bundle_id()
    {
        var (appId, country) = AppStoreLookup.ParseAppStoreRef("lt.manodrabuziai.fr");
        Assert.Null(appId);
        Assert.Null(country);
    }

    [Theory]
    [InlineData("lt.manodrabuziai.fr", true)]
    [InlineData("com.burbn.instagram", true)]
    [InlineData("632064380", false)]                                    // numeric id
    [InlineData("https://apps.apple.com/us/app/x/id1", false)]          // url
    [InlineData("C:/some/app.ipa", false)]                             // local ipa
    public void LooksLikeBundleId_classifies_targets(string target, bool expected)
    {
        Assert.Equal(expected, AppStoreLookup.LooksLikeBundleId(target));
    }
}

public class IpatoolVersionParsingTests
{
    [Fact]
    public void ParseVersionIds_reads_ipatool_json_log_line()
    {
        // ipatool --format json emits zerolog-style JSON lines.
        const string stdout =
            "{\"level\":\"info\",\"externalVersionIdentifiers\":[\"630253062\",\"836887817\",\"842927320\"]," +
            "\"bundleID\":\"com.apple.TestFlight\",\"success\":true}\n";
        var ids = Ipatool.ParseVersionIds(stdout);
        Assert.Equal(new[] { "630253062", "836887817", "842927320" }, ids);
    }

    [Fact]
    public void ParseVersionIds_handles_numeric_array_and_noise_lines()
    {
        const string stdout =
            "starting\n{\"externalVersionIdentifiers\":[111,222],\"success\":true}\ndone\n";
        var ids = Ipatool.ParseVersionIds(stdout);
        Assert.Equal(new[] { "111", "222" }, ids);
    }

    [Fact]
    public void ParseVersionIds_returns_empty_when_absent()
    {
        Assert.Empty(Ipatool.ParseVersionIds("{\"level\":\"error\",\"message\":\"nope\"}"));
    }

    [Fact]
    public void AppVersion_label_formats_version_date_and_latest()
    {
        var v = new AppVersion("842927320", "26.28.1", new DateTime(2024, 5, 1)) { IsLatest = true };
        Assert.Contains("v26.28.1", v.Label);
        Assert.Contains("2024-05-01", v.Label);
        Assert.Contains("latest", v.Label);

        var unresolved = new AppVersion("999", null, null);
        Assert.Contains("id 999", unresolved.Label);
    }
}

public class AccountServiceTests
{
    [Fact]
    public void SlugFor_is_deterministic_and_filesystem_safe()
    {
        var a = AccountService.SlugFor("Me.Name+tag@example.com");
        var b = AccountService.SlugFor("me.name+tag@example.com"); // case-insensitive
        Assert.Equal(a, b);
        Assert.DoesNotContain('@', a);
        Assert.DoesNotContain('+', a);
        Assert.DoesNotContain('.', a);
        Assert.Matches("^[a-z0-9_]+$", a);
    }

    [Fact]
    public void SlugFor_distinguishes_different_emails()
    {
        Assert.NotEqual(AccountService.SlugFor("a@example.com"), AccountService.SlugFor("b@example.com"));
    }
}

public class CacheManagerTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "decrypta-cache-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Clean_wipes_full_and_partial_downloads_and_reports_bytes()
    {
        var outDir = TempDir();
        try
        {
            var cache = Path.Combine(outDir, CacheManager.CacheFolderName);
            Directory.CreateDirectory(cache);
            File.WriteAllBytes(Path.Combine(cache, "app_1.0.ipa"), new byte[2048]);
            File.WriteAllBytes(Path.Combine(cache, "app_2.0.ipa.tmp"), new byte[1024]);

            long size = CacheManager.CacheSizeBytes(outDir, []);
            Assert.Equal(3072, size);

            long freed = CacheManager.Clean(outDir, []);
            Assert.Equal(3072, freed);
            Assert.Empty(Directory.GetFiles(cache));
        }
        finally
        {
            Directory.Delete(outDir, true);
        }
    }

    [Fact]
    public void CleanPartials_removes_only_tmp_files()
    {
        var outDir = TempDir();
        try
        {
            var cache = Path.Combine(outDir, CacheManager.CacheFolderName);
            Directory.CreateDirectory(cache);
            var full = Path.Combine(cache, "app_1.0.ipa");
            File.WriteAllBytes(full, new byte[2048]);
            File.WriteAllBytes(Path.Combine(cache, "app_2.0.ipa.tmp"), new byte[512]);

            long freed = CacheManager.CleanPartials(outDir, []);
            Assert.Equal(512, freed);
            Assert.True(File.Exists(full));                 // completed download kept
            Assert.Empty(Directory.GetFiles(cache, "*.tmp")); // partials gone
        }
        finally
        {
            Directory.Delete(outDir, true);
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
