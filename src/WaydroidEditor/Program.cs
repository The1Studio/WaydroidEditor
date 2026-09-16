using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using WaydroidEditor.Core;

namespace WaydroidEditor;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var options = StartupOptions.Parse(args);
        App.Startup = options;

        // Privileged file access, re-executed once per run through pkexec. Headless by
        // construction: it must never touch a display, which is what makes it work anywhere.
        if (options.Helper)
            return RunHelper(options);

        // The headless render needs no display either.
        if (options.ScreenshotPath is { } screenshot)
            return CaptureScreenshot(options, screenshot);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && !HasDisplay())
            return NoDisplay();

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not open a window: {ex.Message}");
            return NoDisplay();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    static bool HasDisplay() =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    static int NoDisplay()
    {
        Console.Error.WriteLine("""
            No display available — this is a graphical application.

              * Run it from a desktop session. It elevates only the file access it needs.
              * Already root? Use --no-elevate.
              * Verify without a display: --screenshot out.png renders the window headlessly.
            """);
        return 2;
    }

    /// <summary>
    /// The privileged half of the app, run once per session through pkexec. It serves
    /// <c>LIST</c>, <c>READ &lt;pkg&gt;</c>, <c>WRITE &lt;pkg&gt; &lt;bytes&gt;</c> and <c>QUIT</c>
    /// over stdin and never opens a display — which is what lets it work under any compositor.
    /// </summary>
    static int RunHelper(StartupOptions options)
    {
        var dataRoot = options.DataRoot ?? WaydroidAccess.ResolveDataRoot([]);
        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();

        while (true)
        {
            var request = HelperProtocol.ReadLine(stdin);
            if (request is null)
                return 0; // The GUI exited and closed the pipe.

            var parts = request.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            try
            {
                switch (parts)
                {
                    case ["QUIT"]:
                        return 0;

                    case ["LIST"]:
                        HelperProtocol.WriteResponse(stdout, HelperProtocol.Ok,
                            Encoding.UTF8.GetBytes(string.Join('\n', WaydroidAccess.ListPackages(dataRoot))));
                        break;

                    case ["READ", var package]:
                    {
                        var target = WaydroidAccess.ResolveTarget(dataRoot, package);
                        if (!target.Exists)
                            throw new FileNotFoundException($"No PlayerPrefs file for {package}.");

                        var bytes = WaydroidAccess.ReadTarget(target);
                        var header = Encoding.UTF8.GetBytes(target.FilePath + "\n");
                        var payload = new byte[header.Length + bytes.Length];
                        header.CopyTo(payload, 0);
                        bytes.CopyTo(payload, header.Length);
                        HelperProtocol.WriteResponse(stdout, HelperProtocol.Ok, payload);
                        break;
                    }

                    case ["WRITE", var package, var length]:
                    {
                        var data = HelperProtocol.ReadExactly(
                            stdin, int.Parse(length, CultureInfo.InvariantCulture));
                        var target = WaydroidAccess.ResolveTarget(dataRoot, package);
                        WaydroidAccess.WriteTarget(target, data);
                        WaydroidAccess.ForceStopApp(package, out var message);
                        HelperProtocol.WriteResponse(stdout, HelperProtocol.Ok,
                            Encoding.UTF8.GetBytes(message));
                        break;
                    }

                    default:
                        HelperProtocol.WriteError(stdout, $"Unknown request '{request}'.");
                        break;
                }
            }
            catch (Exception ex)
            {
                HelperProtocol.WriteError(stdout, ex is FileNotFoundException
                    ? ex.Message
                    : $"{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>Headless render used as visual verification on machines without a display.</summary>
    static int CaptureScreenshot(StartupOptions options, string path)
    {
        BuildAvaloniaApp()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .SetupWithoutStarting();

        var window = new MainWindow { DataContext = new PrefsViewModel(options) };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var frame = window.CaptureRenderedFrame();
        if (frame is null)
        {
            Console.Error.WriteLine("Headless renderer produced no frame.");
            return 1;
        }

        frame.Save(path, new PngBitmapEncoderOptions());
        Console.WriteLine($"Wrote {path}");
        return 0;
    }
}
