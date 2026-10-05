using System.Text;
using System.Text.Json.Serialization;

namespace Iseberg.Core;

public sealed record ScriptEncoding(int CodePage, bool EmitBom, [property: JsonIgnore] string DisplayName)
{
    [JsonConstructor]
    public ScriptEncoding(int codePage, bool emitBom)
        : this(codePage, emitBom, Choices.FirstOrDefault(choice =>
            choice.CodePage == codePage && choice.EmitBom == emitBom)?.DisplayName ?? $"Code page {codePage}") { }

    public static IReadOnlyList<ScriptEncoding> Choices { get; } = Array.AsReadOnly<ScriptEncoding>(
    [
        new(65001, false, "UTF-8"),
        new(65001, true, "UTF-8 BOM"),
        new(1200, true, "UTF-16 LE"),
        new(1200, false, "UTF-16 LE (no BOM)"),
        new(1201, true, "UTF-16 BE"),
        new(1201, false, "UTF-16 BE (no BOM)"),
        new(12000, true, "UTF-32 LE"),
        new(12000, false, "UTF-32 LE (no BOM)"),
        new(12001, true, "UTF-32 BE"),
        new(12001, false, "UTF-32 BE (no BOM)"),
        new(1252, false, "Windows-1252"),
        new(28591, false, "ISO-8859-1"),
        new(20127, false, "ASCII"),
        new(437, false, "OEM 437"),
        new(850, false, "OEM 850")
    ]);

    public Encoding CreateEncoding()
    {
        if (CodePage <= 0) throw new ArgumentOutOfRangeException(nameof(CodePage), "Choose an explicit code page.");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return CodePage switch
        {
            65001 => new UTF8Encoding(EmitBom, true),
            1200 => new UnicodeEncoding(false, EmitBom, true),
            1201 => new UnicodeEncoding(true, EmitBom, true),
            12000 => new UTF32Encoding(false, EmitBom, true),
            12001 => new UTF32Encoding(true, EmitBom, true),
            _ when EmitBom => throw new ArgumentException("This encoding does not support a Unicode BOM.", nameof(EmitBom)),
            _ => Encoding.GetEncoding(CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
        };
    }

    internal bool Matches(ScriptEncoding other) => CodePage == other.CodePage && EmitBom == other.EmitBom;
}
