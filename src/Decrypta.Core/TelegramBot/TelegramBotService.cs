using System.Security.Cryptography;
using Decrypta.Core.Devices;
using Decrypta.Core.Tools;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Decrypta.Core.TelegramBot;

/// <summary>
/// Optional Telegram bot that lets the user drive Decrypta from their phone: check status, list
/// versions, and run a decrypt — while the Windows app stays open with a device connected.
///
/// Security: only chat ids that have paired (via <c>/pair &lt;code&gt;</c>, where the code is shown
/// in the app) may run commands. The token and the allow-list live in the local settings file.
/// Each command builds a fresh <see cref="DecryptaEngine"/> from the current settings, so account,
/// output folder and device changes made in the GUI are always reflected.
/// </summary>
public sealed class TelegramBotService : IDisposable
{
    private const long CloudUploadLimit = 49L * 1024 * 1024;        // Telegram cloud Bot API: 50 MB cap
    private const long LocalServerUploadLimit = 1990L * 1024 * 1024; // self-hosted Bot API server: ~2 GB

    private readonly object _lock = new();
    private readonly HashSet<long> _allowed = [];
    private readonly SemaphoreSlim _opGate = new(1, 1);

    private TelegramBotClient? _bot;
    private CancellationTokenSource? _cts;
    private RunningJob? _activeJob;
    private long _uploadLimit = CloudUploadLimit;

    public bool Running { get; private set; }
    public string? BotUsername { get; private set; }
    public string PairCode { get; private set; } = "";

    /// <summary>Human-readable status/log lines for the app to display.</summary>
    public event Action<string>? Log;

    public async Task<bool> StartAsync()
    {
        Stop();

        var settings = Settings.Load();
        string token = settings.TelegramBotToken.Trim();
        if (string.IsNullOrEmpty(token))
        {
            Log?.Invoke("Telegram: no bot token set.");
            return false;
        }

        lock (_lock)
        {
            _allowed.Clear();
            foreach (var id in settings.TelegramAllowedChatIds)
            {
                _allowed.Add(id);
            }
        }
        PairCode = GeneratePairCode();

        string baseUrl = settings.TelegramApiBaseUrl.Trim();
        var cts = new CancellationTokenSource();
        try
        {
            TelegramBotClient bot;
            if (baseUrl.Length > 0)
            {
                bot = new TelegramBotClient(new TelegramBotClientOptions(token, baseUrl), cancellationToken: cts.Token);
                _uploadLimit = LocalServerUploadLimit;
            }
            else
            {
                bot = new TelegramBotClient(token, cancellationToken: cts.Token);
                _uploadLimit = CloudUploadLimit;
            }

            var me = await bot.GetMe().ConfigureAwait(false);
            BotUsername = me.Username;
            bot.OnMessage += OnMessage;
            bot.OnError += OnError;

            _bot = bot;
            _cts = cts;
            Running = true;
            string via = baseUrl.Length > 0 ? $" via local API ({baseUrl}, ~2 GB uploads)" : " (cloud API, 50 MB upload cap)";
            Log?.Invoke($"Telegram: @{BotUsername} online{via}. Pair from your phone with:  /pair {PairCode}");
            return true;
        }
        catch (Exception ex)
        {
            cts.Cancel();
            cts.Dispose();
            Log?.Invoke($"Telegram: failed to start — {ex.Message}");
            Running = false;
            return false;
        }
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // already gone
        }
        _cts?.Dispose();
        _cts = null;
        _bot = null;
        Running = false;
    }

    private Task OnError(Exception ex, Telegram.Bot.Polling.HandleErrorSource source)
    {
        Log?.Invoke($"Telegram: {ex.Message}");
        return Task.CompletedTask;
    }

    private async Task OnMessage(Message msg, UpdateType type)
    {
        if (msg.Text is not { } raw)
        {
            return;
        }
        long chatId = msg.Chat.Id;
        var (cmd, arg) = SplitCommand(raw.Trim());

        try
        {
            switch (cmd)
            {
                case "/start":
                    await Send(chatId, WelcomeText(chatId));
                    return;
                case "/pair":
                    await HandlePair(chatId, arg);
                    return;
                case "/help":
                    await Send(chatId, HelpText());
                    return;
            }

            if (!IsAllowed(chatId))
            {
                await Send(chatId, "This bot isn't paired with you yet.\nOpen Decrypta on your PC, copy the pair code, and send:  /pair <code>");
                return;
            }

            switch (cmd)
            {
                case "/status": await HandleStatus(chatId); break;
                case "/devices": await HandleDevices(chatId); break;
                case "/versions": await HandleVersions(chatId, arg); break;
                case "/decrypt": await HandleDecrypt(chatId, arg); break;
                case "/cancel": await HandleCancel(chatId); break;
                case "/library": await HandleLibrary(chatId); break;
                default: await Send(chatId, "Unknown command. Send /help for the list."); break;
            }
        }
        catch (Exception ex)
        {
            await Send(chatId, $"error: {ex.Message}");
        }
    }

    // ---- command handlers ----

    private async Task HandlePair(long chatId, string arg)
    {
        if (string.IsNullOrWhiteSpace(PairCode))
        {
            await Send(chatId, "Pairing isn't available right now.");
            return;
        }
        if (!string.Equals(arg.Trim(), PairCode, StringComparison.OrdinalIgnoreCase))
        {
            await Send(chatId, "Wrong pair code. Check the code shown in the Decrypta app and try:  /pair <code>");
            return;
        }

        // Persist without clobbering other settings the GUI may have changed.
        var s = Settings.Load();
        if (!s.TelegramAllowedChatIds.Contains(chatId))
        {
            s.TelegramAllowedChatIds.Add(chatId);
            s.Save();
        }
        lock (_lock)
        {
            _allowed.Add(chatId);
        }
        Log?.Invoke($"Telegram: paired chat {chatId}.");
        await Send(chatId, "✅ Paired! You can now use /decrypt, /versions, /status, /devices, /library. Send /help for details.");
    }

    private async Task HandleStatus(long chatId)
    {
        var engine = NewEngine();
        string account = engine.IsSignedIn ? $"signed in as {engine.SignedInEmail}" : "not signed in";
        int devices = 0;
        try
        {
            devices = engine.Devices.ServiceReachable() ? engine.Devices.QuickList().Count : 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // usbmux hiccup; report zero
        }
        await Send(chatId,
            $"Decrypta status\n• {account}\n• devices connected: {devices}\n• output: {Settings.Load().OutputDirectory}");
    }

    private async Task HandleDevices(long chatId)
    {
        var engine = NewEngine();
        if (!engine.Devices.ServiceReachable())
        {
            await Send(chatId, "Apple Mobile Device service isn't reachable. Install the Apple Devices app / iTunes on the PC.");
            return;
        }
        var devs = engine.Devices.ListDevices();
        if (devs.Count == 0)
        {
            await Send(chatId, "No devices connected.");
            return;
        }
        await Send(chatId, "Connected devices:\n" + string.Join("\n", devs.Select(d => "• " + d.Summary)));
    }

    private async Task HandleVersions(long chatId, string arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            await Send(chatId, "Usage: /versions <bundle-id | app-store-id | link>");
            return;
        }
        var engine = NewEngine();
        var listed = await engine.LoadVersionListAsync(arg.Trim(), _ => { });
        if (listed.Error is not null || listed.List is null)
        {
            await Send(chatId, listed.Error ?? "no versions found");
            return;
        }
        var list = listed.List;
        var page = list.VersionIds.Take(12).ToList();
        var resolved = await engine.ResolveVersionsAsync(list.AdamId, page, list.VersionIds.FirstOrDefault());
        var lines = resolved.Select(v => $"`{v.ExternalId}`  {v.Label}");
        await Send(chatId,
            $"Newest {resolved.Count} of {list.VersionIds.Count} versions:\n" + string.Join("\n", lines) +
            "\n\nDecrypt a specific one:  /decrypt <app> --version <id>");
    }

    private async Task HandleDecrypt(long chatId, string arg)
    {
        var parts = arg.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            await Send(chatId, "Usage: /decrypt <bundle-id | app-store-id | link> [--installed] [--skip-appex] [--version <id>]");
            return;
        }

        if (!await _opGate.WaitAsync(0))
        {
            await Send(chatId, "A decrypt is already running. Send /cancel to stop it.");
            return;
        }

        try
        {
            string target = parts[0];
            bool installed = parts.Contains("--installed") || parts.Contains("--use-installed");
            bool skipAppex = parts.Contains("--skip-appex");
            string? version = null;
            int vi = Array.FindIndex(parts, p => p is "--version" or "-v");
            if (vi >= 0 && vi + 1 < parts.Length)
            {
                version = parts[vi + 1];
            }

            var engine = NewEngine();
            if (!engine.Devices.ServiceReachable())
            {
                await Send(chatId, "Apple Mobile Device service isn't reachable on the PC.");
                return;
            }
            var device = engine.Devices.Select(Settings.Load().LastUdid) ?? engine.Devices.ListDevices().FirstOrDefault();
            if (device is null)
            {
                await Send(chatId, "No device connected. Plug in / network-pair your jailbroken device and try again.");
                return;
            }
            if (!DecryptaEngine.IsLocalIpa(target) && !engine.IsSignedIn)
            {
                await Send(chatId, "Not signed in. Sign in with your Apple ID in the Decrypta app on the PC first.");
                return;
            }

            var statusMsg = await Send(chatId, $"⏳ Decrypting {target} on {device.Summary}…");
            var lastEdit = DateTime.MinValue;

            var req = new DecryptaEngine.DecryptRequest(
                Target: target,
                FromAppStore: !installed,
                SkipAppex: skipAppex,
                Verbose: false,
                ExternalVersionId: version);

            var result = await engine.DecryptAsync(
                device, req,
                onOutput: line =>
                {
                    string l = line.Trim();
                    if (l.Length == 0)
                    {
                        return;
                    }
                    var now = DateTime.UtcNow;
                    if ((now - lastEdit).TotalSeconds >= 2.5)
                    {
                        lastEdit = now;
                        _ = TryEdit(chatId, statusMsg.MessageId, $"⏳ Decrypting {target}…\n{Truncate(l, 180)}");
                    }
                },
                onJob: j => _activeJob = j);

            if (result.Ok)
            {
                await TryEdit(chatId, statusMsg.MessageId, $"✅ Decrypted {result.FileName} ({HumanSize(result.Bytes)})");
                await SendResultFile(chatId, result);
            }
            else
            {
                await TryEdit(chatId, statusMsg.MessageId, $"❌ Decrypt failed (exit {result.ExitCode}). Check the PC log for details.");
            }
        }
        finally
        {
            _activeJob = null;
            _opGate.Release();
        }
    }

    private async Task SendResultFile(long chatId, DecryptaEngine.DecryptResult result)
    {
        if (_bot is null || result.OutputPath is null || !File.Exists(result.OutputPath))
        {
            return;
        }
        if (result.Bytes > 0 && result.Bytes <= _uploadLimit)
        {
            try
            {
                await Send(chatId, $"Uploading {result.FileName} ({HumanSize(result.Bytes)})…");
                await using var fs = File.OpenRead(result.OutputPath);
                await _bot.SendDocument(chatId, InputFile.FromStream(fs, result.FileName ?? "app.ipa"),
                    caption: $"{result.FileName} ({HumanSize(result.Bytes)})");
                return;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Telegram: upload failed — {ex.Message}");
            }
        }

        string hint = _uploadLimit <= CloudUploadLimit
            ? "\n\nTip: to send big IPAs over Telegram, run a local Bot API server and set its URL in Decrypta's Telegram tab (raises the limit to ~2 GB)."
            : "";
        await Send(chatId,
            $"✅ {result.FileName} ({HumanSize(result.Bytes)})\nSaved on the PC:\n{result.OutputPath}\n\n(Over this bot's {HumanSize(_uploadLimit)} upload limit, so not sent here.){hint}");
    }

    private async Task HandleCancel(long chatId)
    {
        var job = _activeJob;
        if (job is null)
        {
            await Send(chatId, "Nothing is running.");
            return;
        }
        job.Cancel();
        await Send(chatId, "Cancelling the current decrypt…");
    }

    private async Task HandleLibrary(long chatId)
    {
        string dir = Settings.Load().OutputDirectory;
        if (!Directory.Exists(dir))
        {
            await Send(chatId, "Library is empty.");
            return;
        }
        var files = new DirectoryInfo(dir).EnumerateFiles("*.ipa")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(10)
            .ToList();
        if (files.Count == 0)
        {
            await Send(chatId, "Library is empty.");
            return;
        }
        await Send(chatId, "Recent decrypted IPAs:\n" +
            string.Join("\n", files.Select(f => $"• {f.Name} ({HumanSize(f.Length)})")));
    }

    // ---- helpers ----

    private static DecryptaEngine NewEngine() => new(Settings.Load());

    private bool IsAllowed(long chatId)
    {
        lock (_lock)
        {
            return _allowed.Contains(chatId);
        }
    }

    private async Task<Message> Send(long chatId, string text)
        => await _bot!.SendMessage(chatId, text);

    private async Task TryEdit(long chatId, int messageId, string text)
    {
        try
        {
            if (_bot is not null)
            {
                await _bot.EditMessageText(chatId, messageId, text);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // edit can fail if the text is unchanged or we're rate-limited; ignore
        }
    }

    private static (string Cmd, string Arg) SplitCommand(string text)
    {
        if (text.Length == 0)
        {
            return ("", "");
        }
        int sp = text.IndexOf(' ');
        string first = sp < 0 ? text : text[..sp];
        string rest = sp < 0 ? "" : text[(sp + 1)..].Trim();
        // strip the "@BotName" suffix Telegram adds in groups
        int at = first.IndexOf('@');
        if (at >= 0)
        {
            first = first[..at];
        }
        return (first.ToLowerInvariant(), rest);
    }

    private static string GeneratePairCode()
        => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    private string WelcomeText(long chatId)
        => IsAllowed(chatId)
            ? "Decrypta bot is ready. Send /help for commands."
            : $"Welcome to Decrypta.\n\nTo control decryption from here, pair once:\n1. Open Decrypta on your PC → Telegram tab.\n2. Copy the pair code.\n3. Send:  /pair <code>";

    private static string HelpText() =>
        "Decrypta bot commands:\n" +
        "/status — account, devices, output folder\n" +
        "/devices — list connected devices\n" +
        "/versions <app> — list App Store versions\n" +
        "/decrypt <app> [--installed] [--skip-appex] [--version <id>] — decrypt an app\n" +
        "/cancel — stop the current decrypt\n" +
        "/library — recent decrypted IPAs\n" +
        "/pair <code> — pair this chat (code shown in the app)\n\n" +
        "<app> = bundle id, App Store numeric id, or App Store link.";

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static string HumanSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1)
        {
            v /= 1024;
            u++;
        }
        return $"{v:0.#} {units[u]}";
    }

    public void Dispose() => Stop();
}
