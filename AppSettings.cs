using System.IO;
using System.Text.Json;

namespace MarantzController;

// ---------------------------------------------------------------------------
//  Per-user settings, stored as JSON in %APPDATA%\MarantzController\settings.json.
//  Only what cannot be read back from the receiver lives here (e.g. its address).
//  A missing or broken file is never fatal: defaults are used instead.
// ---------------------------------------------------------------------------

public sealed class AppSettings
{
    public string IpAddress { get; set; } = "";

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MarantzController", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch
        {
            // Corrupt or unreadable file – fall back to defaults.
        }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Settings are a convenience; failing to save must not break the app.
        }
    }
}
