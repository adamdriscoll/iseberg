using System.Collections;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using Iseberg.Core;
using SessionState = Iseberg.Core.SessionState;

namespace Iseberg;

public sealed partial class WorkbenchControl
{
    private T IseInvoke<T>(Func<T> operation)
    {
        T Invoke()
        {
            if (windowClosed || closingInProgress) throw new ObjectDisposedException(nameof(WorkbenchControl));
            return operation();
        }
        return Dispatcher.UIThread.CheckAccess() ? Invoke() : Dispatcher.UIThread.InvokeAsync(Invoke).GetAwaiter().GetResult();
    }

    private void RefreshIseMenus()
    {
        if (AddonsMenu is null || Scripting is null) return;
        foreach (var item in AddonsMenu.Items.OfType<MenuItem>().Where(item => item.Tag is IseMenuItem).ToArray())
            AddonsMenu.Items.Remove(item);
        if (Workbench.SelectedSession is not { } selected) return;
        foreach (var item in Scripting.Tab(selected).AddOnsMenu.Submenus.Items)
            AddonsMenu.Items.Add(CreateIseMenu(item));
    }

    private MenuItem CreateIseMenu(IseMenuItem item)
    {
        var menu = new MenuItem { Header = item.Name, InputGesture = item.Gesture, Tag = item };
        AutomationProperties.SetName(menu, item.Name.Replace("_", ""));
        menu.IsEnabled = item.Action is null || item.Tab.Model.Engine.State == SessionState.Ready && !item.Tab.Model.Engine.IsRemote;
        foreach (var child in item.Submenus.Items) menu.Items.Add(CreateIseMenu(child));
        menu.Click += async (_, e) =>
        {
            e.Handled = true;
            if (item.Action is not null) await GuardAsync(() => InvokeIseMenuAsync(item));
        };
        return menu;
    }

    private static void UpdateIseMenuState(MenuItem parent)
    {
        foreach (var menu in parent.Items.OfType<MenuItem>())
        {
            if (menu.Tag is IseMenuItem item)
                menu.IsEnabled = item.Action is null || item.Tab.Model.Engine.State == SessionState.Ready && !item.Tab.Model.Engine.IsRemote;
            UpdateIseMenuState(menu);
        }
    }

    private async Task InvokeIseMenuAsync(IseMenuItem item)
    {
        item.Tab.Check();
        if (!item.Tab.AddOnsMenu.Descendants().Contains(item))
            throw new InvalidOperationException("This Add-ons menu item has been removed.");
        if (item.Action is null) throw new InvalidOperationException("This menu is a submenu container, not an action.");
        await item.Tab.Model.Engine.ExecuteMenuActionAsync(item.Action);
        await RefreshDebuggerAsync(item.Tab.Model, reconcile: true);
        FlushOutput();
    }

    private async Task<bool> HandleIseShortcutAsync(KeyEventArgs e)
    {
        if (Workbench.SelectedSession is not { } session) return false;
        var item = Scripting.Tab(session).AddOnsMenu.Descendants().FirstOrDefault(item => item.Gesture?.Matches(e) == true);
        if (item is null) return false;
        e.Handled = true;
        await GuardAsync(() => InvokeIseMenuAsync(item));
        return true;
    }

    private bool IsIseShortcutReserved(KeyGesture gesture)
    {
        if (gesture.Key is Key.F1 or Key.F5 or Key.F6 or Key.F8 or Key.F9 or Key.F10 or Key.F11)
            return true;
        var control = (gesture.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        return ActionMenus().Any(item => item.Tag is string && item.InputGesture is { } existing &&
            (existing.Equals(gesture) || control && existing.Key == gesture.Key &&
                existing.KeyModifiers.HasFlag(KeyModifiers.Control))) ||
            control && gesture.Key is Key.Tab or Key.C or Key.V or Key.X or Key.Z or Key.Y or Key.Add or Key.Subtract;
    }

    public sealed class IseObjectModel
    {
        internal WorkbenchControl Owner { get; }
        private readonly Dictionary<SessionModel, IsePowerShellTab> tabs = [];
        internal IseObjectModel(WorkbenchControl owner)
        {
            Owner = owner;
            PowerShellTabs = new(this);
        }
        internal IsePowerShellTab Tab(SessionModel model)
        {
            if (!tabs.TryGetValue(model, out var tab)) tabs.Add(model, tab = new(Owner, model));
            return tab;
        }
        internal void Remove(SessionModel model) => tabs.Remove(model);
        public IsePowerShellTab CurrentPowerShellTab => Owner.IseInvoke(() =>
            Tab(Owner.Workbench.SelectedSession ?? throw new InvalidOperationException("No PowerShell tab is selected.")));
        public IseFile? CurrentFile => CurrentPowerShellTab.SelectedFile;
        public IseEditor? CurrentEditor => CurrentFile?.Editor;
        public IsePowerShellTabCollection PowerShellTabs { get; }
    }

    public abstract class IseCollection<T> : IReadOnlyList<T>
    {
        protected abstract T[] Snapshot();
        public int Count => Snapshot().Length;
        public T this[int index] => Snapshot()[index];
        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)Snapshot()).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public sealed class IsePowerShellTabCollection : IseCollection<IsePowerShellTab>
    {
        private readonly IseObjectModel root;
        internal IsePowerShellTabCollection(IseObjectModel root) => this.root = root;
        protected override IsePowerShellTab[] Snapshot() => root.Owner.IseInvoke(() =>
            root.Owner.Workbench.Sessions.Select(root.Tab).ToArray());
        public void SetSelectedPowerShellTab(IsePowerShellTab tab) => root.Owner.IseInvoke(() =>
        {
            if (tab.Owner != root.Owner) throw new ArgumentException("The PowerShell tab belongs to another window.", nameof(tab));
            tab.Activate();
            return true;
        });
        public IsePowerShellTab Add() => throw new PSNotSupportedException("Creating PowerShell tabs from scripts is not supported. Use File > New PowerShell Tab.");
        public void Remove(IsePowerShellTab tab) => throw new PSNotSupportedException("Closing PowerShell tabs from scripts is not supported. Use File > Close PowerShell Tab.");
    }

    public sealed class IsePowerShellTab
    {
        internal WorkbenchControl Owner { get; }
        internal SessionModel Model { get; }
        private readonly Dictionary<ScriptTab, IseFile> files = [];
        internal IsePowerShellTab(WorkbenchControl owner, SessionModel model)
        {
            Owner = owner;
            Model = model;
            Files = new(this);
            AddOnsMenu = new(this, "Add-ons", null, null);
            Snippets = new(this);
        }
        internal void Check()
        {
            if (!Owner.Workbench.Sessions.Contains(Model) || Model.Engine.State == SessionState.Disposed)
                throw new ObjectDisposedException("PowerShell tab");
        }
        internal T Read<T>(Func<T> read) => Owner.IseInvoke(() => { Check(); return read(); });
        internal IseFile File(ScriptTab model)
        {
            if (!files.TryGetValue(model, out var file)) files.Add(model, file = new(this, model));
            return file;
        }
        internal void Forget(ScriptTab model) => files.Remove(model);
        internal void Activate()
        {
            Check();
            Owner.Workbench.SelectedSession = Model;
            Owner.DisplaySession();
        }
        public string DisplayName => Read(() => Model.DisplayName);
        public string Prompt => Read(() => Model.Engine.Prompt);
        public bool CanInvoke => Read(() => Model.Engine.State == SessionState.Ready && !Model.Engine.IsRemote);
        public IseFile? SelectedFile => Read(() => Model.SelectedFile is { } file ? File(file) : null);
        public IseFileCollection Files { get; }
        public IseMenuItem AddOnsMenu { get; }
        public IseSnippetCollection Snippets { get; }
        public IseUnsupportedTools VerticalAddOnTools { get; } = new();
        public IseUnsupportedTools HorizontalAddOnTools { get; } = new();
        public void Invoke(string script) => throw new PSNotSupportedException("Cross-tab script invocation is not supported. Run the command in its owning PowerShell tab.");
        public object InvokeSynchronous(string script) => throw new PSNotSupportedException("Cross-tab synchronous invocation is not supported.");
    }

    public sealed class IseSnippetCollection : IseCollection<PowerShellSnippet>
    {
        private readonly IsePowerShellTab tab;
        internal IseSnippetCollection(IsePowerShellTab tab) => this.tab = tab;
        protected override PowerShellSnippet[] Snapshot()
        {
            var (service, includeDefaults) = tab.Read(() => (tab.Model.Engine.Snippets, tab.Owner.settings.UseDefaultSnippets));
            var result = Task.Run(service.LoadAsync).GetAwaiter().GetResult();
            if (result.Errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, result.Errors));
            return (includeDefaults ? SnippetCatalog.BuiltIns : []).Concat(result.Snippets).Distinct().ToArray();
        }
        public void Load(string fullPath)
        {
            if (!Path.IsPathFullyQualified(fullPath)) throw new ArgumentException("Snippets.Load requires a fully qualified local path.", nameof(fullPath));
            tab.Read(() => tab.Model.Engine.Snippets).Import(fullPath, false);
        }
    }

    public sealed class IseFileCollection : IseCollection<IseFile>
    {
        private readonly IsePowerShellTab tab;
        internal IseFileCollection(IsePowerShellTab tab) => this.tab = tab;
        protected override IseFile[] Snapshot() => tab.Read(() => tab.Model.Files.Select(tab.File).ToArray());
        public IseFile Add() => tab.Read(() =>
        {
            tab.Activate();
            tab.Owner.NewFile();
            return tab.File(tab.Model.SelectedFile!);
        });
        public IseFile Add(string fullPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
            if (!Path.IsPathFullyQualified(fullPath)) throw new ArgumentException("Files.Add requires a fully qualified local path.", nameof(fullPath));
            var file = ScriptFile.FromBytes(fullPath, System.IO.File.ReadAllBytes(fullPath));
            return tab.Read(() =>
            {
                if (tab.Model.Engine.IsRemote) throw new PSNotSupportedException("Files.Add does not open files in a remote runspace.");
                var existing = tab.Model.Files.FirstOrDefault(candidate => !candidate.File.IsRemote && SameScript(candidate.File.Path, file.Path));
                var model = existing ?? new ScriptTab(file);
                if (existing is null) tab.Model.Files.Add(model);
                SetSelectedFile(tab.File(model));
                return tab.File(model);
            });
        }
        public void SetSelectedFile(IseFile file) => tab.Read(() =>
        {
            if (file.Tab != tab) throw new ArgumentException("The file belongs to another PowerShell tab.", nameof(file));
            file.Check();
            tab.Activate();
            tab.Model.SelectedFile = file.Model;
            tab.Owner.DisplayFile();
            return true;
        });
        public void Remove(IseFile file) => Remove(file, false);
        public void Remove(IseFile file, bool force) => tab.Read(() =>
        {
            if (file.Tab != tab) throw new ArgumentException("The file belongs to another PowerShell tab.", nameof(file));
            file.Check();
            if (file.Model.File.IsDirty && !force) throw new InvalidOperationException("Save the file or use Remove(file, true) to discard edits.");
            if (tab.Model.Engine.State is SessionState.Debugging or SessionState.NestedPrompt ||
                file.Model.LineBreakpoints.Count > 0 || tab.Owner.autoSaving ||
                file.Model.File.Path is not null && tab.Model.Engine.State == SessionState.Running)
                throw new PSNotSupportedException("Close this file through the UI after stopping execution; breakpoint/recovery work is pending.");
            // Never acquire the executing runspace's gate to close a file from inside its script.
            tab.Owner.RemoveRecovery(file.Model.RecoveryId);
            tab.Model.Files.Remove(file.Model);
            tab.Forget(file.Model);
            if (tab.Model.SelectedFile == file.Model) tab.Model.SelectedFile = tab.Model.Files.LastOrDefault();
            if (tab.Owner.Workbench.SelectedSession == tab.Model) tab.Owner.DisplayFile();
            return true;
        });
    }

    public sealed class IseFile
    {
        internal IsePowerShellTab Tab { get; }
        internal ScriptTab Model { get; }
        internal IseFile(IsePowerShellTab tab, ScriptTab model) { Tab = tab; Model = model; Editor = new(this); }
        internal void Check()
        {
            Tab.Check();
            if (!Tab.Model.Files.Contains(Model)) throw new ObjectDisposedException("ISE file");
        }
        internal T Read<T>(Func<T> operation) => Tab.Read(() => { Check(); return operation(); });
        internal void CanEdit()
        {
            Check();
            if (Tab.Owner.closingInProgress || Tab.Model.Engine.State == SessionState.Debugging)
                throw new InvalidOperationException("The script editor is read-only while debugging or closing.");
            if (Model.File.IsRemote) throw new PSNotSupportedException("ISE file scripting supports local documents only.");
        }
        public string DisplayName => Read(() => Model.File.Title);
        public string? FullPath => Read(() => Model.File.Path);
        public bool IsUntitled => Read(() => Model.File.Path is null);
        public bool IsSaved => Read(() => !Model.File.IsDirty);
        public Encoding Encoding => Read(() => Model.File.EncodingChoice.CreateEncoding());
        public IseEditor Editor { get; }
        public void Save() => SaveAs(FullPath ?? throw new InvalidOperationException("Use SaveAs with a fully qualified path for an untitled file."));
        public void Save(Encoding encoding) => SaveAs(FullPath ?? throw new InvalidOperationException("Use SaveAs for an untitled file."), encoding);
        public void SaveAs(string fullPath) => SaveAsCore(fullPath, null);
        public void SaveAs(string fullPath, Encoding encoding) => SaveAsCore(fullPath, encoding);
        private void SaveAsCore(string fullPath, Encoding? encoding)
        {
            if (!Path.IsPathFullyQualified(fullPath)) throw new ArgumentException("SaveAs requires a fully qualified local path.", nameof(fullPath));
            if (Dispatcher.UIThread.CheckAccess()) throw new InvalidOperationException("Call synchronous ISE Save methods from a PowerShell script, not the UI thread.");
            Read(() =>
            {
                CanEdit();
                if (Tab.Model.Files.Any(other => other != Model && !other.File.IsRemote && SameScript(other.File.Path, fullPath)))
                    throw new InvalidOperationException("The destination is already open in this PowerShell tab.");
                if (encoding is not null) Model.File.SetEncoding(new(encoding.CodePage, encoding.GetPreamble().Length > 0));
                return Model.File.SaveAsync(fullPath);
            }).GetAwaiter().GetResult();
        }
    }

    public sealed class IseEditor
    {
        private readonly IseFile file;
        internal IseEditor(IseFile file) => this.file = file;
        private TextDocument Document => file.Model.Document;
        private TextEditor Activate()
        {
            file.Tab.Files.SetSelectedFile(file);
            return file.Tab.Owner.ScriptEditor;
        }
        private int Caret => file.Tab.Owner.displayedFile == file.Model
            ? file.Tab.Owner.ScriptEditor.CaretOffset : Math.Min(file.Model.File.CaretOffset, Document.TextLength);
        public string Text
        {
            get => file.Read(() => Document.Text);
            set => file.Read(() => { file.CanEdit(); ArgumentNullException.ThrowIfNull(value); Document.Text = value; return true; });
        }
        public int LineCount => file.Read(() => Document.LineCount);
        public int CaretLine => file.Read(() => Document.GetLocation(Caret).Line);
        public int CaretColumn => file.Read(() => Document.GetLocation(Caret).Column);
        public string CaretLineText => file.Read(() => Document.GetText(Document.GetLineByOffset(Caret)));
        public string SelectedText => file.Read(() => file.Tab.Owner.displayedFile == file.Model ? file.Tab.Owner.ScriptEditor.SelectedText : "");
        public int GetLineLength(int lineNumber) => file.Read(() => Document.GetLineByNumber(lineNumber).Length);
        private int Offset(int line, int column)
        {
            var documentLine = Document.GetLineByNumber(line);
            if (column < 1 || column > documentLine.Length + 1) throw new ArgumentOutOfRangeException(nameof(column));
            return documentLine.Offset + column - 1;
        }
        public void SetCaretPosition(int lineNumber, int columnNumber) => file.Read(() =>
        {
            var offset = Offset(lineNumber, columnNumber);
            var editor = Activate();
            editor.Select(offset, 0);
            editor.CaretOffset = offset;
            return true;
        });
        public void Select(int startLine, int startColumn, int endLine, int endColumn) => file.Read(() =>
        {
            var start = Offset(startLine, startColumn);
            var end = Offset(endLine, endColumn);
            if (end < start) throw new ArgumentException("The selection end must not precede its start.");
            var editor = Activate();
            editor.Select(start, end - start);
            editor.CaretOffset = end;
            return true;
        });
        public void SelectCaretLine() => file.Read(() =>
        {
            var line = Document.GetLineByOffset(Caret);
            Activate().Select(line.Offset, line.Length);
            return true;
        });
        public void InsertText(string text) => file.Read(() =>
        {
            ArgumentNullException.ThrowIfNull(text);
            file.CanEdit();
            var editor = Activate();
            var start = editor.SelectionLength > 0 ? editor.SelectionStart : editor.CaretOffset;
            Document.Replace(start, editor.SelectionLength, text);
            editor.Select(start + text.Length, 0);
            editor.CaretOffset = start + text.Length;
            return true;
        });
        public void Clear() => Text = "";
        public void Focus() => file.Read(() => Activate().TextArea.Focus());
        public void EnsureVisible(int lineNumber) => file.Read(() =>
        {
            _ = Document.GetLineByNumber(lineNumber);
            Activate().ScrollToLine(lineNumber);
            return true;
        });
    }

    public sealed class IseMenuItem
    {
        internal IsePowerShellTab Tab { get; }
        internal string Name { get; }
        internal ScriptBlock? Action { get; }
        internal KeyGesture? Gesture { get; }
        internal IseMenuItem(IsePowerShellTab tab, string name, ScriptBlock? action, KeyGesture? gesture)
        {
            Tab = tab; Name = name; Action = action; Gesture = gesture; Submenus = new(this);
        }
        public string DisplayName => Tab.Read(() => Name);
        public IseMenuItemCollection Submenus { get; }
        internal IEnumerable<IseMenuItem> Descendants() => Submenus.Items.SelectMany(child => new[] { child }.Concat(child.Descendants()));
    }

    public sealed class IseMenuItemCollection : IseCollection<IseMenuItem>
    {
        private readonly IseMenuItem parent;
        internal List<IseMenuItem> Items { get; } = [];
        internal IseMenuItemCollection(IseMenuItem parent) => this.parent = parent;
        private void Check()
        {
            if (parent != parent.Tab.AddOnsMenu && !parent.Tab.AddOnsMenu.Descendants().Contains(parent))
                throw new ObjectDisposedException("ISE menu");
        }
        protected override IseMenuItem[] Snapshot() => parent.Tab.Read(() => { Check(); return Items.ToArray(); });
        public IseMenuItem Add(string displayName, ScriptBlock? action, string? shortcut)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
            var gesture = string.IsNullOrWhiteSpace(shortcut) ? null : KeyGesture.Parse(shortcut);
            if (action is null && gesture is not null) throw new ArgumentException("A submenu container cannot have a shortcut.");
            var caller = Runspace.DefaultRunspace?.InstanceId;
            return parent.Tab.Read(() =>
            {
                Check();
                if (caller != parent.Tab.Model.Engine.LocalRunspaceId)
                    throw new PSNotSupportedException("Register menu actions from their owning local PowerShell tab.");
                if (parent.Action is not null) throw new InvalidOperationException("An action menu cannot contain submenus.");
                if (Items.Any(item => item.Name == displayName)) throw new ArgumentException("A sibling menu already has this display name.");
                if (gesture is not null && (parent.Tab.Owner.IsIseShortcutReserved(gesture) ||
                    parent.Tab.AddOnsMenu.Descendants().Any(item => item.Gesture?.Equals(gesture) == true)))
                    throw new ArgumentException("This shortcut is already reserved by the workbench or this tab.");
                var item = new IseMenuItem(parent.Tab, displayName, action, gesture);
                Items.Add(item);
                parent.Tab.Owner.RefreshIseMenus();
                return item;
            });
        }
        public void Clear() => parent.Tab.Read(() => { Check(); Items.Clear(); parent.Tab.Owner.RefreshIseMenus(); return true; });
        public bool Remove(IseMenuItem item) => parent.Tab.Read(() =>
        {
            Check();
            var removed = Items.Remove(item);
            parent.Tab.Owner.RefreshIseMenus();
            return removed;
        });
    }

    public sealed class IseUnsupportedTools
    {
        public object Add(string name, Type controlType) => throw WpfNotSupported();
        public object Add(string name, Type controlType, bool isVisible) => throw WpfNotSupported();
        public void Clear() => throw WpfNotSupported();
    }

    private static PSNotSupportedException WpfNotSupported() => new(
        "WPF ISE add-on tools are not supported by Iseberg's Avalonia host, including on Windows. " +
        "Use script-based AddOnsMenu actions; WPF controls cannot run natively on Linux or macOS.");
}
