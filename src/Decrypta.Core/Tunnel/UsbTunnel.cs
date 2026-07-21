using System.Net;
using System.Net.Sockets;
using System.Text;
using Decrypta.Core.Usb;

namespace Decrypta.Core.Tunnel;

/// <summary>
/// Forwards a local TCP port to a port on the device over usbmuxd, so tools that only
/// know how to speak plain TCP/SSH (here: ipadecrypt) can reach the device with no manual
/// networking. usbmuxd routes to whichever transport the chosen device id uses, so this
/// works over the USB cable AND over Wi-Fi (network-paired device) transparently. Each
/// accepted local connection gets its own usbmux passthrough and the two streams are
/// pumped in both directions until either side closes.
/// </summary>
public sealed class UsbTunnel : IDisposable
{
    private readonly UsbmuxClient _usbmux;
    private readonly int _deviceId;
    private readonly int _devicePort;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public int LocalPort { get; private set; }

    public UsbTunnel(UsbmuxClient usbmux, int deviceId, int devicePort = 22, int localPort = 0)
    {
        _usbmux = usbmux;
        _deviceId = deviceId;
        _devicePort = devicePort;
        LocalPort = localPort;
    }

    public UsbTunnel Start()
    {
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, LocalPort);
        _listener.Start();
        LocalPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        return this;
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        var listener = _listener!;
        while (!token.IsCancellationRequested)
        {
            TcpClient local;
            try
            {
                local = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }
            _ = Task.Run(() => BridgeAsync(local, token), token);
        }
    }

    private async Task BridgeAsync(TcpClient local, CancellationToken token)
    {
        local.NoDelay = true;
        TcpClient? remote = null;
        try
        {
            // usbmux Connect is synchronous; do it off the accept loop.
            remote = await Task.Run(() => _usbmux.Connect(_deviceId, _devicePort), token).ConfigureAwait(false);
            using var link = CancellationTokenSource.CreateLinkedTokenSource(token);
            var localStream = local.GetStream();
            var remoteStream = remote.GetStream();
            var up = localStream.CopyToAsync(remoteStream, link.Token);
            var down = remoteStream.CopyToAsync(localStream, link.Token);
            await Task.WhenAny(up, down).ConfigureAwait(false);
            link.Cancel(); // tear down the other direction
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or UsbmuxException)
        {
            // Connection ended or device went away; nothing to do but clean up.
        }
        finally
        {
            remote?.Dispose();
            local.Dispose();
        }
    }

    /// <summary>Connect through the tunnel and read the peer's SSH identification banner
    /// (e.g. "SSH-2.0-OpenSSH_9.7"), or null if nothing answers in time.</summary>
    public string? VerifySshBanner(TimeSpan timeout)
    {
        try
        {
            using var probe = new TcpClient();
            if (!probe.ConnectAsync(IPAddress.Loopback, LocalPort).Wait(timeout))
            {
                return null;
            }
            probe.ReceiveTimeout = (int)timeout.TotalMilliseconds;
            var buffer = new byte[256];
            int read = probe.GetStream().Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                return null;
            }
            var text = Encoding.ASCII.GetString(buffer, 0, read).Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch (Exception ex) when (ex is IOException or SocketException or AggregateException)
        {
            return null;
        }
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        try { _listener?.Stop(); } catch (SocketException) { }
        try { _acceptLoop?.Wait(TimeSpan.FromSeconds(3)); } catch (Exception) { /* best effort */ }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }

    /// <summary>Pick a bindable loopback port at or after <paramref name="preferred"/>.</summary>
    public static int FindFreePort(int preferred = 2222, int span = 40)
    {
        for (int candidate = preferred; candidate < preferred + span; candidate++)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, candidate);
                listener.Start();
                listener.Stop();
                return candidate;
            }
            catch (SocketException)
            {
                // in use, try next
            }
        }
        return preferred;
    }
}
