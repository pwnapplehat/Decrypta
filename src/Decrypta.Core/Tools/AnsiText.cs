using System.Text.RegularExpressions;

namespace Decrypta.Core.Tools;

/// <summary>Strips terminal control sequences from tool output so it renders cleanly in a GUI
/// log or a plain console pipe: CSI (colours, cursor moves), OSC, and bare carriage returns
/// (spinner rewrites).</summary>
public static partial class AnsiText
{
    public static string Strip(string text) => AnsiRegex().Replace(text, string.Empty);

    [GeneratedRegex("\u001b\\[[0-9;?]*[ -/]*[@-~]|\u001b\\][^\u0007]*\u0007|\r")]
    private static partial Regex AnsiRegex();
}
