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
        Console.Error.WriteLine("usage: decrypta decrypt <bundle-id|id|url|path.ipa> [--use-installed] [--udid X] [-o out.ipa]");
        return 1;
    }
    var target = args[1];
    var engine = new DecryptaEngine();
    var settings = Settings.Load();

    var device = engine.Devices.Select(ArgValue(args, "--udid"))
                 ?? throw new DecryptaException("no matching device connected");
    Console.WriteLine($"device: {device.Summary}");

    var flags = new List<string>();
    flags.Add(HasFlag(args, "--use-installed") ? "--use-installed" : "--from-appstore");
    if (settings.VerboseLog)
    {
        flags.Add("--verbose");
    }

    var output = ArgValue(args, "-o")
                 ?? (DecryptaEngine.IsLocalIpa(target) ? null
                     : DecryptaEngine.DefaultOutputPath(settings.OutputDirectory, target));

    var job = engine.StartDecrypt(device, target, output, flags, s => Console.Write(s));
    int rc = await job.Completion;
    Console.WriteLine($"\n[exit {rc}]");
    return rc;
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
          decrypta-cli decrypt <bundle-id|id|url|path.ipa> [--use-installed] [--udid <udid>] [-o <out.ipa>]

        Sign-in (Apple ID + 2FA) is done in the Decrypta desktop app.
        """);
}
