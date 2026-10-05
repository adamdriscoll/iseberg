using System.Globalization;
using System.Text;

namespace Iseberg.Core;

// Streaming SGR only: cursor/erase/OSC controls never mutate the protected transcript.
public sealed class AnsiOutputParser
{
    private string pending = "";
    private string? foreground;
    private string? background;
    private bool bold;
    private bool underline;
    private bool inverse;
    public bool UnsupportedControl { get; private set; }
    public void Complete()
    {
        if (pending.Length == 0) return;
        pending = "";
        UnsupportedControl = true;
    }

    private static readonly string[] Palette =
    [
        "#000000", "#800000", "#008000", "#808000", "#000080", "#800080", "#008080", "#C0C0C0",
        "#808080", "#FF0000", "#00FF00", "#FFFF00", "#0000FF", "#FF00FF", "#00FFFF", "#FFFFFF"
    ];

    public static string ConsoleColorHex(ConsoleColor color)
    {
        if (!Enum.IsDefined(color)) throw new ArgumentOutOfRangeException(nameof(color));
        return Palette[(int)color switch
        {
            1 => 4, 3 => 6, 4 => 1, 6 => 3, 9 => 12, 11 => 14, 12 => 9, 14 => 11, var value => value
        }];
    }

    public IReadOnlyList<OutputEntry> Parse(OutputEntry entry)
    {
        var text = pending + entry.Text;
        pending = "";
        var output = new List<OutputEntry>();
        var content = new StringBuilder();
        void Flush()
        {
            if (content.Length == 0) return;
            var fg = foreground ?? entry.Style?.Foreground;
            var bg = background ?? entry.Style?.Background;
            var style = new OutputStyle(fg, bg, bold || entry.Style?.Bold == true, underline || entry.Style?.Underline == true, inverse);
            output.Add(entry with { Text = content.ToString(), Style = style == new OutputStyle() ? null : style });
            content.Clear();
        }
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '\x1b')
            {
                if (text[index] == '\b') UnsupportedControl = true;
                else content.Append(text[index]);
                continue;
            }
            Flush();
            var start = index;
            if (++index >= text.Length) { pending = text[start..]; break; }
            if (text[index] == '[')
            {
                index++;
                while (index < text.Length && text[index] is not (>= '@' and <= '~')) index++;
                if (index >= text.Length)
                {
                    if (text.Length - start <= 4096) pending = text[start..];
                    else UnsupportedControl = true;
                    break;
                }
                if (text[index] == 'm') Apply(text[(start + 2)..index]);
                else UnsupportedControl = true;
            }
            else if (text[index] == ']')
            {
                while (++index < text.Length && text[index] != '\a' &&
                    !(text[index] == '\x1b' && index + 1 < text.Length && text[index + 1] == '\\')) { }
                if (index >= text.Length)
                {
                    if (text.Length - start <= 4096) pending = text[start..];
                    else UnsupportedControl = true;
                    break;
                }
                if (text[index] == '\x1b') index++;
                UnsupportedControl = true;
            }
            else UnsupportedControl = true;
        }
        Flush();
        return output;
    }

    private void Apply(string sequence)
    {
        var parts = sequence.Split(';');
        var codes = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i].Length == 0 ? "0" : parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out codes[i]))
            { UnsupportedControl = true; return; }
        for (var i = 0; i < codes.Length; i++)
        {
            var code = codes[i];
            switch (code)
            {
                case 0: foreground = background = null; bold = underline = inverse = false; break;
                case 1: bold = true; break;
                case 4: underline = true; break;
                case 7: inverse = true; break;
                case 22: bold = false; break;
                case 24: underline = false; break;
                case 27: inverse = false; break;
                case 39: foreground = null; break;
                case 49: background = null; break;
                case >= 30 and <= 37: foreground = Palette[code - 30]; break;
                case >= 40 and <= 47: background = Palette[code - 40]; break;
                case >= 90 and <= 97: foreground = Palette[code - 90 + 8]; break;
                case >= 100 and <= 107: background = Palette[code - 100 + 8]; break;
                case 38 or 48:
                    string? color = null;
                    if (i + 2 < codes.Length && codes[i + 1] == 5 && codes[i + 2] is >= 0 and <= 255)
                    { color = Indexed(codes[i + 2]); i += 2; }
                    else if (i + 4 < codes.Length && codes[i + 1] == 2 && codes.Skip(i + 2).Take(3).All(value => value is >= 0 and <= 255))
                    { color = $"#{codes[i + 2]:X2}{codes[i + 3]:X2}{codes[i + 4]:X2}"; i += 4; }
                    else { UnsupportedControl = true; return; }
                    if (code == 38) foreground = color;
                    else background = color;
                    break;
                default: UnsupportedControl = true; break;
            }
        }
    }

    private static string Indexed(int index)
    {
        if (index < 16) return Palette[index];
        if (index >= 232)
        {
            var shade = 8 + (index - 232) * 10;
            return $"#{shade:X2}{shade:X2}{shade:X2}";
        }
        index -= 16;
        int Channel(int value) => value == 0 ? 0 : 55 + value * 40;
        return $"#{Channel(index / 36):X2}{Channel(index / 6 % 6):X2}{Channel(index % 6):X2}";
    }
}
