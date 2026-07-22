using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Decrypta.Core.AppStore;

namespace Decrypta.Core.Tools;

/// <summary>
/// Wraps the bundled majd/ipatool binary. Decrypta uses it only for App Store version
/// discovery (list-versions / get-version-metadata) so the user can pick a specific version;
/// the actual versioned download is still done by ipadecrypt via --external-version-id.
///
/// ipatool keeps its own keychain (separate from ipadecrypt), so it needs a one-time sign-in.
/// We pin a fixed keychain passphrase so the token is stored non-interactively and reused.
/// </summary>
public sealed class Ipatool
{
    private readonly string _exe;

    public Ipatool(string? exe = null) => _exe = exe ?? AppPaths.IpatoolExe;

    public bool Exists => File.Exists(_exe);

    private List<string> Base() =>
        ["--keychain-passphrase", AppPaths.IpatoolKeychainPassphrase, "--format", "json", "--non-interactive"];

    /// <summary>Email ipatool is currently logged in as, or null if not authenticated.</summary>
    public async Task<string?> AuthInfoEmailAsync(CancellationToken ct = default)
    {
        var args = Base();
        args.Add("auth");
        args.Add("info");
        var (code, stdout, _) = await RunAsync(args, ct).ConfigureAwait(false);
        if (code != 0)
        {
            return null;
        }
        return ReadStringField(stdout, "email") ?? ReadNestedField(stdout, "account", "email");
    }

    /// <summary>Sign ipatool in. Pass authCode on the retry when 2FA is required.</summary>
    public async Task<IpatoolAuthResult> AuthLoginAsync(
        string email, string password, string? authCode = null, CancellationToken ct = default)
    {
        var args = Base();
        args.Add("auth");
        args.Add("login");
        args.Add("-e");
        args.Add(email);
        args.Add("-p");
        args.Add(password);
        if (!string.IsNullOrWhiteSpace(authCode))
        {
            args.Add("--auth-code");
            args.Add(authCode.Trim());
        }
        var (code, stdout, stderr) = await RunAsync(args, ct).ConfigureAwait(false);
        string combined = (stdout + "\n" + stderr);
        if (code == 0 && (ReadBoolField(stdout, "success") ?? true))
        {
            return new IpatoolAuthResult(true, false, null);
        }
        bool needs2Fa = combined.Contains("2FA", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("auth-code", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("two-factor", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("code is required", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("verification code", StringComparison.OrdinalIgnoreCase);
        return new IpatoolAuthResult(false, needs2Fa, ExtractError(combined));
    }

    /// <summary>All external version identifiers for an app (oldest→newest as Apple returns them).</summary>
    public async Task<IReadOnlyList<string>> ListVersionIdsAsync(string bundleId, CancellationToken ct = default)
    {
        var args = Base();
        args.Add("list-versions");
        args.Add("-b");
        args.Add(bundleId);
        var (code, stdout, stderr) = await RunAsync(args, ct).ConfigureAwait(false);
        if (code != 0)
        {
            throw new IpatoolException(ExtractError(stdout + "\n" + stderr) ?? "list-versions failed");
        }
        return ParseVersionIds(stdout);
    }

    /// <summary>Resolve one external version id to its display version + release date.</summary>
    public async Task<AppVersion?> GetVersionMetadataAsync(string bundleId, string externalId, CancellationToken ct = default)
    {
        var args = Base();
        args.Add("get-version-metadata");
        args.Add("-b");
        args.Add(bundleId);
        args.Add("--external-version-id");
        args.Add(externalId);
        var (code, stdout, _) = await RunAsync(args, ct).ConfigureAwait(false);
        if (code != 0)
        {
            return null;
        }
        string? display = ReadStringField(stdout, "displayVersion");
        DateTime? date = null;
        if (ReadStringField(stdout, "releaseDate") is { Length: > 0 } ds &&
            DateTime.TryParse(ds, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d))
        {
            date = d;
        }
        return new AppVersion(externalId, display, date);
    }

    public string Version()
    {
        try
        {
            var psi = new ProcessStartInfo(_exe, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            var outText = p.StandardOutput.ReadToEnd();
            var errText = p.StandardError.ReadToEnd();
            p.WaitForExit(5000);
            return (outText + errText).Trim().Split('\n').FirstOrDefault()?.Trim() ?? "unknown";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "unknown";
        }
    }

    // ---- parsing (ipatool --format json emits one JSON object per line to stdout) ----

    public static IReadOnlyList<string> ParseVersionIds(string stdout)
    {
        foreach (var el in JsonObjects(stdout))
        {
            if (el.TryGetProperty("externalVersionIdentifiers", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>(arr.GetArrayLength());
                foreach (var item in arr.EnumerateArray())
                {
                    list.Add(item.ValueKind == JsonValueKind.Number ? item.GetRawText() : item.GetString() ?? "");
                }
                return list.Where(s => s.Length > 0).ToList();
            }
        }
        return [];
    }

    private static string? ReadStringField(string stdout, string field)
    {
        foreach (var el in JsonObjects(stdout))
        {
            if (el.TryGetProperty(field, out var v))
            {
                return v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();
            }
        }
        return null;
    }

    private static string? ReadNestedField(string stdout, string obj, string field)
    {
        foreach (var el in JsonObjects(stdout))
        {
            if (el.TryGetProperty(obj, out var o) && o.ValueKind == JsonValueKind.Object &&
                o.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String)
            {
                return v.GetString();
            }
        }
        return null;
    }

    private static bool? ReadBoolField(string stdout, string field)
    {
        foreach (var el in JsonObjects(stdout))
        {
            if (el.TryGetProperty(field, out var v) &&
                (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False))
            {
                return v.GetBoolean();
            }
        }
        return null;
    }

    private static string? ExtractError(string text)
    {
        foreach (var el in JsonObjects(text))
        {
            if (el.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
            {
                return e.GetString();
            }
            if (el.TryGetProperty("level", out var lvl) && lvl.GetString() == "error" &&
                el.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
            {
                return m.GetString();
            }
        }
        var firstLine = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        return string.IsNullOrEmpty(firstLine) ? null : firstLine;
    }

    /// <summary>ipatool prints structured logs as JSON lines; yield each parseable object.</summary>
    private static IEnumerable<JsonElement> JsonObjects(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] != '{')
            {
                continue;
            }
            JsonDocument? doc = null;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { }
            if (doc is not null)
            {
                yield return doc.RootElement.Clone();
                doc.Dispose();
            }
        }
    }

    private async Task<(int Code, string Stdout, string Stderr)> RunAsync(IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using var p = new Process { StartInfo = psi };
        p.Start();
        var so = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        var se = await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return (p.ExitCode, so, se);
    }
}

public sealed class IpatoolException : Exception
{
    public IpatoolException(string message) : base(message) { }
}
