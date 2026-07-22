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
    //
    // Backed by Apple's volumeStoreDownloadProduct endpoint, reusing the active account's
    // ipadecrypt session (no second sign-in). One list call returns every release id (newest
    // first); names/dates are resolved per id in parallel, so it's fast and can page on demand.

    /// <summary>Opaque handle to a loaded version list: the app's numeric id plus every release
    /// identifier ordered newest→latest-first. Hand slices of <see cref="VersionIds"/> back to
    /// <see cref="ResolveVersionsAsync"/> to fill in human version numbers a page at a time.</summary>
    public sealed record VersionList(long AdamId, IReadOnlyList<string> VersionIds);

    public sealed record VersionListResult(VersionList? List, string? Error);

    /// <summary>
    /// Resolve the target to a numeric App Store id and fetch the full ordered list of its release
    /// identifiers in a single call. No names are resolved yet — call <see cref="ResolveVersionsAsync"/>
    /// for the page you want to show. Returns a friendly error if the session is missing/expired.
    /// </summary>
    public async Task<VersionListResult> LoadVersionListAsync(
        string target, Action<string>? progress, CancellationToken ct = default)
    {
        long? adamId = null;
        var (parsedId, country) = AppStoreLookup.ParseAppStoreRef(target);
        var countries = new[] { country, string.IsNullOrWhiteSpace(_settings.Storefront) ? null : _settings.Storefront };
        if (parsedId is not null && long.TryParse(parsedId, out long pid))
        {
            adamId = pid;
        }
        else if (AppStoreLookup.LooksLikeBundleId(target))
        {
            progress?.Invoke($"resolving {target.Trim()}…\n");
            adamId = await AppStoreLookup.LookupAppIdAsync(target.Trim(), countries, ct).ConfigureAwait(false);
            if (adamId is null)
            {
                return new VersionListResult(null, $"Couldn't resolve {target.Trim()} to an App Store id.");
            }
        }
        else
        {
            return new VersionListResult(null, "Enter a bundle id, App Store id or link first.");
        }

        var session = StoreKitSession.Load(_accounts.Active()?.RootDir);
        if (session is null || !session.IsUsable)
        {
            return new VersionListResult(null, "Sign in first — listing versions needs your Apple ID session.");
        }

        progress?.Invoke("listing versions…\n");
        try
        {
            using var client = new StoreKitClient(session);
            var info = await client.ListAsync(adamId.Value, ct).ConfigureAwait(false);
            if (info.Error is not null)
            {
                return new VersionListResult(null, SessionHint(info.Error));
            }
            if (info.OrderedVersionIds.Count == 0)
            {
                return new VersionListResult(null, "No versions returned for this app.");
            }
            var newestFirst = info.OrderedVersionIds.Reverse().ToList(); // Apple returns oldest→newest
            return new VersionListResult(new VersionList(adamId.Value, newestFirst), null);
        }
        catch (StoreKitException ex)
        {
            return new VersionListResult(null, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return new VersionListResult(null, $"network error listing versions: {ex.Message}");
        }
    }

    /// <summary>Resolve a set of release identifiers to human version numbers + dates, in parallel
    /// (throttled). Unresolved ids still come back (labelled by id) so the UI never loses a row.
    /// <paramref name="latestId"/> is flagged as the current release.</summary>
    public async Task<IReadOnlyList<AppVersion>> ResolveVersionsAsync(
        long adamId, IReadOnlyList<string> ids, string? latestId, CancellationToken ct = default)
    {
        if (ids.Count == 0)
        {
            return [];
        }
        var session = StoreKitSession.Load(_accounts.Active()?.RootDir)
            ?? throw new DecryptaException("Apple ID session not found — sign in first.");
        using var client = new StoreKitClient(session);
        using var gate = new SemaphoreSlim(8);

        var tasks = ids.Select(async id =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var v = await client.ResolveAsync(adamId, id, ct).ConfigureAwait(false)
                        ?? new AppVersion(id, null);
                return v with { IsLatest = id == latestId };
            }
            catch (HttpRequestException)
            {
                return new AppVersion(id, null) { IsLatest = id == latestId };
            }
            finally
            {
                gate.Release();
            }
        });
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static string SessionHint(string error)
    {
        // Apple's token-expiry failures surface as generic store errors; nudge the user to refresh.
        if (error.Contains("2034") || error.Contains("2042") ||
            error.Contains("expired", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("sign", StringComparison.OrdinalIgnoreCase))
        {
            return "Apple ID session expired — run a decrypt or re-sign-in to refresh, then try again.";
        }
        return error;
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
