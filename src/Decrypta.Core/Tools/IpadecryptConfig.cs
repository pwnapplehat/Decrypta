using System.Text.Json;
using System.Text.Json.Nodes;

namespace Decrypta.Core.Tools;

/// <summary>
/// Reads and writes ipadecrypt's own <c>config.json</c> (schema version 2). We pre-seed the
/// Apple and device sections so ipadecrypt's interactive bootstrap only needs the 2FA code,
/// and we point its SSH client at our USB tunnel by rewriting the device host/port. Unknown
/// fields (e.g. tokens ipadecrypt stores after login) are preserved via JsonNode.
/// </summary>
public sealed class IpadecryptConfig
{
    private const int SchemaVersion = 2;

    private readonly string _path;

    public IpadecryptConfig(string? rootDir = null)
        => _path = System.IO.Path.Combine(rootDir ?? AppPaths.LegacyIpadecryptRoot, "config.json");

    public string ConfigPath => _path;

    private JsonObject Load()
    {
        if (!File.Exists(_path))
        {
            return new JsonObject { ["version"] = SchemaVersion };
        }
        try
        {
            return JsonNode.Parse(File.ReadAllText(_path)) as JsonObject
                   ?? new JsonObject { ["version"] = SchemaVersion };
        }
        catch (JsonException)
        {
            return new JsonObject { ["version"] = SchemaVersion };
        }
    }

    private void Save(JsonObject root)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        root["version"] = SchemaVersion;
        var options = new JsonSerializerOptions { WriteIndented = true };
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString(options));
        File.Move(tmp, _path, overwrite: true);
    }

    private static JsonObject Section(JsonObject root, string name)
    {
        if (root[name] is JsonObject existing)
        {
            return existing;
        }
        var created = new JsonObject();
        root[name] = created;
        return created;
    }

    public void SetAppleCredentials(string email, string password)
    {
        var root = Load();
        var apple = Section(root, "apple");
        apple["email"] = email;
        apple["password"] = password;
        Save(root);
    }

    public void SetDeviceFull(string user, string password, string host, int port)
    {
        var root = Load();
        var device = Section(root, "device");
        device["host"] = host;
        device["port"] = port;
        device["user"] = user;
        device["acceptNewHostKey"] = true;
        device["auth"] = new JsonObject { ["kind"] = "password", ["password"] = password };
        Save(root);
    }

    public void SetDeviceEndpoint(string host, int port)
    {
        var root = Load();
        var device = Section(root, "device");
        device["host"] = host;
        device["port"] = port;
        if (device["acceptNewHostKey"] is null)
        {
            device["acceptNewHostKey"] = true;
        }
        Save(root);
    }

    public void ClearApple()
    {
        var root = Load();
        root["apple"] = new JsonObject();
        Save(root);
    }

    public bool IsAppleConfigured()
    {
        var apple = Load()["apple"] as JsonObject;
        return apple?["email"]?.GetValue<string>() is { Length: > 0 };
    }

    public bool IsDeviceConfigured()
    {
        var device = Load()["device"] as JsonObject;
        return device?["host"]?.GetValue<string>() is { Length: > 0 };
    }

    public string? AppleEmail() => (Load()["apple"] as JsonObject)?["email"]?.GetValue<string>();

    public string? ApplePassword() => (Load()["apple"] as JsonObject)?["password"]?.GetValue<string>();
}
