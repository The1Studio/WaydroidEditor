namespace WaydroidEditor;

/// <summary>Parsed command line.</summary>
public sealed class StartupOptions
{
    public string? DataRoot { get; init; }
    public string? Package { get; init; }
    public string? ScreenshotPath { get; init; }
    /// <summary>Set on the pkexec child: serve privileged file requests instead of showing a GUI.</summary>
    public bool Helper { get; init; }
    public bool NoElevate { get; init; }

    public static StartupOptions Parse(string[] args)
    {
        string? dataRoot = null, package = null, screenshot = null;
        var noElevate = false;
        var helper = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--data-root" when i + 1 < args.Length:
                    dataRoot = args[++i];
                    break;
                case "--package" when i + 1 < args.Length:
                    package = args[++i];
                    break;
                case "--screenshot" when i + 1 < args.Length:
                    screenshot = args[++i];
                    break;
                case "--helper":
                    helper = true;
                    break;
                case "--no-elevate":
                    noElevate = true;
                    break;
            }
        }

        return new StartupOptions
        {
            DataRoot = dataRoot,
            Package = package,
            ScreenshotPath = screenshot,
            Helper = helper,
            NoElevate = noElevate,
        };
    }
}
