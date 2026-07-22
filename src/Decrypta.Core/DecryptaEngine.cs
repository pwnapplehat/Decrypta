using Decrypta.Core.AppStore;
using Decrypta.Core.Devices;
using Decrypta.Core.Tools;
using Decrypta.Core.Tunnel;
using Decrypta.Core.Usb;

namespace Decrypta.Core;

/// <summary>
/// The application core: device discovery, multi-account App Store sign-in, and decrypt -
/// each wired over a fresh USB/Wi-Fi tunnel. The encrypted-download cache is kept inside the
/// user's output folder so everything is contained and cleanable. UI and CLI front-ends
/// drive this and render the streamed output.
/// </summary>
public sealed class DecryptaEngine
{
    private readonly UsbmuxClient _usbmux;
    private readonly DeviceService _devices;
    private readonly Settings _settings;
    private readonly AccountService _accounts;
    private readonly Ipatool _ipatool = new();

    public DecryptaEngine(Settings? settings = null)
    {
        _usbmux = new UsbmuxClient();
        _devices = new DeviceService(_usbmux);
        _settings = settings ?? Settings.Load();
        _accounts = new AccountService(_settings);
    }

    public DeviceService Devices => _devices;
    public AccountService Accounts => _accounts;

    public bool IsSignedIn => _accounts.HasActiveConfigured;
    public string? SignedInEmail => _accounts.ActiveEmail;

    // ---- accounts ----
    public IReadOnlyList<AccountView> ListAccounts() => _accounts.Accounts();
    public void SwitchAccount(string slug) => _accounts.SetActive(slug);
    public void RemoveAccount(string slug) => _accounts.Remove(slug);

    /// <summary>Sign out the active account (clears its stored Apple credentials but keeps the
    /// account slot so it can be re-signed-in).</summary>
    public void SignOutActive() => _accounts.Active()?.Config.ClearApple();

    public RunningJob StartSignIn(DeviceInfo device, string email, string applePassword,
        string sshUser, string sshPassword, Action<string> onOutput)
    {
        EnsureToolsPresent();
        if (string.IsNullOrWhiteSpace(email))
        {
            throw new DecryptaException("Enter an Apple ID email.");
        }

        var idec = _accounts.EnsureAndActivate(email.Trim());
        var (tunnel, banner) = OpenTunnel(device);
        onOutput($"[tunnel] 127.0.0.1:{tunnel.LocalPort} -> device:22 via {device.ConnectionSummary} ({banner ?? "no SSH banner"})\n");

        idec.Config.SetAppleCredentials(email.Trim(), applePassword);
        idec.Config.SetDeviceFull(sshUser, sshPassword, "127.0.0.1", tunnel.LocalPort);

        var runner = idec.Bootstrap();
        runner.Output += onOutput;
        return new RunningJob(runner, tunnel);
    }

    public RunningJob StartDecrypt(DeviceInfo device, string target, string? output,
        IEnumerable<string> flags, Action<string> onOutput)
    {
        EnsureToolsPresent();
        bool localIpa = IsLocalIpa(target);
        var idec = _accounts.Active();
        if (!localIpa && (idec is null || !idec.Config.IsAppleConfigured()))
        {
            throw new DecryptaException("Not signed in. Use the Sign in tab first.");
        }
        idec ??= _accounts.EnsureAndActivate("local");

        // Keep the encrypted-download cache inside the user's output folder (contained + cleanable).
        string outputDir = !string.IsNullOrEmpty(output)
            ? Path.GetDirectoryName(output) ?? _settings.OutputDirectory
            : _settings.OutputDirectory;
        Directory.CreateDirectory(outputDir);
        bool redirected = CacheManager.RedirectCache(idec.RootDir, outputDir);

        var (tunnel, banner) = OpenTunnel(device);
        if (banner is null || !banner.StartsWith("SSH-", StringComparison.Ordinal))
        {
            tunnel.Stop();
            tunnel.Dispose();
            throw new DecryptaException(
                "Device SSH is not reachable. Make sure OpenSSH is installed and running on the device.");
        }
        onOutput($"[tunnel] 127.0.0.1:{tunnel.LocalPort} -> device:22 via {device.ConnectionSummary} ({banner})\n");
        onOutput($"[cache] {(redirected ? Path.Combine(outputDir, CacheManager.CacheFolderName) : idec.RootDir + "\\cache (fallback)")}\n");
        idec.Config.SetDeviceEndpoint("127.0.0.1", tunnel.LocalPort);
        if (!string.IsNullOrEmpty(output))
        {
            onOutput($"[output] {output}\n");
        }

        var runner = idec.Decrypt(target, output, flags);
        runner.Output += onOutput;
        return new RunningJob(runner, tunnel);
    }

    // ---- version listing (for the "pick a specific version" UI) ----

    public sealed record VersionsResult(IReadOnlyList<AppVersion>? Versions, bool Needs2Fa, string? Error);

    /// <summary>
    /// List an app's App Store versions so the user can pick one. Resolves the target to a
    /// bundle id, ensures ipatool is signed in as the active account (returns Needs2Fa so the
    /// caller can prompt for a code), lists every external version id, and resolves the
    /// human version number + date for the newest <paramref name="resolveNewest"/> only —
    /// each metadata call hits Apple's private endpoint, which is rate-limited, so we keep it
    /// small and gently paced. Older versions are still selectable by id.
    /// </summary>
    public async Task<VersionsResult> LoadVersionsAsync(
        string target, string? authCode, int resolveNewest, Action<string>? progress, CancellationToken ct = default)
    {
        if (!_ipatool.Exists)
        {
            return new VersionsResult(null, false, "ipatool.exe not found under tools\\.");
        }

        string? bundleId = AppStoreLookup.LooksLikeBundleId(target) ? target.Trim() : null;
        if (bundleId is null)
        {
            var (appId, country) = AppStoreLookup.ParseAppStoreRef(target);
            if (appId is null)
            {
                return new VersionsResult(null, false, "Enter a bundle id, App Store id or link first.");
            }
            progress?.Invoke($"resolving App Store id {appId}…\n");
            bundleId = await AppStoreLookup.LookupBundleIdAsync(
                appId, [country, string.IsNullOrWhiteSpace(_settings.Storefront) ? null : _settings.Storefront], ct)
                .ConfigureAwait(false);
            if (bundleId is null)
            {
                return new VersionsResult(null, false, $"Couldn't resolve id {appId} to a bundle id.");
            }
        }

        var idec = _accounts.Active();
        var email = idec?.Config.AppleEmail();
        var password = idec?.Config.ApplePassword();
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
        {
            return new VersionsResult(null, false, "Sign in first — listing versions needs your Apple ID.");
        }

        var who = await _ipatool.AuthInfoEmailAsync(ct).ConfigureAwait(false);
        if (!string.Equals(who, email, StringComparison.OrdinalIgnoreCase))
        {
            progress?.Invoke("signing in to ipatool for version listing…\n");
            var auth = await _ipatool.AuthLoginAsync(email!, password!, authCode, ct).ConfigureAwait(false);
            if (!auth.Ok)
            {
                return auth.Needs2Fa
                    ? new VersionsResult(null, true, null)
                    : new VersionsResult(null, false, auth.Error ?? "ipatool sign-in failed");
            }
        }

        progress?.Invoke($"listing versions for {bundleId}…\n");
        IReadOnlyList<string> ids;
        try
        {
            ids = await _ipatool.ListVersionIdsAsync(bundleId, ct).ConfigureAwait(false);
        }
        catch (IpatoolException ex)
        {
            return new VersionsResult(null, false, ex.Message);
        }
        if (ids.Count == 0)
        {
            return new VersionsResult([], false, null);
        }

        var newestFirst = ids.Reverse().ToList(); // Apple returns oldest->newest
        int toResolve = Math.Min(resolveNewest, newestFirst.Count);
        var result = new List<AppVersion>(newestFirst.Count);
        for (int i = 0; i < newestFirst.Count; i++)
        {
            string id = newestFirst[i];
            if (i < toResolve)
            {
                progress?.Invoke($"resolving version {i + 1}/{toResolve}…\n");
                var meta = await _ipatool.GetVersionMetadataAsync(bundleId, id, ct).ConfigureAwait(false);
                result.Add((meta ?? new AppVersion(id, null, null)) with { IsLatest = i == 0 });
                await Task.Delay(120, ct).ConfigureAwait(false);
            }
            else
            {
                result.Add(new AppVersion(id, null, null) { IsLatest = i == 0 });
            }
        }
        return new VersionsResult(result, false, null);
    }

    // ---- cache / cleanup ----

    public long CacheSizeBytes() =>
        CacheManager.CacheSizeBytes(_settings.OutputDirectory, _accounts.AllRoots());

    /// <summary>Wipe every cached and partial (.tmp) encrypted download. Returns bytes freed.</summary>
    public long CleanCache() =>
        CacheManager.Clean(_settings.OutputDirectory, _accounts.AllRoots());

    /// <summary>Remove only partial (.tmp) downloads from an interrupted decrypt. Returns bytes freed.</summary>
    public long CleanPartials() =>
        CacheManager.CleanPartials(_settings.OutputDirectory, _accounts.AllRoots());

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

    private static void EnsureToolsPresent()
    {
        if (!File.Exists(AppPaths.IpadecryptExe))
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
