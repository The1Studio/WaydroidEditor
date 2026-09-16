using System.Text.Json;
using System.Text.Json.Serialization;

namespace WaydroidPrefsEditor.Core;

public sealed class AppSettings
{
    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string DataRoot { get; set; } = "";
    public List<string> GameDlls { get; set; } = new();
    public string LastPackage { get; set; } = "";

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "waydroid-playerprefs-editor",
        "settings.json");

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

    public void Save()
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
    }
}
