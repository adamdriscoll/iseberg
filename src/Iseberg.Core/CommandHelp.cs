using System.Collections;
using System.Management.Automation;
using System.Text;

namespace Iseberg.Core;

public enum HelpSectionKind { Synopsis, Syntax, Description, Parameters, Inputs, Outputs, Notes, Examples, RelatedLinks, Remarks }

public sealed class HelpViewSettings
{
    public List<HelpSectionKind> Sections { get; set; } = [.. Enum.GetValues<HelpSectionKind>()];
    public bool MatchCase { get; set; }
    public bool WholeWord { get; set; }
    public double Zoom { get; set; } = 100;

    public HelpViewSettings Copy() => new() { Sections = [.. Sections], MatchCase = MatchCase, WholeWord = WholeWord, Zoom = Zoom };

    public void Normalize()
    {
        Sections ??= [.. Enum.GetValues<HelpSectionKind>()];
        if (Sections.Any(section => !Enum.IsDefined(section))) throw new InvalidDataException("An unknown help section was saved.");
        Sections = Sections.Distinct().ToList();
        Zoom = double.IsFinite(Zoom) ? Math.Clamp(Zoom, 20, 400) : 100;
    }
}

public sealed record CommandHelpSection(HelpSectionKind Kind, string Text);

public sealed record CommandHelpDocument(string Name, IReadOnlyList<CommandHelpSection> Sections)
{
    public string ToText() => string.Join("\n\n", Sections.Select(section => section.Kind + "\n" + section.Text));

    public static CommandHelpDocument FromHelp(string name, IEnumerable<PSObject> help)
    {
        var sections = new Dictionary<HelpSectionKind, List<string>>();
        void Add(HelpSectionKind kind, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (!sections.TryGetValue(kind, out var list)) sections.Add(kind, list = []);
            list.Add(text.Trim());
        }
        foreach (var entry in help)
        {
            if (entry.BaseObject is string topic)
            {
                Add(HelpSectionKind.Remarks, topic);
                continue;
            }
            Add(HelpSectionKind.Synopsis, Text(Property(entry, "synopsis")));
            Add(HelpSectionKind.Description, Text(Property(entry, "description")));
            Add(HelpSectionKind.Remarks, Text(Property(entry, "remarks")));
            foreach (var syntax in Items(Property(Property(entry, "syntax"), "syntaxItem")))
            {
                var builder = new StringBuilder(Text(Property(syntax, "name")));
                foreach (var parameter in Items(Property(syntax, "parameter")))
                {
                    var required = Text(Property(parameter, "required")).Equals("true", StringComparison.OrdinalIgnoreCase);
                    var value = Text(Property(parameter, "parameterValue"));
                    var argument = "-" + Text(Property(parameter, "name")) + (value.Length == 0 ? "" : " <" + value + ">");
                    builder.Append(' ').Append(required ? argument : "[" + argument + "]");
                }
                Add(HelpSectionKind.Syntax, builder.ToString());
            }
            foreach (var parameter in Items(Property(Property(entry, "parameters"), "parameter")))
            {
                var type = Text(Property(Property(parameter, "type"), "name"));
                var builder = new StringBuilder("-" + Text(Property(parameter, "name")) + (type.Length == 0 ? "" : " <" + type + ">"));
                var description = Text(Property(parameter, "description"));
                if (description.Length > 0) builder.Append("\n\n    ").Append(description.Replace("\n", "\n    "));
                foreach (var (property, label) in new[]
                {
                    ("required", "Required?"), ("position", "Position?"), ("defaultValue", "Default value"),
                    ("pipelineInput", "Accept pipeline input?"), ("globbing", "Accept wildcard characters?")
                })
                {
                    var value = Text(Property(parameter, property));
                    if (value.Length > 0) builder.Append("\n    ").Append(label.PadRight(28)).Append(value);
                }
                Add(HelpSectionKind.Parameters, builder.ToString());
            }
            foreach (var (kind, container, child) in new[]
            {
                (HelpSectionKind.Inputs, "inputTypes", "inputType"), (HelpSectionKind.Outputs, "returnValues", "returnValue")
            })
                foreach (var item in Items(Property(Property(entry, container), child)))
                    Add(kind, Join(Text(Property(Property(item, "type"), "name")), Text(Property(item, "description"))));
            Add(HelpSectionKind.Notes, Text(Property(Property(entry, "alertSet"), "alert")));
            foreach (var example in Items(Property(Property(entry, "examples"), "example")))
                Add(HelpSectionKind.Examples, Join(Text(Property(example, "title")), Text(Property(example, "introduction")),
                    Text(Property(example, "code")), Text(Property(example, "remarks"))));
            foreach (var link in Items(Property(Property(entry, "relatedLinks"), "navigationLink")))
                Add(HelpSectionKind.RelatedLinks, Join(Text(Property(link, "linkText")), Text(Property(link, "uri"))));
        }
        if (sections.Count == 0) throw new InvalidDataException($"Command '{name}' did not provide readable help content.");
        return new(name, Enum.GetValues<HelpSectionKind>().Where(sections.ContainsKey)
            .Select(kind => new CommandHelpSection(kind, string.Join("\n\n", sections[kind]))).ToArray());
    }

    private static string Join(params string[] values) => string.Join("\n", values.Where(value => value.Length > 0));

    private static object? Property(object? value, string name) =>
        value is null ? null : PSObject.AsPSObject(value).Properties[name]?.Value;

    private static IEnumerable<object> Items(object? value)
    {
        if (value is PSObject { BaseObject: IEnumerable and not string } wrapper) value = wrapper.BaseObject;
        if (value is null) yield break;
        if (value is IEnumerable sequence and not string)
        {
            foreach (var item in sequence) if (item is not null) yield return item;
        }
        else yield return value;
    }

    private static string Text(object? value)
    {
        if (value is null) return "";
        if (value is PSObject wrapper && wrapper.BaseObject is string text) return text;
        if (value is string literal) return literal;
        if (Property(value, "Text") is { } content) return Text(content);
        if (value is IEnumerable sequence)
            return string.Join("\n", sequence.Cast<object?>().Select(Text).Where(item => item.Length > 0));
        return value.ToString() ?? "";
    }
}
