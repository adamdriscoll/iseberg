using System.Text.Json;

namespace Iseberg.Core;

public sealed class UserSettings
{
    public double Zoom { get; set; } = 100;
    public string Layout { get; set; } = "Top";
    public bool ShowCommands { get; set; } = true;
    public bool ShowLineNumbers { get; set; } = true;
    public bool WordWrap { get; set; }
    public bool LoadProfiles { get; set; }
    public List<string> RecentFiles { get; set; } = [];
    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Iseberg", "settings.json");

    public static async Task<UserSettings> LoadAsync(string? path = null)
    {
        path ??= SettingsPath;
        if (!File.Exists(path)) return new();
        var json = await File.ReadAllTextAsync(path);
        var settings = JsonSerializer.Deserialize<UserSettings>(json) ?? throw new InvalidDataException("The settings file is empty.");
        settings.Zoom = Math.Clamp(settings.Zoom, 20, 400);
        if (settings.Layout is not ("Top" or "Right" or "Maximized")) settings.Layout = "Top";
        return settings;
    }

    public async Task SaveAsync(string? path = null)
    {
        path ??= SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, overwrite: true);
    }
}
