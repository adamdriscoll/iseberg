using Avalonia.Controls;
using Avalonia.Styling;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Editing;

namespace Iseberg.Editor;

/// <summary>Shared styled completion UI for script editors and host-owned protected consoles. No provider or engine calls.</summary>
public static class EditorCompletionPopup
{
    public static CompletionWindow Show(TextArea textArea, EditorTextSpan span, IEnumerable<ICompletionData> items,
        bool acceptOnEnter = true, string prefix = "")
    {
        Avalonia.Threading.Dispatcher.UIThread.VerifyAccess();
        ArgumentNullException.ThrowIfNull(textArea);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(prefix);
        span.Validate(textArea.Document.TextLength);
        var window = new CompletionWindow(textArea)
        {
            StartOffset = span.Start, EndOffset = span.End, CloseWhenCaretAtBeginning = false
        };
        try
        {
            var styles = Styles();
            var baseResources = styles.Resources.MergedDictionaries.Single() as IResourceDictionary
                ?? throw new InvalidOperationException("The editor template resources are unavailable.");
            foreach (var key in new[] { typeof(ListBox), typeof(ListBoxItem) })
            {
                if (!textArea.TryFindResource(key, out var resource))
                    throw new InvalidOperationException($"The host theme does not provide {key.Name}.");
                baseResources[key] = resource;
            }
            window.CompletionList.Styles.Add(styles);
            if (!styles.TryGetResource(typeof(CompletionList), textArea.ActualThemeVariant, out var theme) ||
                theme is not ControlTheme completionTheme)
                throw new InvalidOperationException("The editor completion theme is unavailable.");
            window.CompletionList.Theme = completionTheme;
            foreach (var item in items)
            {
                ArgumentNullException.ThrowIfNull(item);
                window.CompletionList.CompletionData.Add(item);
            }
            if (!acceptOnEnter) window.CompletionList.CompletionAcceptKeys = [Avalonia.Input.Key.Tab];
            window.Show();
            window.CompletionList.ApplyTemplate();
            window.CompletionList.SelectItem(prefix);
            return window;
        }
        catch
        {
            window.Close();
            throw;
        }
    }

    internal static EditorStyles Styles() => new();
}
