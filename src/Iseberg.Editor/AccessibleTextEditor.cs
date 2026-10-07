using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using AvaloniaEdit;

namespace Iseberg.Editor;

public sealed class AccessibleTextEditor : TextEditor
{
    protected override Type StyleKeyOverride => typeof(TextEditor);
    protected override AutomationPeer OnCreateAutomationPeer() => new EditorPeer(this);

    private sealed class EditorPeer : ControlAutomationPeer, IValueProvider
    {
        private readonly AccessibleTextEditor editor;
        private string lastText;
        public EditorPeer(AccessibleTextEditor editor) : base(editor)
        {
            this.editor = editor;
            lastText = editor.Text;
            editor.TextChanged += (_, _) =>
            {
                RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, lastText, editor.Text);
                lastText = editor.Text;
            };
            editor.PropertyChanged += (_, e) =>
            {
                if (e.Property == IsReadOnlyProperty)
                    RaisePropertyChangedEvent(ValuePatternIdentifiers.IsReadOnlyProperty, e.OldValue, e.NewValue);
            };
        }
        public bool IsReadOnly => editor.IsReadOnly;
        public string Value => editor.Text;
        public void SetValue(string? value)
        {
            EnsureEnabled();
            if (IsReadOnly) throw new InvalidOperationException("The editor is read-only.");
            if (!editor.TextArea.ReadOnlySectionProvider.CanInsert(0) ||
                editor.TextArea.ReadOnlySectionProvider.GetDeletableSegments(new AvaloniaEdit.Document.SimpleSegment(0, editor.Document.TextLength))
                    .Sum(segment => segment.Length) != editor.Document.TextLength)
                throw new InvalidOperationException("The editor contains protected text.");
            editor.Document.Replace(0, editor.Document.TextLength, value ?? "");
        }
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Edit;
        protected override string GetClassNameCore() => nameof(TextEditor);
        protected override void SetFocusCore() => editor.TextArea.Focus();
        protected override object? GetProviderCore(Type providerType) =>
            providerType == typeof(IValueProvider) ? this : base.GetProviderCore(providerType);
    }
}
