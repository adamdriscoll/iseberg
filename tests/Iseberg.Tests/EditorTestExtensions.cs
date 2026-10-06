using Avalonia.Controls;
using AvaloniaEdit;

namespace Iseberg.Tests;

internal static class EditorTestExtensions
{
    public static TextEditor? FindEditor(this Control control, string name) =>
        control.FindControl<PowerShellEditorControl>(name + "Control")?.TextEditor ?? control.FindControl<TextEditor>(name);
}
