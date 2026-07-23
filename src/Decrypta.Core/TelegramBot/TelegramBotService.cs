using System.Collections.Concurrent;
using System.Security.Cryptography;
using Decrypta.Core.AppStore;
using Decrypta.Core.Tools;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

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
    private string _largeFileMode = "link";
    private readonly BotApiServerManager _apiServer = new();
    private readonly FileShareService _fileShare = new();
    private readonly ConcurrentDictionary<long, ChatSession> _sessions = new();

    // Persistent bottom-menu labels (a reply keyboard sends the label as a normal message).
    private const string MenuDecrypt = "🔓 Decrypt";
    private const string MenuVersions = "🕘 Pick version";
    private const string MenuStatus = "📱 Status";
    private const string MenuDevices = "🔌 Devices";
    private const string MenuLibrary = "📚 Library";

    private sealed class ChatSession
    {
        public bool AwaitingApp;
        public bool AwaitForVersions;
        public Draft? Draft;
    }

    private sealed class Draft
    {
        public string Target = "";
        public bool FromAppStore = true;
        public bool SkipAppex;
        public string? ExternalVersionId;
        public string? VersionLabel;
        public DecryptaEngine.VersionList? Versions;
        public readonly List<AppVersion> Resolved = [];
        public int CardMessageId;
    }

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

        _largeFileMode = (settings.TelegramLargeFileMode ?? "link").Trim().ToLowerInvariant();
        var cts = new CancellationTokenSource();
        try
        {
            // Decide the API endpoint: an explicit advanced URL, else an auto-managed local server
            // (mode "server"), else Telegram's cloud API.
            string? baseUrl = settings.TelegramApiBaseUrl.Trim();
            baseUrl = baseUrl.Length > 0 ? baseUrl : null;
            if (baseUrl is null && _largeFileMode == "server")
            {
                try
                {
                    baseUrl = await _apiServer.StartAsync(settings.TelegramApiId, settings.TelegramApiHash, Log, cts.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"Telegram: local server unavailable ({ex.Message}); falling back to cloud + link.");
                    _largeFileMode = "link";
                }
            }

            TelegramBotClient bot;
            if (baseUrl is not null)
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
            bot.OnUpdate += OnUpdate;
            bot.OnError += OnError;

            _bot = bot;
            _cts = cts;
            Running = true;
            string via = baseUrl is not null
                ? " via local Bot API server (~2 GB in-chat uploads)"
                : _largeFileMode == "link"
                    ? " (cloud API; big files sent as a private download link)"
                    : " (cloud API, 50 MB upload cap)";
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
        _apiServer.Stop();
        _fileShare.Dispose();
    }

    private Task OnError(Exception ex, Telegram.Bot.Polling.HandleErrorSource source)
    {
        Log?.Invoke($"Telegram: {ex.Message}");
        return Task.CompletedTask;
    }

    private async Task OnUpdate(Update update)
    {
        try
        {
            if (update.Message is { Text: { } text } m)
            {
                await OnText(m.Chat.Id, text.Trim());
            }
            else if (update.CallbackQuery is { } cq)
            {
                await OnCallback(cq);
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Telegram: {ex.Message}");
        }
    }

    private async Task OnText(long chatId, string text)
    {
        var (cmd, arg) = SplitCommand(text);

        // Always-available commands.
        switch (cmd)
        {
            case "/start" or "/menu":
                if (IsAllowed(chatId)) { await ShowMenu(chatId, MenuGreeting); }
                else { await Send(chatId, WelcomeText(chatId)); }
                return;
            case "/pair": await HandlePair(chatId, arg); return;
            case "/help": await Send(chatId, HelpText()); return;
        }

        if (!IsAllowed(chatId))
        {
            await Send(chatId, "This bot isn't paired with you yet.\nOpen Decrypta on your PC → Telegram tab, copy the pair code, and send:  /pair <code>");
            return;
        }

        // Bottom-menu taps (sent as plain text by the reply keyboard).
        switch (text)
        {
            case MenuDecrypt: Log?.Invoke("bot: menu Decrypt"); await StartAppPrompt(chatId, forVersions: false); return;
            case MenuVersions: Log?.Invoke("bot: menu Pick version"); await StartAppPrompt(chatId, forVersions: true); return;
            case MenuStatus: Log?.Invoke("bot: menu Status"); await HandleStatus(chatId); return;
            case MenuDevices: Log?.Invoke("bot: menu Devices"); await HandleDevices(chatId); return;
            case MenuLibrary: Log?.Invoke("bot: menu Library"); await HandleLibrary(chatId); return;
        }

        // Power-user slash commands still work.
        switch (cmd)
        {
            case "/status": await HandleStatus(chatId); return;
            case "/devices": await HandleDevices(chatId); return;
            case "/library": await HandleLibrary(chatId); return;
            case "/cancel": await HandleCancel(chatId); return;
            case "/decrypt" when arg.Length > 0: await BeginDraft(chatId, FirstToken(arg), startAtVersions: false); return;
            case "/versions" when arg.Length > 0: await BeginDraft(chatId, FirstToken(arg), startAtVersions: true); return;
            case "/decrypt": await StartAppPrompt(chatId, forVersions: false); return;
            case "/versions": await StartAppPrompt(chatId, forVersions: true); return;
        }

        // Otherwise: if we're waiting for an app identifier, this text is it.
        var sess = _sessions.GetOrAdd(chatId, _ => new ChatSession());
        if (sess.AwaitingApp && !text.StartsWith('/'))
        {
            sess.AwaitingApp = false;
            Log?.Invoke($"bot: app = {text.Trim()}");
            await BeginDraft(chatId, text.Trim(), startAtVersions: sess.AwaitForVersions);
            return;
        }

        await ShowMenu(chatId, "Tap an option below 👇");
    }

    private async Task StartAppPrompt(long chatId, bool forVersions)
    {
        var sess = _sessions.GetOrAdd(chatId, _ => new ChatSession());
        sess.AwaitingApp = true;
        sess.AwaitForVersions = forVersions;
        await Send(chatId, "Send the app — a bundle id (e.g. com.burbn.instagram), an App Store numeric id, or an App Store link.");
    }

    // Build the interactive "decrypt card" for an app and (optionally) jump straight to the version picker.
    private async Task BeginDraft(long chatId, string target, bool startAtVersions)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            await Send(chatId, "Send a bundle id, App Store id, or link.");
            return;
        }
        var sess = _sessions.GetOrAdd(chatId, _ => new ChatSession());
        var d = new Draft { Target = target.Trim(), FromAppStore = true };
        sess.Draft = d;
        var msg = await _bot!.SendMessage(chatId, CardText(d), replyMarkup: CardKeyboard(d));
        d.CardMessageId = msg.MessageId;
        if (startAtVersions)
        {
            await ShowVersionButtons(chatId, d.CardMessageId, d, loadMore: false);
        }
    }

    private async Task OnCallback(CallbackQuery cq)
    {
        long chatId = cq.Message?.Chat.Id ?? cq.From.Id;
        int msgId = cq.Message?.MessageId ?? 0;
        string data = cq.Data ?? "";
        try { await _bot!.AnswerCallbackQuery(cq.Id); } catch (Exception) { /* expired */ }

        if (!IsAllowed(chatId))
        {
            return;
        }
        Log?.Invoke($"bot tap: {data}");
        if (data == "menu")
        {
            await ShowMenu(chatId, MenuGreeting);
            return;
        }

        var sess = _sessions.GetOrAdd(chatId, _ => new ChatSession());
        var d = sess.Draft;
        if (d is null)
        {
            await ShowMenu(chatId, "That card expired — tap an option:");
            return;
        }

        switch (data)
        {
            case "src:as": d.FromAppStore = true; await UpdateCard(chatId, msgId, d); break;
            case "src:inst": d.FromAppStore = false; d.ExternalVersionId = null; d.VersionLabel = null; await UpdateCard(chatId, msgId, d); break;
            case "appex": d.SkipAppex = !d.SkipAppex; await UpdateCard(chatId, msgId, d); break;
            case "pickver": await ShowVersionButtons(chatId, msgId, d, loadMore: false); break;
            case "vermore": await ShowVersionButtons(chatId, msgId, d, loadMore: true); break;
            case "verlatest": d.ExternalVersionId = null; d.VersionLabel = "latest"; await UpdateCard(chatId, msgId, d); break;
            case "card": await UpdateCard(chatId, msgId, d); break;
            case "cancel": sess.Draft = null; await TryEdit(chatId, msgId, "Cancelled."); await ShowMenu(chatId, MenuGreeting); break;
            case "go": await RunDraft(chatId, msgId, d); break;
            default:
                if (data.StartsWith("ver:") && int.TryParse(data[4..], out int idx) && idx >= 0 && idx < d.Resolved.Count)
                {
                    var v = d.Resolved[idx];
                    d.ExternalVersionId = v.ExternalId;
                    d.VersionLabel = v.DisplayVersion is { Length: > 0 } dv ? dv : v.ExternalId;
                    d.FromAppStore = true;
                    await UpdateCard(chatId, msgId, d);
                }
                break;
        }
    }

    private async Task ShowVersionButtons(long chatId, int msgId, Draft d, bool loadMore)
    {
        var engine = NewEngine();
        if (d.Versions is null)
        {
            await TryEdit(chatId, msgId, $"Loading versions for {d.Target}…");
            var listed = await engine.LoadVersionListAsync(d.Target, _ => { });
            if (listed.Error is not null || listed.List is null)
            {
                await SafeEdit(chatId, msgId, $"Couldn't list versions: {listed.Error ?? "none found"}", CardKeyboard(d));
                return;
            }
            d.Versions = listed.List;
        }

        int target = loadMore ? d.Resolved.Count + 8 : Math.Max(d.Resolved.Count, 8);
        target = Math.Min(target, d.Versions.VersionIds.Count);
        if (target > d.Resolved.Count)
        {
            var slice = d.Versions.VersionIds.Skip(d.Resolved.Count).Take(target - d.Resolved.Count).ToList();
            var resolved = await engine.ResolveVersionsAsync(d.Versions.AdamId, slice, d.Versions.VersionIds.FirstOrDefault());
            d.Resolved.AddRange(resolved);
        }

        var rows = new List<InlineKeyboardButton[]>();
        for (int i = 0; i < d.Resolved.Count; i++)
        {
            rows.Add([InlineKeyboardButton.WithCallbackData(d.Resolved[i].Label, $"ver:{i}")]);
        }
        var nav = new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("⭐ Latest", "verlatest") };
        if (d.Resolved.Count < d.Versions.VersionIds.Count)
        {
            nav.Add(InlineKeyboardButton.WithCallbackData("⬇️ Load more", "vermore"));
        }
        nav.Add(InlineKeyboardButton.WithCallbackData("◀ Back", "card"));
        rows.Add([.. nav]);

        await SafeEdit(chatId, msgId,
            $"Pick a version of {d.Target}  ({d.Resolved.Count}/{d.Versions.VersionIds.Count} shown):",
            new InlineKeyboardMarkup(rows));
    }

    private async Task RunDraft(long chatId, int msgId, Draft d)
    {
        var req = new DecryptaEngine.DecryptRequest(
            Target: d.Target,
            FromAppStore: d.FromAppStore,
            SkipAppex: d.SkipAppex,
            Verbose: false,
            ExternalVersionId: d.ExternalVersionId);
        try
        {
            await RunDecrypt(chatId, msgId, req, d.Target);
        }
        finally
        {
            _sessions.GetOrAdd(chatId, _ => new ChatSession()).Draft = null;
            await ShowMenu(chatId, "Done — anything else?");
        }
    }

    private string CardText(Draft d)
    {
        string version = d.FromAppStore ? $"\n• Version: {d.VersionLabel ?? "latest"}" : "";
        return $"🔓 Decrypt\n• App: {d.Target}\n• Source: {(d.FromAppStore ? "App Store" : "installed build")}{version}\n• Skip app extensions: {(d.SkipAppex ? "yes" : "no")}\n\nAdjust with the buttons, then tap Decrypt.";
    }

    private static InlineKeyboardMarkup CardKeyboard(Draft d)
    {
        var rows = new List<InlineKeyboardButton[]>();
        rows.Add(new[]
        {
            InlineKeyboardButton.WithCallbackData((d.FromAppStore ? "● " : "○ ") + "App Store", "src:as"),
            InlineKeyboardButton.WithCallbackData((!d.FromAppStore ? "● " : "○ ") + "Installed", "src:inst"),
        });
        if (d.FromAppStore)
        {
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData($"🕘 Version: {d.VersionLabel ?? "latest"}", "pickver") });
        }
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData($"🧩 Skip app extensions: {(d.SkipAppex ? "on" : "off")}", "appex") });
        rows.Add(new[]
        {
            InlineKeyboardButton.WithCallbackData("▶️ Decrypt", "go"),
            InlineKeyboardButton.WithCallbackData("✖️ Cancel", "cancel"),
        });
        return new InlineKeyboardMarkup(rows);
    }

    private async Task UpdateCard(long chatId, int msgId, Draft d)
        => await SafeEdit(chatId, msgId, CardText(d), CardKeyboard(d));

    // ---- shared decrypt runner (used by the card flow and the /decrypt command) ----

    private async Task RunDecrypt(long chatId, int statusMsgId, DecryptaEngine.DecryptRequest req, string label)
    {
        if (!await _opGate.WaitAsync(0))
        {
            await Send(chatId, "A decrypt is already running. Send /cancel to stop it.");
            return;
        }
        try
        {
            var engine = NewEngine();
            if (!engine.Devices.ServiceReachable())
            {
                await TryEdit(chatId, statusMsgId, "Apple Mobile Device service isn't reachable on the PC.");
                return;
            }
            var device = engine.Devices.Select(Settings.Load().LastUdid) ?? engine.Devices.ListDevices().FirstOrDefault();
            if (device is null)
            {
                await TryEdit(chatId, statusMsgId, "No device connected. Plug in / network-pair your jailbroken device and try again.");
                return;
            }
            if (!DecryptaEngine.IsLocalIpa(req.Target) && !engine.IsSignedIn)
            {
                await TryEdit(chatId, statusMsgId, "Not signed in. Sign in with your Apple ID in the Decrypta app on the PC first.");
                return;
            }

            Log?.Invoke($"bot: decrypt {label} ({(req.FromAppStore ? "App Store" : "installed")}{(req.ExternalVersionId is { Length: > 0 } v ? $" v{v}" : "")})");
            await TryEdit(chatId, statusMsgId, $"⏳ Decrypting {label} on {device.Summary}…");
            var lastEdit = DateTime.MinValue;
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
                        _ = TryEdit(chatId, statusMsgId, $"⏳ Decrypting {label}…\n{Truncate(l, 180)}");
                    }
                },
                onJob: j => _activeJob = j);

            if (result.Ok)
            {
                await TryEdit(chatId, statusMsgId, $"✅ Decrypted {result.FileName} ({HumanSize(result.Bytes)})");
                await SendResultFile(chatId, result);
            }
            else
            {
                await TryEdit(chatId, statusMsgId, $"❌ Decrypt failed (exit {result.ExitCode}). Check the PC log for details.");
            }
        }
        finally
        {
            _activeJob = null;
            _opGate.Release();
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
        await ShowMenu(chatId, "✅ Paired! Use the buttons below to control Decrypta from here.");
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

        // Over the in-chat limit: offer a private download link (mode "link"), else point to the PC.
        if (_largeFileMode == "link")
        {
            try
            {
                await Send(chatId, $"{result.FileName} is {HumanSize(result.Bytes)} — over the {HumanSize(_uploadLimit)} in-chat limit. Preparing a private download link…");
                var ct = _cts?.Token ?? CancellationToken.None;
                string? url = await _fileShare.ShareAsync(result.OutputPath, TimeSpan.FromMinutes(30), Log, ct);
                if (url is not null)
                {
                    await Send(chatId,
                        $"📦 {result.FileName} ({HumanSize(result.Bytes)})\n{url}\n\nPrivate link — expires in 30 minutes. Tap to download.");
                    return;
                }
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Telegram: share link failed — {ex.Message}");
            }
        }

        string hint = _largeFileMode != "server"
            ? "\n\nTip: for in-chat delivery of big IPAs, switch large-file mode to \"local server\" in Decrypta's Telegram tab (up to ~2 GB)."
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

    private const string MenuGreeting = "What would you like to do?";

    private static readonly ReplyKeyboardMarkup Menu = new(new[]
    {
        new KeyboardButton[] { MenuDecrypt, MenuVersions },
        new KeyboardButton[] { MenuStatus, MenuDevices, MenuLibrary },
    })
    { ResizeKeyboard = true, IsPersistent = true };

    private async Task ShowMenu(long chatId, string text)
    {
        if (_bot is not null)
        {
            await _bot.SendMessage(chatId, text, replyMarkup: Menu);
        }
    }

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

    private async Task SafeEdit(long chatId, int messageId, string text, InlineKeyboardMarkup markup)
    {
        try
        {
            if (_bot is not null)
            {
                await _bot.EditMessageText(chatId, messageId, text, replyMarkup: markup);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // unchanged text / rate limit / expired message — ignore
        }
    }

    private static string FirstToken(string s)
    {
        var t = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return t.Length > 0 ? t[0] : "";
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
            ? "Decrypta bot is ready. Send /menu for the buttons."
            : "Welcome to Decrypta.\n\nTo control decryption from here, pair once:\n1. Open Decrypta on your PC → Telegram tab.\n2. Copy the pair code.\n3. Send:  /pair <code>";

    private static string HelpText() =>
        "Decrypta — control it from the buttons below (no typing needed):\n\n" +
        "🔓 Decrypt — pick an app, then choose source / version / options and tap Decrypt.\n" +
        "🕘 Pick version — browse an app's App Store versions and decrypt one.\n" +
        "📱 Status — account, devices, output folder.\n" +
        "🔌 Devices — connected devices.\n" +
        "📚 Library — recent decrypted IPAs.\n\n" +
        "The only thing you type is the app itself (a bundle id, App Store id, or link).\n" +
        "Shortcuts still work: /decrypt <app>, /versions <app>, /cancel, /menu.";

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
