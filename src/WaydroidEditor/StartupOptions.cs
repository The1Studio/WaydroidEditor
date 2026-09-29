namespace WaydroidEditor;

/// <summary>Parsed command line.</summary>
public sealed class StartupOptions
{
    /// <summary>Overrides the Waydroid data root resolved from the environment.</summary>
    public string? DataRoot { get; init; }

    /// <summary>The package to select on startup, ahead of the remembered one.</summary>
    public string? Package { get; init; }

    /// <summary>Renders the window headlessly to this PNG path instead of showing it.</summary>
    public string? ScreenshotPath { get; init; }

    /// <summary>Set on the pkexec child: serve privileged file requests instead of showing a GUI.</summary>
    public bool Helper { get; init; }

    /// <summary>Skips pkexec and touches the prefs file with this process's own privileges.</summary>
    public bool NoElevate { get; init; }

    /// <summary>Parses the recognised switches from the command line, ignoring anything else.</summary>
    /// <param name="args">The raw command-line arguments.</param>
    /// <returns>The options the rest of the app reads.</returns>
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
