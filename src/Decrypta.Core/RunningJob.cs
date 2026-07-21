using Decrypta.Core.Tools;
using Decrypta.Core.Tunnel;

namespace Decrypta.Core;

/// <summary>A running tool invocation paired with the USB tunnel that keeps it connected to
/// the device. The tunnel is torn down automatically when the process exits.</summary>
public sealed class RunningJob
{
    private readonly ProcessRunner _runner;
    private readonly UsbTunnel _tunnel;

    public Task<int> Completion { get; }

    internal RunningJob(ProcessRunner runner, UsbTunnel tunnel)
    {
        _runner = runner;
        _tunnel = tunnel;
        Completion = RunAsync();
    }

    private async Task<int> RunAsync()
    {
        try
        {
            return await _runner.RunAsync().ConfigureAwait(false);
        }
        finally
        {
            _tunnel.Stop();
            _tunnel.Dispose();
        }
    }

    /// <summary>Write a line to the tool's stdin (e.g. the 2FA code during sign-in).</summary>
    public void SendLine(string text) => _runner.SendLine(text);

    public bool IsRunning => _runner.IsRunning;

    public void Cancel() => _runner.TryKill();
}

public sealed class DecryptaException : Exception
{
    public DecryptaException(string message) : base(message) { }
}
