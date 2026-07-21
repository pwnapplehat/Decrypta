using System.Text;

namespace Decrypta.Core.Tools;

/// <summary>A signed-in (or being-signed-in) App Store account exposed to the UI.</summary>
public sealed record AccountView(string Email, string Slug, bool IsActive, bool IsConfigured);

/// <summary>
/// Manages multiple App Store accounts. Each Apple ID gets its own ipadecrypt root
/// (<c>accounts\&lt;slug&gt;</c>) so credentials, cookies and tokens are fully isolated and
/// switching accounts is just a matter of which root we drive. Backed by the shared
/// <see cref="Settings"/> (account list + active slug are persisted there).
/// </summary>
public sealed class AccountService
{
    private readonly Settings _settings;

    public AccountService(Settings settings)
    {
        _settings = settings;
        MigrateLegacyIfNeeded();
        NormalizeActive();
    }

    public IReadOnlyList<AccountView> Accounts()
    {
        return _settings.Accounts
            .Select(a => new AccountView(a.Email, a.Slug, a.Slug == _settings.ActiveAccountSlug,
                new Ipadecrypt(rootDir: AppPaths.AccountRoot(a.Slug)).Config.IsAppleConfigured()))
            .ToList();
    }

    public string? ActiveSlug => _settings.ActiveAccountSlug;

    public string? ActiveEmail =>
        _settings.Accounts.FirstOrDefault(a => a.Slug == _settings.ActiveAccountSlug)?.Email;

    public bool HasActiveConfigured => Active()?.Config.IsAppleConfigured() ?? false;

    /// <summary>ipadecrypt for the active account, or null if there is none.</summary>
    public Ipadecrypt? Active()
    {
        var slug = _settings.ActiveAccountSlug;
        return slug is null ? null : new Ipadecrypt(rootDir: AppPaths.AccountRoot(slug));
    }

    /// <summary>Ensure an account exists for this email, make it active, and return its
    /// ipadecrypt handle (used right before sign-in bootstrap).</summary>
    public Ipadecrypt EnsureAndActivate(string email)
    {
        string slug = SlugFor(email);
        if (!_settings.Accounts.Any(a => a.Slug == slug))
        {
            _settings.Accounts.Add(new AccountEntry { Email = email, Slug = slug });
        }
        else
        {
            // Keep the display email fresh (case/edits).
            _settings.Accounts.First(a => a.Slug == slug).Email = email;
        }
        Directory.CreateDirectory(AppPaths.AccountRoot(slug));
        _settings.ActiveAccountSlug = slug;
        _settings.Save();
        return new Ipadecrypt(rootDir: AppPaths.AccountRoot(slug));
    }

    public void SetActive(string slug)
    {
        if (_settings.Accounts.Any(a => a.Slug == slug))
        {
            _settings.ActiveAccountSlug = slug;
            _settings.Save();
        }
    }

    /// <summary>Remove an account and delete its isolated root (credentials/cookies).</summary>
    public void Remove(string slug)
    {
        _settings.Accounts.RemoveAll(a => a.Slug == slug);
        try
        {
            var root = AppPaths.AccountRoot(slug);
            if (Directory.Exists(root))
            {
                // Drop the cache junction first so we never follow it into the output folder.
                var cache = Path.Combine(root, "cache");
                if (Directory.Exists(cache) && new DirectoryInfo(cache).Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    Directory.Delete(cache);
                }
                Directory.Delete(root, recursive: true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        if (_settings.ActiveAccountSlug == slug)
        {
            _settings.ActiveAccountSlug = _settings.Accounts.FirstOrDefault()?.Slug;
        }
        _settings.Save();
    }

    public IEnumerable<string> AllRoots() =>
        _settings.Accounts.Select(a => AppPaths.AccountRoot(a.Slug));

    // ---- internals ----

    private void MigrateLegacyIfNeeded()
    {
        // Pre-multi-account builds stored one account at StateDir\ipadecrypt. If it has an
        // Apple ID and we have no accounts yet, adopt it as the first account.
        if (_settings.Accounts.Count > 0)
        {
            return;
        }
        var legacy = AppPaths.LegacyIpadecryptRoot;
        var legacyCfg = new IpadecryptConfig(legacy);
        var email = legacyCfg.AppleEmail();
        if (string.IsNullOrEmpty(email))
        {
            return;
        }
        string slug = SlugFor(email);
        string dest = AppPaths.AccountRoot(slug);
        try
        {
            Directory.CreateDirectory(AppPaths.AccountsDir);
            if (!Directory.Exists(dest))
            {
                Directory.Move(legacy, dest);
            }
            _settings.Accounts.Add(new AccountEntry { Email = email, Slug = slug });
            _settings.ActiveAccountSlug = slug;
            _settings.Save();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void NormalizeActive()
    {
        if (_settings.ActiveAccountSlug is not null &&
            !_settings.Accounts.Any(a => a.Slug == _settings.ActiveAccountSlug))
        {
            _settings.ActiveAccountSlug = _settings.Accounts.FirstOrDefault()?.Slug;
            _settings.Save();
        }
        else if (_settings.ActiveAccountSlug is null && _settings.Accounts.Count > 0)
        {
            _settings.ActiveAccountSlug = _settings.Accounts[0].Slug;
            _settings.Save();
        }
    }

    /// <summary>Deterministic, filesystem-safe folder name for an email.</summary>
    public static string SlugFor(string email)
    {
        var sb = new StringBuilder(email.Length);
        foreach (char c in email.Trim().ToLowerInvariant())
        {
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        }
        var baseSlug = sb.ToString().Trim('_');
        if (baseSlug.Length == 0)
        {
            baseSlug = "account";
        }
        // Short hash suffix so different emails that normalize the same never collide.
        int h = email.Trim().ToLowerInvariant().GetHashCode() & 0xFFFF;
        return $"{baseSlug}_{h:x4}";
    }
}
