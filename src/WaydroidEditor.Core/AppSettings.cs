using System.Text.Json;
using System.Text.Json.Serialization;

namespace WaydroidEditor.Core;

/// <summary>
/// The editor's own persistent preferences, stored as camelCase JSON under the user's config
/// directory. Holds the last-used data root and package plus the game DLL folders the user added.
/// </summary>
public sealed class AppSettings
{
    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Data root last chosen in the UI; empty means "resolve it as the CLI would".</summary>
    public string DataRoot { get; set; } = "";

    /// <summary>Folders the DLLs tab added, each standing for the assemblies inside it.</summary>
    public List<string> GameDlls { get; set; } = new();

    /// <summary>Package selected on the last run, reselected on startup when it still exists.</summary>
    public string LastPackage { get; set; } = "";

    /// <summary>Absolute path of the settings file, honouring <c>XDG_CONFIG_HOME</c> on Linux.</summary>
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "waydroid-editor",
        "settings.json");

    /// <summary>Reads the file, or returns defaults — never throws, so a corrupt file cannot block startup.</summary>
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options)
                       ?? new AppSettings();
        }
        catch (Exception)
        {
            // Corrupt settings must never block startup.
        }
        return new AppSettings();
    }

    /// <summary>Writes the file, creating its config directory when missing.</summary>
    public void Save()
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
    }
}
