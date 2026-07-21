using Decrypta.Core.Usb;
using Xunit;

namespace Decrypta.Core.Tests;

public class PlistTests
{
    [Fact]
    public void BuildDict_emits_string_and_integer_nodes()
    {
        var bytes = Plist.BuildDict(("MessageType", "ListDevices"), ("kLibUSBMuxVersion", 3));
        var xml = System.Text.Encoding.UTF8.GetString(bytes);

        Assert.Contains("<key>MessageType</key><string>ListDevices</string>", xml);
        Assert.Contains("<key>kLibUSBMuxVersion</key><integer>3</integer>", xml);
    }

    [Fact]
    public void BuildDict_escapes_special_characters()
    {
        var bytes = Plist.BuildDict(("Note", "a & b < c > d"));
        var xml = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.Contains("a &amp; b &lt; c &gt; d", xml);
    }

    [Fact]
    public void ParseTopDict_reads_scalars()
    {
        const string xml = """
            <plist version="1.0"><dict>
              <key>Type</key><string>com.apple.mobile.lockdown</string>
              <key>Number</key><integer>0</integer>
              <key>Enabled</key><true/>
            </dict></plist>
            """;
        var dict = Plist.ParseTopDict(xml);

        Assert.Equal("com.apple.mobile.lockdown", dict["Type"]);
        Assert.Equal("0", dict["Number"]);
        Assert.Equal("true", dict["Enabled"]);
    }

    [Fact]
    public void ParseDictsContaining_reads_the_flat_properties_entry()
    {
        // usbmuxd's ListDevices reply nests the real fields in a Properties sub-dict that
        // itself carries DeviceID/SerialNumber/ConnectionType flat - that's the entry we key on.
        const string xml = """
            <plist version="1.0"><dict><key>DeviceList</key><array>
              <dict><key>DeviceID</key><integer>3</integer><key>MessageType</key><string>Attached</string>
                    <key>Properties</key>
                    <dict><key>DeviceID</key><integer>3</integer>
                          <key>SerialNumber</key><string>abc123</string>
                          <key>ConnectionType</key><string>USB</string></dict></dict>
            </array></dict></plist>
            """;
        var entries = Plist.ParseDictsContaining(xml, "SerialNumber").ToList();

        Assert.Single(entries);
        Assert.Equal("3", entries[0]["DeviceID"]);
        Assert.Equal("abc123", entries[0]["SerialNumber"]);
        Assert.Equal("USB", entries[0]["ConnectionType"]);
    }
}
