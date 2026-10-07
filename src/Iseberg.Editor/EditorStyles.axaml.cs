using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace Iseberg.Editor;

/// <summary>AvaloniaEdit templates with dynamically resolved, control-scoped Fluent colors.</summary>
public sealed partial class EditorStyles : Styles
{
    public EditorStyles() => AvaloniaXamlLoader.Load(this);
}
