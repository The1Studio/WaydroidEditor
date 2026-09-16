using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using WaydroidEditor.Core;

namespace WaydroidEditor;

public sealed record PrefsReadResult(string FilePath, byte[] Bytes);

/// <summary>
/// Reads and writes the prefs file. Files under the Waydroid data root belong to the Android app's
/// real uid, so the unprivileged GUI cannot touch them — and it must not become root itself, or it
/// loses the user's display (and with it XWayland's cookie on compositors such as Hyprland).
/// Privileged work is delegated to a headless child through pkexec.
/// </summary>
public interface IPrefsStore
{
    IReadOnlyList<string> ListPackages();
    PrefsReadResult Read(string package);
    string Write(string package, byte[] data);
}

public static class PrefsStore
{
    public static IPrefsStore Create(StartupOptions options, string dataRoot)
    {
        if (options.NoElevate || IsRoot() || FindOnPath("pkexec") is null)
            return new DirectPrefsStore(dataRoot);
        return new ElevatedPrefsStore(dataRoot);
    }

    [DllImport("libc", SetLastError = true)]
    static extern uint geteuid();

    static bool IsRoot() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && geteuid() == 0;

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
}

/// <summary>Used when the process is already privileged, or when <c>--no-elevate</c> was passed.</summary>
public sealed class DirectPrefsStore(string dataRoot) : IPrefsStore
{
    public IReadOnlyList<string> ListPackages() => WaydroidAccess.ListPackages(dataRoot);

    public PrefsReadResult Read(string package)
    {
        var target = WaydroidAccess.ResolveTarget(dataRoot, package);
        if (!target.Exists)
            throw new FileNotFoundException($"No PlayerPrefs file for {package}.");
        return new PrefsReadResult(target.FilePath, WaydroidAccess.ReadTarget(target));
    }

    public string Write(string package, byte[] data)
    {
        var target = WaydroidAccess.ResolveTarget(dataRoot, package);
        WaydroidAccess.WriteTarget(target, data);
        WaydroidAccess.ForceStopApp(package, out var message);
        return message;
    }
}

/// <summary>
/// Keeps ONE <c>pkexec</c> child alive for the whole run and multiplexes every privileged
/// operation over its pipes. A fresh <c>pkexec</c> per operation means a fresh polkit prompt per
/// operation, because polkit cannot reuse an authorization it granted to a short-lived process.
/// </summary>
public sealed class ElevatedPrefsStore(string dataRoot) : IPrefsStore, IDisposable
{
    readonly string _self = Environment.ProcessPath
        ?? throw new InvalidOperationException("Cannot determine the path of this executable.");

    Process? _helper;

    public IReadOnlyList<string> ListPackages() =>
        Encoding.UTF8.GetString(Request("LIST", null))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public PrefsReadResult Read(string package)
    {
        var payload = Request("READ " + package, null);
        var separator = Array.IndexOf(payload, (byte)'\n');
        if (separator < 0)
            throw new InvalidOperationException("The privileged helper returned no file header.");
        return new PrefsReadResult(
            Encoding.UTF8.GetString(payload, 0, separator),
            payload[(separator + 1)..]);
    }

    public string Write(string package, byte[] data) =>
        Encoding.UTF8.GetString(Request($"WRITE {package} {data.Length}", data)).Trim();

    public void Dispose()
    {
        if (_helper is null)
            return;
        try
        {
            if (!_helper.HasExited)
                _helper.StandardInput.BaseStream.Write(Encoding.UTF8.GetBytes("QUIT\n"));
        }
        catch (IOException)
        {
            // The child is already gone; nothing to end gracefully.
        }
        _helper.Dispose();
        _helper = null;
    }

    /// <summary>Sends one request and reads its reply, starting or restarting the child as needed.</summary>
    byte[] Request(string header, byte[]? payload)
    {
        var helper = EnsureHelper();
        var stdin = helper.StandardInput.BaseStream;
        var headerBytes = Encoding.UTF8.GetBytes(header + "\n");
        stdin.Write(headerBytes, 0, headerBytes.Length);
        if (payload is not null)
            stdin.Write(payload, 0, payload.Length);
        stdin.Flush();

        var stdout = helper.StandardOutput.BaseStream;
        var status = HelperProtocol.ReadLine(stdout)
            ?? throw new InvalidOperationException(
                "The privileged helper exited before replying. Retry, or check the polkit agent.");

        var space = status.IndexOf(' ');
        if (space < 0)
            throw new InvalidOperationException($"Malformed reply from the privileged helper: '{status}'.");

        var body = HelperProtocol.ReadExactly(stdout, int.Parse(status[(space + 1)..], CultureInfo.InvariantCulture));
        return status[..space] == HelperProtocol.Ok
            ? body
            : throw new InvalidOperationException(Encoding.UTF8.GetString(body));
    }

    Process EnsureHelper()
    {
        if (_helper is { HasExited: false })
            return _helper;

        _helper?.Dispose();
        var startInfo = new ProcessStartInfo("pkexec")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        };
        foreach (var argument in new[] { _self, "--helper", "serve", "--data-root", dataRoot })
            startInfo.ArgumentList.Add(argument);

        _helper = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start pkexec.");
        return _helper;
    }
}
