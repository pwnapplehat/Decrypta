using System.Diagnostics;
using System.Text;

namespace Decrypta.Core.Tools;

/// <summary>
/// Runs a child tool with merged stdout+stderr streamed as ANSI-stripped text chunks, and an
/// open stdin so callers can answer interactive prompts (e.g. the App Store 2FA code that
/// ipadecrypt requests without a trailing newline). Output is read in raw chunks - not lines -
/// so prompts surface the instant they are written.
/// </summary>
public sealed class ProcessRunner
{
    private readonly ProcessStartInfo _psi;
    private Process? _process;

    /// <summary>Raised (on background threads) with each decoded, ANSI-stripped output chunk.</summary>
    public event Action<string>? Output;

    public ProcessRunner(string fileName, IEnumerable<string> arguments,
        string? workingDirectory = null, IDictionary<string, string>? environment = null)
    {
        _psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? AppContext.BaseDirectory,
        };
        foreach (var arg in arguments)
        {
            _psi.ArgumentList.Add(arg);
        }
        if (environment is not null)
        {
            foreach (var (k, v) in environment)
            {
                _psi.Environment[k] = v;
            }
        }
    }

    public async Task<int> RunAsync(CancellationToken token = default)
    {
        _process = new Process { StartInfo = _psi, EnableRaisingEvents = true };
        _process.Start();

        var stdout = PumpAsync(_process.StandardOutput, token);
        var stderr = PumpAsync(_process.StandardError, token);

        using (token.Register(TryKill))
        {
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        return _process.ExitCode;
    }

    private async Task PumpAsync(StreamReader reader, CancellationToken token)
    {
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[4096];
        var chars = new char[4096];
        var stream = reader.BaseStream;
        while (true)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(bytes.AsMemory(), token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                break;
            }
            if (read <= 0)
            {
                break;
            }
            int produced = decoder.GetChars(bytes, 0, read, chars, 0);
            if (produced > 0)
            {
                Output?.Invoke(AnsiText.Strip(new string(chars, 0, produced)));
            }
        }
    }

    public void SendLine(string text)
    {
        try
        {
            _process?.StandardInput.WriteLine(text);
            _process?.StandardInput.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // process already gone
        }
    }

    public bool IsRunning => _process is { HasExited: false };

    public void TryKill()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // already exited
        }
    }
}
