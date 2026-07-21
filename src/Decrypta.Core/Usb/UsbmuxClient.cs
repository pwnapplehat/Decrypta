using System.Buffers.Binary;
using System.Net.Sockets;

namespace Decrypta.Core.Usb;

/// <summary>
/// Talks to usbmuxd. On Windows this is provided by Apple Mobile Device Service
/// (installed with iTunes / the "Apple Devices" app) and listens on 127.0.0.1:27015.
///
/// Only the two messages we need are implemented: <c>ListDevices</c> and <c>Connect</c>.
/// After a successful <c>Connect</c>, the underlying socket becomes a raw byte pipe to the
/// requested TCP port on the device - used both to reach lockdownd and to carry the SSH
/// tunnel.
/// </summary>
public sealed class UsbmuxClient
{
    public const string DefaultHost = "127.0.0.1";
    public const int DefaultPort = 27015;

    private const int VersionPlist = 1;
    private const int MessagePlist = 8;

    private readonly string _host;
    private readonly int _port;

    public UsbmuxClient(string host = DefaultHost, int port = DefaultPort)
    {
        _host = host;
        _port = port;
    }

    public bool ServiceReachable()
    {
        try
        {
            using var probe = new TcpClient();
            probe.Connect(_host, _port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public List<MuxDevice> ListDevices()
    {
        using var client = Dial();
        using var stream = client.GetStream();
        SendPlist(stream, Plist.BuildDict(
            ("MessageType", "ListDevices"),
            ("ClientVersionString", "Decrypta"),
            ("ProgName", "Decrypta"),
            ("kLibUSBMuxVersion", 3)), tag: 1);

        var xml = ReceivePlist(stream);
        var devices = new List<MuxDevice>();
        foreach (var entry in Plist.ParseDictsContaining(xml, "DeviceID"))
        {
            if (!entry.TryGetValue("DeviceID", out var idStr) || !int.TryParse(idStr, out var id))
            {
                continue;
            }
            devices.Add(new MuxDevice(
                id,
                entry.GetValueOrDefault("SerialNumber", string.Empty),
                entry.GetValueOrDefault("ConnectionType", "USB")));
        }
        return devices;
    }

    /// <summary>Open a raw byte pipe to <paramref name="devicePort"/> on the given device.
    /// The returned <see cref="TcpClient"/> is owned by the caller. Throws on failure.</summary>
    public TcpClient Connect(int deviceId, int devicePort)
    {
        var client = Dial();
        try
        {
            var stream = client.GetStream();
            // usbmux wants the destination port in network byte order.
            ushort portBe = BinaryPrimitives.ReverseEndianness((ushort)devicePort);
            SendPlist(stream, Plist.BuildDict(
                ("MessageType", "Connect"),
                ("DeviceID", deviceId),
                ("PortNumber", (int)portBe),
                ("ClientVersionString", "Decrypta"),
                ("ProgName", "Decrypta"),
                ("kLibUSBMuxVersion", 3)), tag: 2);

            var result = Plist.ParseTopDict(ReceivePlist(stream));
            var number = result.GetValueOrDefault("Number", "-1");
            if (number != "0")
            {
                throw new UsbmuxException(
                    $"usbmux refused connection to device {deviceId} port {devicePort} (Number={number})");
            }
            return client; // socket is now a passthrough to devicePort
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private TcpClient Dial()
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            client.Connect(_host, _port);
            return client;
        }
        catch (SocketException ex)
        {
            client.Dispose();
            throw new UsbmuxException(
                $"cannot reach usbmuxd at {_host}:{_port}. Install the 'Apple Devices' app or iTunes.", ex);
        }
    }

    private static void SendPlist(NetworkStream stream, byte[] payload, int tag)
    {
        Span<byte> header = stackalloc byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(header[..4], 16 + payload.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(4, 4), VersionPlist);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(8, 4), MessagePlist);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(12, 4), tag);
        stream.Write(header);
        stream.Write(payload);
    }

    private static string ReceivePlist(NetworkStream stream)
    {
        var header = ReadExactly(stream, 4);
        int total = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (total < 16)
        {
            throw new UsbmuxException($"invalid usbmux frame length {total}");
        }
        var body = ReadExactly(stream, total - 4);       // version(4) + message(4) + tag(4) + payload
        const int payloadOffset = 12;
        return System.Text.Encoding.UTF8.GetString(body, payloadOffset, body.Length - payloadOffset);
    }

    internal static byte[] ReadExactly(NetworkStream stream, int count)
    {
        var buffer = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int read = stream.Read(buffer, offset, count - offset);
            if (read <= 0)
            {
                throw new UsbmuxException("usbmux socket closed unexpectedly");
            }
            offset += read;
        }
        return buffer;
    }
}

public sealed class UsbmuxException : Exception
{
    public UsbmuxException(string message) : base(message) { }
    public UsbmuxException(string message, Exception inner) : base(message, inner) { }
}
