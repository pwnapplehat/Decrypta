using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using Decrypta.Core.Tools;
using Decrypta.Core.Tunnel;

namespace Decrypta.Core.TelegramBot;

/// <summary>
/// Runs a local Telegram Bot API server (tdlight build) so the bot can send files up to ~2 GB
/// in-chat. Downloads the server on demand, launches it on a dynamically chosen free port with
/// the user's api_id/api_hash, waits until it's listening, and hands back its base URL. The
/// process is torn down on <see cref="Stop"/> / dispose.
/// </summary>
public sealed class BotApiServerManager : IDisposable
{
    private Process? _proc;
    public string? BaseUrl { get; private set; }
    public int Port { get; private set; }

    /// <summary>Start the server; returns its base URL (http://127.0.0.1:port) or throws on failure.</summary>
    public async Task<string> StartAsync(int apiId, string apiHash, Action<string>? log = null, CancellationToken ct = default)
    {
        Stop();

        if (apiId <= 0 || string.IsNullOrWhiteSpace(apiHash))
        {
            throw new DecryptaException("The local Bot API server needs an api_id and api_hash (from my.telegram.org).");
        }

        string exe = await HelperBinaries.EnsureBotApiServerAsync(log, ct).ConfigureAwait(false);
        int port = UsbTunnel.FindFreePort(8081, 80);

        var proc = new Process
        {
            StartInfo = new ProcessStartInfo(exe)
            {
                ArgumentList =
                {
                    $"--api-id={apiId}",
                    $"--api-hash={apiHash}",
                    "--local",
                    "--http-ip-address=127.0.0.1",
                    $"--http-port={port}",
                },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = HelperBinaries.BotApiDir,
            },
        };
        proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) { log?.Invoke("botapi: " + e.Data.Trim()); } };
        proc.Start();
        proc.BeginErrorReadLine();

        // Wait until the port accepts connections (server is up).
        bool up = false;
        for (int i = 0; i < 40 && !up; i++)
        {
            if (proc.HasExited)
            {
                throw new DecryptaException("Local Bot API server exited on startup — check the api_id/api_hash.");
            }
            await Task.Delay(500, ct).ConfigureAwait(false);
            up = await PortOpenAsync(port, ct).ConfigureAwait(false);
        }
        if (!up)
        {
            try { proc.Kill(entireProcessTree: true); } catch (Exception) { }
            throw new DecryptaException("Local Bot API server didn't start listening in time.");
        }

        _proc = proc;
        Port = port;
        BaseUrl = $"http://127.0.0.1:{port}";
        log?.Invoke($"local Bot API server up on {BaseUrl} (uploads up to ~2 GB).");
        return BaseUrl;
    }

    private static async Task<bool> PortOpenAsync(int port, CancellationToken ct)
    {
        try
        {
            using var c = new TcpClient();
            await c.ConnectAsync(System.Net.IPAddress.Loopback, port, ct).ConfigureAwait(false);
            return c.Connected;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    public void Stop()
    {
        try { if (_proc is { HasExited: false }) { _proc.Kill(entireProcessTree: true); } } catch (Exception) { }
        try { _proc?.Dispose(); } catch (Exception) { }
        _proc = null;
        BaseUrl = null;
        Port = 0;
    }

    public void Dispose() => Stop();
}
