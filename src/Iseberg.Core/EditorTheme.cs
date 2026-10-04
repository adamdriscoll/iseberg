namespace Iseberg.Core;

public sealed class EditorTheme
{
    public string Name { get; set; } = "Dark Console, Light Editor (default)";
    public Dictionary<string, string> Colors { get; set; } = DefaultColors();

    public EditorTheme Copy() => new() { Name = Name, Colors = new(Colors) };

    public static Dictionary<string, string> DefaultColors() => new()
    {
        ["Script.Foreground"] = "#000000", ["Script.Background"] = "#FFFFFF",
        ["Console.Foreground"] = "#FFFFFF", ["Console.Background"] = "#012456",
        ["Console.TextBackground"] = "#012456",
        ["Script.Comment"] = "#006400", ["Script.Keyword"] = "#0000FF",
        ["Script.String"] = "#8B0000", ["Script.Variable"] = "#FF4500",
        ["Script.Number"] = "#FF00FF", ["Script.Command"] = "#0000FF",
        ["Script.Function"] = "#FF00FF", ["Script.Attribute"] = "#00BFFF",
        ["Script.CommandArgument"] = "#FF00FF", ["Script.Label"] = "#0000FF",
        ["Script.Parameter"] = "#000080", ["Script.Type"] = "#008080",
        ["Script.Operator"] = "#000000", ["Script.Member"] = "#000000",
        ["Console.Comment"] = "#90EE90", ["Console.Keyword"] = "#00FFFF",
        ["Console.String"] = "#FFFF00", ["Console.Variable"] = "#FF4500",
        ["Console.Number"] = "#EE82EE", ["Console.Command"] = "#FFFFFF",
        ["Console.Function"] = "#EE82EE", ["Console.Attribute"] = "#00FFFF",
        ["Console.CommandArgument"] = "#EE82EE", ["Console.Label"] = "#00FFFF",
        ["Console.Parameter"] = "#FFFFFF", ["Console.Type"] = "#00FFFF",
        ["Console.Operator"] = "#FFFFFF", ["Console.Member"] = "#FFFFFF",
        ["Xml.Comment"] = "#006400", ["Xml.Tag"] = "#800000",
        ["Xml.Attribute"] = "#FF0000", ["Xml.Value"] = "#0000FF",
        ["Stream.Error"] = "#FF0000", ["Stream.Warning"] = "#FFFF00",
        ["Stream.Verbose"] = "#FFFF00", ["Stream.Debug"] = "#FFFF00"
    };

    public static bool IsColor(string? value) => value is { Length: 7 } && value[0] == '#' &&
        value.AsSpan(1).ContainsAnyExcept("0123456789abcdefABCDEF".AsSpan()) == false;

    public void Normalize()
    {
        var defaults = DefaultColors();
        Colors ??= [];
        foreach (var (key, value) in defaults)
            if (!Colors.TryGetValue(key, out var color) || !IsColor(color)) Colors[key] = value;
        if (string.IsNullOrWhiteSpace(Name)) Name = "Custom";
    }
}
