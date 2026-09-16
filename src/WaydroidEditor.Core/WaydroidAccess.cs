using System.Diagnostics;

namespace WaydroidEditor.Core;

public sealed record PrefsTarget(string Package, string DataRoot, string FilePath, bool Exists);

/// <summary>Locates and rewrites Waydroid's host-side view of <c>/data</c>.</summary>
public static class WaydroidAccess
{
    public const string PrefsSuffix = "playerprefs.xml";

    /// <summary>First match wins: <c>--data-root</c>, <c>WAYDROID_DATA_ROOT</c>, XDG, /var/lib.</summary>
    public static string ResolveDataRoot(IReadOnlyList<string> cliArgs)
    {
        for (var i = 0; i + 1 < cliArgs.Count; i++)
            if (cliArgs[i] == "--data-root")
                return cliArgs[i + 1];

        var fromEnv = Environment.GetEnvironmentVariable("WAYDROID_DATA_ROOT");
        if (!string.IsNullOrEmpty(fromEnv))
            return fromEnv;

        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrEmpty(xdgDataHome))
        {
            var home = Environment.GetEnvironmentVariable("HOME")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            xdgDataHome = Path.Combine(home, ".local", "share");
        }

        var xdgCandidate = Path.Combine(xdgDataHome, "waydroid", "data");
        if (Directory.Exists(xdgCandidate))
            return xdgCandidate;

        const string systemCandidate = "/var/lib/waydroid/data";
        return Directory.Exists(systemCandidate) ? systemCandidate : xdgCandidate;
    }

    /// <summary>Packages under <c>&lt;dataRoot&gt;/data/*/shared_prefs/</c> that hold a playerprefs file.</summary>
    public static IReadOnlyList<string> ListPackages(string dataRoot)
    {
        var dataDir = Path.Combine(dataRoot, "data");
        if (!Directory.Exists(dataDir))
            return Array.Empty<string>();

        var packages = new List<string>();
        foreach (var packageDir in Directory.EnumerateDirectories(dataDir))
        {
            var sharedPrefs = Path.Combine(packageDir, "shared_prefs");
            if (Directory.Exists(sharedPrefs) && HasPlayerPrefs(sharedPrefs))
                packages.Add(Path.GetFileName(packageDir));
        }
        packages.Sort(StringComparer.Ordinal);
        return packages;
    }

    public static PrefsTarget ResolveTarget(string dataRoot, string package)
    {
        var sharedPrefs = Path.Combine(dataRoot, "data", package, "shared_prefs");
        var canonical = Path.Combine(sharedPrefs, package + ".v2." + PrefsSuffix);
        if (File.Exists(canonical))
            return new PrefsTarget(package, dataRoot, canonical, true);

        if (Directory.Exists(sharedPrefs))
        {
            var alternate = Directory.EnumerateFiles(sharedPrefs, "*" + PrefsSuffix)
                .OrderBy(p => p, StringComparer.Ordinal)
                .FirstOrDefault();
            if (alternate is not null)
                return new PrefsTarget(package, dataRoot, alternate, true);
        }

        return new PrefsTarget(package, dataRoot, canonical, false);
    }

    public static byte[] ReadTarget(PrefsTarget target) => File.ReadAllBytes(target.FilePath);

    /// <summary>
    /// Truncates in place when the file exists so the app's uid keeps ownership of its own
    /// prefs file, and drops a <c>.wpe.bak</c> first. New files are chowned to the directory owner.
    /// </summary>
    public static void WriteTarget(PrefsTarget target, byte[] data)
    {
        if (File.Exists(target.FilePath))
        {
            RunTool("cp", "-a", target.FilePath, target.FilePath + ".wpe.bak");
            using var existing = new FileStream(target.FilePath, FileMode.Open, FileAccess.Write);
            existing.SetLength(0);
            existing.Write(data);
            existing.Flush(flushToDisk: true);
            return;
        }

        var directory = Path.GetDirectoryName(target.FilePath)!;
        Directory.CreateDirectory(directory);
        using (var created = new FileStream(target.FilePath, FileMode.Create, FileAccess.Write))
        {
            created.Write(data);
            created.Flush(flushToDisk: true);
        }
        RunTool("chown", "--reference=" + directory, target.FilePath);
        RunTool("chmod", "600", target.FilePath);
    }

    /// <summary>Best effort: a still-running app would flush its in-memory prefs over our edit.</summary>
    public static bool ForceStopApp(string package, out string message)
    {
        var waydroid = FindOnPath("waydroid");
        if (waydroid is null)
        {
            message = "waydroid not on PATH — skipped force-stop.";
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo(waydroid);
            foreach (var argument in new[] { "shell", "--", "am", "force-stop", package })
                startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                message = "could not start waydroid.";
                return false;
            }
            if (!process.WaitForExit(15_000))
            {
                process.Kill(entireProcessTree: true);
                message = "waydroid force-stop timed out.";
                return false;
            }
            if (process.ExitCode != 0)
            {
                message = $"waydroid force-stop exited {process.ExitCode}.";
                return false;
            }
            message = $"Force-stopped {package}.";
            return true;
        }
        catch (Exception ex)
        {
            message = "force-stop failed: " + ex.Message;
            return false;
        }
    }

    static bool HasPlayerPrefs(string sharedPrefsDir) =>
        Directory.EnumerateFiles(sharedPrefsDir, "*" + PrefsSuffix).Any();

    static string? FindOnPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;
        foreach (var directory in path.Split(Path.PathSeparator))
        {
            if (directory.Length == 0)
                continue;
            var candidate = Path.Combine(directory, executable);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    static void RunTool(string executable, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(executable) { RedirectStandardError = true };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start '{executable}'.");
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"{executable} {string.Join(' ', arguments)} exited {process.ExitCode}: {stderr.Trim()}");
    }
}
