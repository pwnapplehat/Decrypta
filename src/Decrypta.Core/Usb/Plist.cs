using System.Text;
using System.Xml.Linq;

namespace Decrypta.Core.Usb;

/// <summary>
/// Minimal XML property-list helpers. usbmuxd and lockdownd both speak XML plists
/// for the request/response messages we use (ListDevices, Connect, GetValue), so a
/// tiny scalar-dict reader/writer is all we need - no binary plist support required.
/// </summary>
public static class Plist
{
    private const string Header =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
        "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" " +
        "\"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n";

    /// <summary>Build a &lt;plist&gt;&lt;dict&gt;…&lt;/dict&gt;&lt;/plist&gt; document from key/value pairs.
    /// Integer values are emitted as &lt;integer&gt;, everything else as &lt;string&gt;.</summary>
    public static byte[] BuildDict(params (string Key, object Value)[] pairs)
    {
        var sb = new StringBuilder(Header);
        sb.Append("<plist version=\"1.0\"><dict>");
        foreach (var (key, value) in pairs)
        {
            sb.Append("<key>").Append(Escape(key)).Append("</key>");
            if (value is int or long or uint or ushort)
            {
                sb.Append("<integer>").Append(value).Append("</integer>");
            }
            else
            {
                sb.Append("<string>").Append(Escape(value.ToString() ?? string.Empty)).Append("</string>");
            }
        }
        sb.Append("</dict></plist>");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>Parse the top-level dict of a plist into scalar string values (nested containers
    /// are rendered as a placeholder tag). Good enough for the flat messages we exchange.</summary>
    public static Dictionary<string, string> ParseTopDict(string xml)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var dict = XDocument.Parse(xml).Descendants("dict").FirstOrDefault();
        if (dict is null)
        {
            return result;
        }
        FillFromDict(dict, result);
        return result;
    }

    /// <summary>Extract every &lt;dict&gt; that contains the given key (used to walk DeviceList entries).</summary>
    public static IEnumerable<Dictionary<string, string>> ParseDictsContaining(string xml, string requiredKey)
    {
        foreach (var dict in XDocument.Parse(xml).Descendants("dict"))
        {
            if (dict.Elements("key").Any(k => k.Value == requiredKey))
            {
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                FillFromDict(dict, map);
                yield return map;
            }
        }
    }

    private static void FillFromDict(XElement dict, Dictionary<string, string> into)
    {
        var nodes = dict.Elements().ToList();
        for (int i = 0; i + 1 < nodes.Count; i++)
        {
            if (nodes[i].Name != "key")
            {
                continue;
            }
            var key = nodes[i].Value;
            var val = nodes[i + 1];
            into[key] = val.Name.LocalName switch
            {
                "string" or "integer" or "real" or "date" => val.Value,
                "true" => "true",
                "false" => "false",
                _ => $"<{val.Name.LocalName}>",
            };
            i++;
        }
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
