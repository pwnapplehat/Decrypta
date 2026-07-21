using System.Diagnostics;

namespace Decrypta.Core.Tools;

/// <summary>
/// Keeps ipadecrypt's encrypted-IPA cache INSIDE the user's chosen output folder instead of
/// buried in LocalAppData, and can wipe it completely.
///
/// ipadecrypt always caches at <c>&lt;root-dir&gt;\cache</c> with no way to relocate it, and that
/// root also holds credentials — which we do NOT want in the output folder. So we point
/// <c>&lt;root-dir&gt;\cache</c> at <c>&lt;output&gt;\.decrypta-cache</c> with a directory junction
/// (no admin needed, unlike a symlink). The big files then physically live in the output
/// folder, are listed by the cleaner, and vanish if the user deletes that folder — while
/// credentials stay safely in LocalAppData.
/// </summary>
public static class CacheManager
{
    public const string CacheFolderName = ".decrypta-cache";

    public static string CacheTarget(string outputDir) => Path.Combine(outputDir, CacheFolderName);

    /// <summary>Point <c>&lt;root&gt;\cache</c> at <c>&lt;outputDir&gt;\.decrypta-cache</c> via a junction.
    /// Best-effort: on any failure ipadecrypt just keeps caching under its root (still cleanable
    /// via <see cref="Clean"/>). Returns true if the redirect is in place.</summary>
    public static bool RedirectCache(string rootDir, string outputDir)
    {
        try
        {
            string target = CacheTarget(outputDir);
            Directory.CreateDirectory(target);
            HideFolder(target);

            string link = Path.Combine(rootDir, "cache");
            string targetFull = Path.GetFullPath(target);

            if (Directory.Exists(link))
            {
                var info = new DirectoryInfo(link);
                bool isReparse = info.Attributes.HasFlag(FileAttributes.ReparsePoint);
                if (isReparse)
                {
                    var current = Directory.ResolveLinkTarget(link, returnFinalTarget: true)?.FullName;
                    if (string.Equals(current, targetFull, StringComparison.OrdinalIgnoreCase))
                    {
                        return true; // already redirected correctly
                    }
                    Directory.Delete(link); // removes the junction only, not the target contents
                }
                else
                {
                    // A real cache dir from before the redirect: move its files into the target,
                    // then replace it with the junction so nothing is lost.
                    MoveContents(link, target);
                    Directory.Delete(link, recursive: true);
                }
            }
            else
            {
                Directory.CreateDirectory(rootDir);
            }

            return CreateJunction(link, targetFull);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Total bytes currently cached (in the output folder target and any per-root
    /// caches that weren't redirected).</summary>
    public static long CacheSizeBytes(string outputDir, IEnumerable<string> accountRoots)
    {
        long total = DirSize(CacheTarget(outputDir));
        foreach (var root in accountRoots)
        {
            string cache = Path.Combine(root, "cache");
            if (Directory.Exists(cache) && !new DirectoryInfo(cache).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                total += DirSize(cache);
            }
        }
        return total;
    }

    /// <summary>Wipe every cached/partial download: the output-folder cache target and any
    /// non-redirected per-root caches. Returns bytes freed.</summary>
    public static long Clean(string outputDir, IEnumerable<string> accountRoots)
    {
        long freed = 0;
        freed += WipeContents(CacheTarget(outputDir));
        foreach (var root in accountRoots)
        {
            string cache = Path.Combine(root, "cache");
            if (Directory.Exists(cache) && !new DirectoryInfo(cache).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                freed += WipeContents(cache);
            }
        }
        return freed;
    }

    /// <summary>Remove only partial (*.tmp) downloads left by an interrupted/failed decrypt,
    /// keeping any fully-cached IPAs. Returns bytes freed.</summary>
    public static long CleanPartials(string outputDir, IEnumerable<string> accountRoots)
    {
        long freed = 0;
        var dirs = new List<string> { CacheTarget(outputDir) };
        dirs.AddRange(accountRoots.Select(r => Path.Combine(r, "cache")));
        foreach (var dir in dirs.Distinct())
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*.tmp", SearchOption.AllDirectories))
                {
                    try { long s = new FileInfo(f).Length; File.Delete(f); freed += s; }
                    catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return freed;
    }

    private static long WipeContents(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return 0;
        }
        long size = DirSize(dir);
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        foreach (var d in Directory.EnumerateDirectories(dir))
        {
            try { Directory.Delete(d, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return size;
    }

    private static long DirSize(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return 0;
        }
        long total = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(f).Length; } catch (IOException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return total;
    }

    private static void MoveContents(string from, string to)
    {
        foreach (var f in Directory.EnumerateFiles(from))
        {
            try { File.Move(f, Path.Combine(to, Path.GetFileName(f)), overwrite: true); }
            catch (IOException) { }
        }
    }

    private static void HideFolder(string dir)
    {
        try
        {
            var info = new DirectoryInfo(dir);
            if (!info.Attributes.HasFlag(FileAttributes.Hidden))
            {
                info.Attributes |= FileAttributes.Hidden;
            }
        }
        catch (IOException) { }
    }

    private static bool CreateJunction(string link, string target)
    {
        // mklink /J makes a directory junction and needs no elevation (a symlink would).
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        try
        {
            using var p = Process.Start(psi)!;
            p.WaitForExit(10000);
            return p.ExitCode == 0 && new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return false;
        }
    }
}
