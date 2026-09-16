using System.Diagnostics;
using System.Text;
using WaydroidEditor.Core;
using Xunit;

namespace WaydroidEditor.Tests;

public class WaydroidAccessTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "wpe-tests-" + Guid.NewGuid().ToString("N"));

    public WaydroidAccessTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        GC.SuppressFinalize(this);
    }

    string AddPackage(string package, string fileName)
    {
        var sharedPrefs = Path.Combine(_root, "data", package, "shared_prefs");
        Directory.CreateDirectory(sharedPrefs);
        var path = Path.Combine(sharedPrefs, fileName);
        File.WriteAllText(path, """
            <?xml version="1.0" encoding="utf-8" standalone="yes"?><map><int name="x" value="1" /></map>
            """);
        return path;
    }

    [Fact]
    public void ListsOnlyPackagesWithAPlayerPrefsFile()
    {
        AddPackage("com.beta", "com.beta.v2.playerprefs.xml");
        AddPackage("com.alpha", "com.alpha.v2.playerprefs.xml");
        AddPackage("com.gamma", "settings.xml");

        Assert.Equal(new[] { "com.alpha", "com.beta" }, WaydroidAccess.ListPackages(_root));
    }

    [Fact]
    public void ResolvesTheCanonicalFileThenFallsBackToTheDirectory()
    {
        var canonical = AddPackage("com.alpha", "com.alpha.v2.playerprefs.xml");
        var target = WaydroidAccess.ResolveTarget(_root, "com.alpha");
        Assert.True(target.Exists);
        Assert.Equal(canonical, target.FilePath);

        var alternate = AddPackage("com.beta", "com.beta.playerprefs.xml");
        var fallback = WaydroidAccess.ResolveTarget(_root, "com.beta");
        Assert.True(fallback.Exists);
        Assert.Equal(alternate, fallback.FilePath);

        var missing = WaydroidAccess.ResolveTarget(_root, "com.absent");
        Assert.False(missing.Exists);
    }

    [Fact]
    public void ResolveDataRootPrefersTheCommandLineArgument()
    {
        Assert.Equal(
            "/custom/root",
            WaydroidAccess.ResolveDataRoot(new[] { "--package", "com.x", "--data-root", "/custom/root" }));
    }

    [Fact]
    public void WriteTargetPreservesInodeOwnerAndMode()
    {
        var path = AddPackage("com.alpha", "com.alpha.v2.playerprefs.xml");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

        var before = Stat(path);
        var target = WaydroidAccess.ResolveTarget(_root, "com.alpha");
        var payload = Encoding.UTF8.GetBytes(
            """<?xml version="1.0" encoding="utf-8" standalone="yes"?><map><int name="x" value="42" /></map>""");

        WaydroidAccess.WriteTarget(target, payload);

        Assert.Equal(before, Stat(path));
        Assert.True(File.Exists(path + ".wpe.bak"));
        Assert.Contains("value=\"1\"", File.ReadAllText(path + ".wpe.bak"), StringComparison.Ordinal);
        Assert.Contains("value=\"42\"", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void WriteTargetCreatesAMissingFileWithTheDirectoryOwner()
    {
        var directory = Path.Combine(_root, "data", "com.new", "shared_prefs");
        Directory.CreateDirectory(directory);
        var target = new PrefsTarget(
            "com.new", _root, Path.Combine(directory, "com.new.v2.playerprefs.xml"), false);

        WaydroidAccess.WriteTarget(target, Encoding.UTF8.GetBytes("""<map />"""));

        var info = new FileInfo(target.FilePath);
        Assert.True(info.Exists);
        Assert.Equal(Stat(directory).Owner, Stat(target.FilePath).Owner);
        Assert.Equal("600", Stat(target.FilePath).Mode);
    }

    [Fact]
    public void ForceStopReportsMissingWaydroidWithoutThrowing()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", "/nonexistent");
        try
        {
            Assert.False(WaydroidAccess.ForceStopApp("com.x", out var message));
            Assert.Contains("waydroid", message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", path);
        }
    }

    /// <summary>stat(1) is the only way to observe the inode from .NET.</summary>
    static (string Inode, string Owner, string Mode) Stat(string path)
    {
        var startInfo = new ProcessStartInfo("stat")
        {
            RedirectStandardOutput = true,
        };
        foreach (var argument in new[] { "-c", "%i %u %a", path })
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        var parts = output.Trim().Split(' ');
        return (parts[0], parts[1], parts[2]);
    }
}
