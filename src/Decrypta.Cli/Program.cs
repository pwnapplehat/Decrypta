using Decrypta.Core;
using Decrypta.Core.Diagnostics;
using Decrypta.Core.Devices;
using Decrypta.Core.Tools;

// Headless companion to the Decrypta GUI. Same engine, no window - handy for scripting and
// for verifying the device pipeline from a terminal.

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

try
{
    switch (args[0].ToLowerInvariant())
    {
        case "devices":
            return Devices();
        case "doctor":
            return DoctorCmd(ArgValue(args, "--udid"));
        case "decrypt":
            return await DecryptCmd(args);
        case "versions":
            return await VersionsCmd(args);
        case "-h" or "--help" or "help":
            PrintUsage();
            return 0;
        default:
            Console.Error.WriteLine($"unknown command: {args[0]}");
            PrintUsage();
            return 1;
    }
}
catch (DecryptaException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

static int Devices()
{
    var svc = new DeviceService();
    if (!svc.ServiceReachable())
    {
        Console.Error.WriteLine("usbmuxd not reachable - install the 'Apple Devices' app or iTunes.");
        return 1;
    }
    var devices = svc.ListDevices();
    if (devices.Count == 0)
    {
        Console.WriteLine("no devices connected");
        return 1;
    }
    foreach (var d in devices)
    {
        Console.WriteLine(d.Summary);
    }
    return 0;
}

static int DoctorCmd(string? udid)
{
    foreach (var c in new Doctor().Run(udid))
    {
        var tag = c.Status switch
        {
            CheckStatus.Ok => "[ OK ]",
            CheckStatus.Warn => "[WARN]",
            _ => "[FAIL]",
        };
        Console.WriteLine($"{tag} {c.Name,-14} {c.Detail}");
    }
    return 0;
}

static async Task<int> DecryptCmd(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("usage: decrypta-cli decrypt <bundle-id|id|url|path.ipa> [--use-installed] [--external-version-id <id>] [--udid X]");
        return 1;
    }
    var target = args[1];
    var settings = Settings.Load();
    var engine = new DecryptaEngine(settings);

    var device = engine.Devices.Select(ArgValue(args, "--udid"))
                 ?? throw new DecryptaException("no matching device connected");
    Console.WriteLine($"device: {device.Summary}");

    var extVersionId = ArgValue(args, "--external-version-id");
    // A pinned historical version can only come from the App Store, never the installed build.
    bool useInstalled = HasFlag(args, "--use-installed") && string.IsNullOrWhiteSpace(extVersionId);
    var req = new DecryptaEngine.DecryptRequest(
        Target: target,
        FromAppStore: !useInstalled,
        Verbose: settings.VerboseLog,
        ExternalVersionId: string.IsNullOrWhiteSpace(extVersionId) ? null : extVersionId);

    var result = await engine.DecryptAsync(device, req, s => Console.Write(s));
    if (result.Ok)
    {
        Console.WriteLine($"\n[saved] {result.OutputPath}");
    }
    Console.WriteLine($"[exit {result.ExitCode}]");
    return result.ExitCode;
}

static async Task<int> VersionsCmd(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("usage: decrypta-cli versions <bundle-id|id|url> [-n <count>]");
        return 1;
    }
    var target = args[1];
    var settings = Settings.Load();
    var engine = new DecryptaEngine(settings);
    int count = int.TryParse(ArgValue(args, "-n"), out var n) ? Math.Clamp(n, 1, 200) : 15;

    var listed = await engine.LoadVersionListAsync(target, s => Console.Error.Write(s));
    if (listed.Error is not null || listed.List is null)
    {
        Console.Error.WriteLine($"error: {listed.Error ?? "no versions"}");
        return 1;
    }

    var list = listed.List;
    var page = list.VersionIds.Take(count).ToList();
    Console.Error.Write($"resolving {page.Count} of {list.VersionIds.Count} versions…\n");
    var resolved = await engine.ResolveVersionsAsync(list.AdamId, page, list.VersionIds.FirstOrDefault());

    foreach (var v in resolved)
    {
        Console.WriteLine($"{v.ExternalId,-12} {v.Label}");
    }
    Console.Error.WriteLine(
        $"\nShowing {resolved.Count} of {list.VersionIds.Count}. Decrypt one with: " +
        $"decrypta-cli decrypt {target} --external-version-id <id>");
    return 0;
}

static string? ArgValue(string[] args, string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static bool HasFlag(string[] args, string name) => Array.IndexOf(args, name) >= 0;

static void PrintUsage()
{
    Console.WriteLine("""
        Decrypta CLI - App Store download + on-device FairPlay decrypt (Windows).

        Usage:
          decrypta-cli devices
          decrypta-cli doctor [--udid <udid>]
          decrypta-cli versions <bundle-id|id|url> [-n <count>]
          decrypta-cli decrypt <bundle-id|id|url|path.ipa> [--use-installed]
                       [--external-version-id <id>] [--udid <udid>]
          (output is saved to your configured folder as <bundleId>_<version>.ipa)

        Sign-in (Apple ID + 2FA) is done in the Decrypta desktop app.
        'versions' lists App Store builds so you can pass one to 'decrypt --external-version-id'.
        """);
}
