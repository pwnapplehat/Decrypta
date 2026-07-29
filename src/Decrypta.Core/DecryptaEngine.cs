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

        // Output can be a directory (let ipadecrypt name it <bundleId>_<version>.decrypted.ipa),
        // a full file path, or null (fall back to the default output folder, directory mode).
        string outArg = string.IsNullOrEmpty(output) ? _settings.OutputDirectory : output;
        // Keep the encrypted-download cache inside the user's output folder (contained + cleanable).
        string outputDir = Directory.Exists(outArg)
            ? outArg
            : Path.GetDirectoryName(outArg) ?? _settings.OutputDirectory;
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
        onOutput($"[output] {outArg}\n");

        var runner = idec.Decrypt(target, outArg, flags);
        runner.Output += onOutput;
        return new RunningJob(runner, tunnel);
    }

    // ---- high-level decrypt (shared by GUI, CLI and the Telegram bot) ----

    /// <summary>Options for a decrypt run, mirroring the GUI toggles.</summary>
    public sealed record DecryptRequest(
        string Target,
        bool FromAppStore = true,
        bool SkipAppex = false,
        bool PatchDeviceType = false,
        bool Verbose = true,
        string? ExternalVersionId = null,
        string? Storefront = null);

    /// <summary>Outcome of a decrypt run. <see cref="OutputPath"/> is the final, tidily-named IPA.
    /// <see cref="Error"/> is set when the run failed (including when ipadecrypt exits 0 but wrote
    /// nothing — e.g. appinst missing on the device).</summary>
    public sealed record DecryptResult(int ExitCode, string? OutputPath, string? FileName, long Bytes, string? Error = null)
    {
        public bool Ok => ExitCode == 0 && OutputPath is not null && Error is null;
    }

    /// <summary>
    /// Run a full decrypt end to end: resolve the target (incl. id/URL → bundle id for the
    /// "use installed build" case), build flags, let ipadecrypt name the file
    /// <c>&lt;bundleId&gt;_&lt;version&gt;.decrypted.ipa</c> in the output folder (accurate for App
    /// Store, installed and local-IPA sources alike), then tidy the name to
    /// <c>&lt;bundleId&gt;_&lt;version&gt;.ipa</c>. On failure, partial downloads are cleaned up.
    /// </summary>
    public async Task<DecryptResult> DecryptAsync(
        DeviceInfo device, DecryptRequest req, Action<string> onOutput,
        Action<RunningJob>? onJob = null, CancellationToken ct = default)
    {
        string target = req.Target.Trim();
        bool pinned = !string.IsNullOrWhiteSpace(req.ExternalVersionId);

        // "Use installed build" matches by bundle id, so resolve an id/URL to a bundle id first.
        if (!req.FromAppStore && !pinned && !IsLocalIpa(target) && !AppStoreLookup.LooksLikeBundleId(target))
        {
            var (appId, country) = AppStoreLookup.ParseAppStoreRef(target);
            if (appId is not null)
            {
                onOutput($"resolving App Store id {appId}…\n");
                var sf = req.Storefront ?? (string.IsNullOrWhiteSpace(_settings.Storefront) ? null : _settings.Storefront);
                var bundleId = await AppStoreLookup.LookupBundleIdAsync(appId, [country, sf], ct).ConfigureAwait(false);
                if (bundleId is not null)
                {
                    onOutput($"[resolve] App Store id {appId} -> {bundleId} (using installed build)\n");
                    target = bundleId;
                }
                else
                {
                    onOutput($"[resolve] couldn't resolve id {appId} to a bundle id — fetching from the App Store instead.\n");
                }
            }
        }

        var flags = new List<string>();
        if (req.Verbose)
        {
            flags.Add("--verbose");
        }
        // A pinned historical version can only come from the App Store (never the installed build).
        flags.Add(req.FromAppStore || pinned ? "--from-appstore" : "--use-installed");
        if (req.SkipAppex)
        {
            flags.Add("--skip-appex");
        }
        if (req.PatchDeviceType)
        {
            flags.Add("--patch-device-type");
        }
        if (pinned)
        {
            flags.Add("--external-version-id");
            flags.Add(req.ExternalVersionId!.Trim());
        }
        string? storefront = req.Storefront ?? (string.IsNullOrWhiteSpace(_settings.Storefront) ? null : _settings.Storefront);
        if (!string.IsNullOrWhiteSpace(storefront))
        {
            flags.Add("--storefront");
            flags.Add(storefront.Trim());
        }

        string outputDir = _settings.OutputDirectory;
        Directory.CreateDirectory(outputDir);
        var before = SnapshotIpas(outputDir);

        // Tee ipadecrypt's stream so we can surface its last [err] line if it exits without
        // producing an IPA (upstream sometimes returns exit 0 after printing [err]).
        string? lastErr = null;
        void Tee(string chunk)
        {
            onOutput(chunk);
            foreach (var line in chunk.Split('\n'))
            {
                string t = line.Trim();
                if (t.Contains("[err]", StringComparison.OrdinalIgnoreCase))
                {
                    lastErr = t;
                }
            }
        }

        var job = StartDecrypt(device, target, outputDir, flags, Tee);
        onJob?.Invoke(job);
        await using var reg = ct.Register(() => { try { job.Cancel(); } catch (InvalidOperationException) { } });
        int rc = await job.Completion.ConfigureAwait(false);

        if (rc != 0)
        {
            CleanPartials();
            return new DecryptResult(rc, null, null, 0, lastErr ?? $"decrypt exited with code {rc}");
        }

        var produced = ClaimNewIpa(outputDir, before);
        if (produced is null)
        {
            // CRITICAL: never treat "exit 0 + an older IPA already in the folder" as success.
            // That was claiming e.g. com.burbn.instagram_439.0.0.ipa after a failed 440 run.
            CleanPartials();
            string msg = ExplainMissingOutput(lastErr);
            onOutput($"[fail] {msg}\n");
            return new DecryptResult(1, null, null, 0, msg);
        }

        return new DecryptResult(0, produced.FullName, produced.Name, produced.Length);
    }

    /// <summary>Friendly failure when ipadecrypt exited 0 but wrote no decrypted IPA.</summary>
    private static string ExplainMissingOutput(string? lastErr)
    {
        if (!string.IsNullOrWhiteSpace(lastErr))
        {
            // Strip a leading "[err] " so the dialog isn't noisy.
            string bare = lastErr.Trim();
            if (bare.StartsWith("[err]", StringComparison.OrdinalIgnoreCase))
            {
                bare = bare["[err]".Length..].TrimStart(' ', '-', ':');
            }
            if (bare.Contains("appinst", StringComparison.OrdinalIgnoreCase))
            {
                return $"{bare}\n\nOpen the Sign in tab and run Sign in (bootstrap) so appinst is installed on the device, then try again.";
            }
            return bare;
        }
        return "No decrypted IPA was produced. Check the log above — if you see 'appinst not found', open Sign in and run bootstrap on the device.";
    }

    /// <summary>Path + size + mtime fingerprint of every *.ipa in the output folder before a run.</summary>
    public sealed record IpaSnapshot(string Path, long Length, DateTime LastWriteUtc);

    public static List<IpaSnapshot> SnapshotIpas(string dir) =>
        !Directory.Exists(dir)
            ? []
            : Directory.EnumerateFiles(dir, "*.ipa")
                .Select(f =>
                {
                    var fi = new FileInfo(f);
                    return new IpaSnapshot(fi.FullName, fi.Length, fi.LastWriteTimeUtc);
                })
                .ToList();

    /// <summary>
    /// Claim the IPA this decrypt run actually wrote. Only files that are <em>new or rewritten</em>
    /// since <paramref name="before"/> count — never an untouched older IPA already in the folder.
    /// Prefers <c>*.decrypted.ipa</c> (ipadecrypt's real output), then tidies the name to
    /// <c>&lt;bundleId&gt;_&lt;version&gt;.ipa</c>.
    /// </summary>
    public static FileInfo? ClaimNewIpa(string dir, IReadOnlyList<IpaSnapshot> before)
    {
        if (!Directory.Exists(dir))
        {
            return null;
        }

        var beforeMap = new Dictionary<string, IpaSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in before)
        {
            beforeMap[s.Path] = s;
        }

        var changed = new List<FileInfo>();
        foreach (var path in Directory.EnumerateFiles(dir, "*.ipa"))
        {
            var fi = new FileInfo(path);
            if (!beforeMap.TryGetValue(fi.FullName, out var prev)
                || prev.Length != fi.Length
                || prev.LastWriteUtc != fi.LastWriteTimeUtc)
            {
                changed.Add(fi);
            }
        }

        if (changed.Count == 0)
        {
            return null;
        }

        // Prefer ipadecrypt's authentic output name over any other new *.ipa.
        var produced = changed
            .Where(fi => fi.Name.EndsWith(".decrypted.ipa", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(fi => fi.LastWriteTimeUtc)
            .FirstOrDefault()
            ?? changed.OrderByDescending(fi => fi.LastWriteTimeUtc).First();

        string tidy = TidyDecryptedName(produced.Name);
        if (!string.Equals(tidy, produced.Name, StringComparison.Ordinal))
        {
            string clean = Path.Combine(dir, tidy);
            try
            {
                if (File.Exists(clean))
                {
                    File.Delete(clean);
                }
                File.Move(produced.FullName, clean);
                return new FileInfo(clean);
            }
            catch (IOException)
            {
                return produced; // keep ipadecrypt's name if the rename fails
            }
        }
        return produced;
    }

    /// <summary>ipadecrypt writes <c>&lt;bundleId&gt;_&lt;version&gt;.decrypted.ipa</c>; tidy that to
    /// <c>&lt;bundleId&gt;_&lt;version&gt;.ipa</c> so the Library shows a clean name.</summary>
    public static string TidyDecryptedName(string fileName)
    {
        const string suffix = ".decrypted.ipa";
        return fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? fileName[..^suffix.Length] + ".ipa"
            : fileName;
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

}
