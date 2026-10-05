using System.Text.Json;

namespace Iseberg.Core;

public sealed class ThemeFile
{
    [System.Text.Json.Serialization.JsonRequired] public int Version { get; set; } = 1;
    [System.Text.Json.Serialization.JsonRequired] public EditorTheme Theme { get; set; } = new();
    [System.Text.Json.Serialization.JsonRequired] public string FontFamily { get; set; } = "Lucida Console";
    [System.Text.Json.Serialization.JsonRequired] public double FontSize { get; set; } = 9;

    public static ThemeFile FromSettings(UserSettings settings) => new()
    {
        Theme = settings.Theme.Copy(), FontFamily = settings.FontFamily, FontSize = settings.FontSize
    };

    public void Validate()
    {
        if (Version != 1 || Theme is null || string.IsNullOrWhiteSpace(Theme.Name) ||
            Theme.Colors is null || !Theme.Colors.Keys.Order().SequenceEqual(EditorTheme.DefaultColors().Keys.Order()) ||
            Theme.Colors.Values.Any(color => !EditorTheme.IsColor(color)) ||
            string.IsNullOrWhiteSpace(FontFamily) || !double.IsFinite(FontSize) || FontSize is < 6 or > 72)
            throw new InvalidDataException("The theme file is invalid or unsupported.");
    }

    public static async Task<ThemeFile> LoadAsync(string path)
    {
        var json = await File.ReadAllTextAsync(path);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("Theme", out var content) || content.ValueKind != JsonValueKind.Object ||
            !content.TryGetProperty("Name", out _) || !content.TryGetProperty("Colors", out _))
            throw new InvalidDataException("The theme file must include a named theme and its colors.");
        var theme = JsonSerializer.Deserialize<ThemeFile>(json)
            ?? throw new InvalidDataException("The theme file is empty.");
        theme.Validate();
        return theme;
    }

    public async Task SaveAsync(string path)
    {
        Validate();
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}
