namespace Decrypta.Core.Tools;

/// <summary>Builds argument lists and process runners for the bundled ipadecrypt binary.</summary>
public sealed class Ipadecrypt
{
    private readonly string _exe;
    private readonly string _rootDir;

    public IpadecryptConfig Config { get; }

    public Ipadecrypt(string? exe = null, string? rootDir = null)
    {
        _exe = exe ?? AppPaths.IpadecryptExe;
        _rootDir = rootDir ?? AppPaths.IpadecryptRoot;
        Config = new IpadecryptConfig(_rootDir);
    }

    public bool Exists => File.Exists(_exe);

    private List<string> Base() => ["--root-dir", _rootDir];

    public ProcessRunner Bootstrap(bool reset = false)
    {
        var args = Base();
        args.Add("bootstrap");
        if (reset)
        {
            args.Add("--reset");
        }
        return new ProcessRunner(_exe, args, AppContext.BaseDirectory);
    }

    public ProcessRunner Decrypt(string target, string? output, IEnumerable<string> flags)
    {
        var args = Base();
        args.Add("decrypt");
        args.Add(target);
        if (!string.IsNullOrEmpty(output))
        {
            args.Add("-o");
            args.Add(output);
        }
        args.AddRange(flags);
        return new ProcessRunner(_exe, args, AppContext.BaseDirectory);
    }

    public ProcessRunner Versions(string target)
    {
        var args = Base();
        args.Add("versions");
        args.Add(target);
        return new ProcessRunner(_exe, args, AppContext.BaseDirectory);
    }

    public string Version()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(_exe, "-v")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            var outText = p.StandardOutput.ReadToEnd();
            var errText = p.StandardError.ReadToEnd();
            p.WaitForExit(5000);
            return (outText + errText).Trim().Split('\n').FirstOrDefault()?.Trim() ?? "unknown";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "unknown";
        }
    }
}
