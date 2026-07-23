using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Decrypta.Core.Tools;
using Decrypta.Core.Tunnel;

namespace Decrypta.Core.TelegramBot;

/// <summary>
/// Zero-config large-file delivery: serves one file from a local HTTP listener on a dynamically
/// chosen free port, and exposes it through a Cloudflare "quick tunnel" (cloudflared, no account)
/// as a private, unguessable, auto-expiring <c>https://…trycloudflare.com/&lt;token&gt;/&lt;file&gt;</c>
/// link. Handles any size. Each share tears itself down after its TTL.
/// </summary>
public sealed partial class FileShareService : IDisposable
{
    private readonly List<Share> _shares = [];
    private readonly object _lock = new();

    /// <summary>Publish <paramref name="filePath"/> and return a public download URL (or null on failure).</summary>
    public async Task<string?> ShareAsync(
        string filePath, TimeSpan ttl, Action<string>? log = null, CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }
        string cloudflared = await HelperBinaries.EnsureCloudflaredAsync(log, ct).ConfigureAwait(false);

        int port = UsbTunnel.FindFreePort(8790, 80);
        string token = Guid.NewGuid().ToString("N");
        string fileName = Path.GetFileName(filePath);

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();

        var share = new Share(listener, token, filePath, fileName);
        _ = share.ServeLoopAsync();

        log?.Invoke($"sharing on a private tunnel (port {port})…");
        var proc = new Process
        {
            StartInfo = new ProcessStartInfo(cloudflared)
            {
                // --http-host-header makes cloudflared send "Host: localhost:<port>" to our origin,
                // so .NET HttpListener (which matches its prefix on the Host header) accepts it —
                // otherwise the forwarded trycloudflare host yields HTTP 400.
                ArgumentList =
                {
                    "tunnel", "--no-autoupdate",
                    "--url", $"http://localhost:{port}",
                    "--http-host-header", $"localhost:{port}",
                },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        var sb = new StringBuilder();
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (sb) { sb.AppendLine(e.Data); } } };
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (sb) { sb.AppendLine(e.Data); } } };
        proc.Start();
        proc.BeginErrorReadLine();
        proc.BeginOutputReadLine();
        share.Cloudflared = proc;

        string? baseUrl = null;
        for (int i = 0; i < 40 && baseUrl is null; i++)
        {
            await Task.Delay(750, ct).ConfigureAwait(false);
            string text;
            lock (sb) { text = sb.ToString(); }
            var m = TryCloudflareUrl().Match(text);
            if (m.Success)
            {
                baseUrl = m.Value;
            }
            if (proc.HasExited && baseUrl is null)
            {
                break;
            }
        }

        if (baseUrl is null)
        {
            log?.Invoke("tunnel didn't come up; leaving the file on the PC.");
            share.Dispose();
            return null;
        }

        lock (_lock)
        {
            _shares.Add(share);
        }
        // Auto-expire.
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(ttl, ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
            lock (_lock) { _shares.Remove(share); }
            share.Dispose();
        }, CancellationToken.None);

        return $"{baseUrl}/{token}/{Uri.EscapeDataString(fileName)}";
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var s in _shares)
            {
                s.Dispose();
            }
            _shares.Clear();
        }
    }

    [GeneratedRegex(@"https://[a-z0-9-]+\.trycloudflare\.com")]
    private static partial Regex TryCloudflareUrl();

    private sealed class Share(HttpListener listener, string token, string filePath, string fileName) : IDisposable
    {
        public Process? Cloudflared { get; set; }

        public async Task ServeLoopAsync()
        {
            string wanted = $"/{token}/{Uri.EscapeDataString(fileName)}";
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }
                _ = HandleAsync(ctx, wanted);
            }
        }

        private async Task HandleAsync(HttpListenerContext ctx, string wanted)
        {
            try
            {
                if (!string.Equals(ctx.Request.Url?.AbsolutePath, wanted, StringComparison.Ordinal) || !File.Exists(filePath))
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                    return;
                }
                var fi = new FileInfo(filePath);
                ctx.Response.ContentType = "application/octet-stream";
                ctx.Response.ContentLength64 = fi.Length;
                ctx.Response.AddHeader("Content-Disposition", $"attachment; filename=\"{fileName}\"");
                await using var fs = File.OpenRead(filePath);
                await fs.CopyToAsync(ctx.Response.OutputStream).ConfigureAwait(false);
                ctx.Response.OutputStream.Close();
                ctx.Response.Close();
            }
            catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException)
            {
                // client went away / listener closed
            }
        }

        public void Dispose()
        {
            try { if (Cloudflared is { HasExited: false }) { Cloudflared.Kill(entireProcessTree: true); } } catch (Exception) { }
            try { Cloudflared?.Dispose(); } catch (Exception) { }
            try { if (listener.IsListening) { listener.Stop(); } } catch (Exception) { }
            try { listener.Close(); } catch (Exception) { }
        }
    }
}
