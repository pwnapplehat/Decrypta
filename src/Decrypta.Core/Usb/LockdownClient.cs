using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace Decrypta.Core.Usb;

/// <summary>
/// Reads device properties from lockdownd. We only ever issue unauthenticated
/// <c>QueryType</c>/<c>GetValue</c> requests, which return public values such as the
/// device name, model and iOS version without needing a pairing session - enough to show
/// rich device info before the user signs in.
///
/// lockdownd frames each plist with a 4-byte big-endian length prefix (distinct from the
/// usbmux 16-byte header used to establish the connection).
/// </summary>
public sealed class LockdownClient : IDisposable
{
    private const int LockdownPort = 62078;

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;

    private LockdownClient(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
    }

    public static LockdownClient Open(UsbmuxClient usbmux, int deviceId)
    {
        var client = usbmux.Connect(deviceId, LockdownPort);
        return new LockdownClient(client);
    }

    public string QueryType()
    {
        Send(Plist.BuildDict(("Request", "QueryType")));
        return Plist.ParseTopDict(Receive()).GetValueOrDefault("Type", string.Empty);
    }

    public string? GetValue(string key)
    {
        Send(Plist.BuildDict(("Request", "GetValue"), ("Key", key)));
        var resp = Plist.ParseTopDict(Receive());
        return resp.TryGetValue("Value", out var v) ? v : null;
    }

    private void Send(byte[] payload)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, payload.Length);
        _stream.Write(len);
        _stream.Write(payload);
    }

    private string Receive()
    {
        var header = UsbmuxClient.ReadExactly(_stream, 4);
        int len = BinaryPrimitives.ReadInt32BigEndian(header);
        var body = UsbmuxClient.ReadExactly(_stream, len);
        return Encoding.UTF8.GetString(body);
    }

    public void Dispose()
    {
        _stream.Dispose();
        _client.Dispose();
    }
}
