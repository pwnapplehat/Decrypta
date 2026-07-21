namespace Decrypta.Core.Tools;

/// <summary>Builds process runners for the bundled ipatool binary (App Store client).</summary>
public sealed class Ipatool
{
    private readonly string _exe;

    public Ipatool(string? exe = null) => _exe = exe ?? AppPaths.IpatoolExe;

    public bool Exists => File.Exists(_exe);

    public ProcessRunner Search(string term, int limit)
        => new(_exe, ["search", term, "-l", limit.ToString(), "--format", "json"], AppContext.BaseDirectory);

    public ProcessRunner Download(string bundleId, string output, bool purchase)
    {
        var args = new List<string> { "download", "-b", bundleId, "-o", output };
        if (purchase)
        {
            args.Add("--purchase");
        }
        return new ProcessRunner(_exe, args, AppContext.BaseDirectory);
    }

    public string Version()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(_exe, "--version")
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
