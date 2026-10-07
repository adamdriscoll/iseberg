using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using Iseberg.Editor;

namespace EditorHost;

internal static class Program
{
    public static bool Smoke { get; private set; }
    [STAThread]
    public static int Main(string[] args)
    {
        Smoke = args.Contains("--smoke");
        return AppBuilder.Configure<HostApplication>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
    }
}

public sealed class HostApplication : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new HostWindow();
        base.OnFrameworkInitializationCompleted();
    }
}

public sealed class HostWindow : Window
{
    private readonly TextDocument document = new("$value = 42\n$value");
    private readonly PowerShellEditorControl editor;
    private readonly TextBlock status = new() { Text = "Diagnostics unavailable (no host analysis provider)." };

    public HostWindow()
    {
        editor = new PowerShellEditorControl(document);
        Title = "Iseberg editor - host-owned document (no execution)";
        Width = 900;
        Height = 600;
        var capture = new Button { Content = "Capture text (does not run)" };
        AutomationProperties.SetName(capture, "Capture script text without executing");
        capture.Click += (_, _) =>
        {
            var snapshot = editor.CaptureText();
            status.Text = $"Captured {snapshot.Text.Length} UTF-16 characters at version {snapshot.Version}.";
            // A real host passes this snapshot to its execution seam.
        };
        editor.AnalysisChanged += (_, _) => status.Text = editor.Analysis.State == EditorAnalysisState.Available
            ? $"{editor.Analysis.Diagnostics.Length} host diagnostics, version {editor.Analysis.Version}."
            : $"Diagnostics: {editor.Analysis.State} (no built-in parser).";
        editor.ErrorOccurred += (_, error) => status.Text = error.Exception.Message;
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        grid.Children.Add(capture);
        Grid.SetRow(editor, 1);
        grid.Children.Add(editor);
        Grid.SetRow(status, 2);
        grid.Children.Add(status);
        Content = grid;
        Closed += (_, _) => editor.Dispose();
        if (Program.Smoke) Opened += (_, _) => Dispatcher.UIThread.Post(RunSmoke);
    }

    private async void RunSmoke()
    {
        try
        {
            await Task.Delay(150);
            var inner = editor.GetVisualDescendants().OfType<TextEditor>().Single();
            Check(inner.Bounds.Width > 0 && inner.Bounds.Height > 0, "Native Fluent layout");
            Check(editor.FocusEditor(), "Editor focus");
            editor.CaretOffset = document.TextLength;
            inner.TextArea.PerformTextInput("\n'edited'");
            Check(document.Text.EndsWith("'edited'"), "Native text input path");
            Check((await editor.AnalyzeAsync()).State == EditorAnalysisState.Unavailable, "Explicit unavailable diagnostics");
            editor.Select(new(0, 6));
            Check(editor.CaptureText(EditorTextScope.SelectionOrCurrentLine).Text == "$value", "Selection snapshot");
            var peer = ControlAutomationPeer.CreatePeerForElement(inner)!;
            var value = peer.GetProvider<IValueProvider>()!;
            Check(peer.GetAutomationControlType() == AutomationControlType.Edit, "Edit automation role");
            Check(AutomationProperties.GetName(inner) == "PowerShell script editor", "Automation label");
            editor.IsReadOnly = true;
            inner.TextArea.PerformTextInput("must not insert");
            Check(document.Text.StartsWith("$value"), "Read-only keyboard path");
            Check(value.IsReadOnly, "Read-only automation");
            editor.IsReadOnly = false;
            editor.Select(new(document.TextLength, 0));
            inner.TextArea.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Z, KeyModifiers = KeyModifiers.Control });
            Check(!document.Text.EndsWith("'edited'"), "Undo keyboard gesture");
            document.Text = "Get";
            editor.CaretOffset = 3;
            editor.CompletionProvider = new SmokeCompletionProvider();
            inner.TextArea.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space, KeyModifiers = KeyModifiers.Control });
            await Task.Delay(100);
            inner.TextArea.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Check(document.Text == "Get-Date", "Native completion popup and acceptance");
            var parent = (Grid)editor.Parent!;
            parent.Children.Remove(editor);
            Check(inner.Document is not null && inner.Document != document && inner.Document.TextLength == 0,
                "Detach releases host document with IME-safe empty document");
            document.Insert(0, "# detached edit\n");
            parent.Children.Add(editor);
            Check((await editor.AnalyzeAsync()).State == EditorAnalysisState.Unavailable, "Reattach diagnostics state");
            foreach (var text in new[] { "configuration Example { Node localhost { } }", "if (", "$x = 1" })
            {
                document.Text = text;
                Check((await editor.AnalyzeAsync()).State == EditorAnalysisState.Unavailable, "No implicit parser for DSC/invalid/ordinary text");
                await Task.Delay(50);
            }
            CheckImeTabLifecycle();
            editor.Dispose();
            document.Insert(0, "# remains host owned\n");
            Check(AppDomain.CurrentDomain.GetAssemblies().All(assembly => assembly.GetName().Name is not ("Iseberg" or "Iseberg.Core" or "System.Management.Automation")),
                "No workbench/Core/PowerShell runtime loaded");
            Console.WriteLine("PASS: native Windows Fluent layout/input/undo/completion/automation/detach/reattach/DSC/focused IME tab lifecycle; no PowerShell runtime loaded, diagnostics explicitly unavailable.");
            Close();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            if (Application.Current!.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown(1);
        }
    }

    private void CheckImeTabLifecycle()
    {
        document.Text = "first\nsecond";
        using var other = new PowerShellEditorControl(new TextDocument("other\neditor"));
        ((Grid)editor.Parent!).Children.Remove(editor);
        var firstTab = new TabItem { Header = "First", Content = editor };
        var secondTab = new TabItem { Header = "Second", Content = other };
        var tabs = new TabControl { Items = { firstTab, secondTab }, SelectedItem = firstTab };
        Content = tabs;
        UpdateLayout();
        for (var iteration = 0; iteration < 10; iteration++)
        {
            Check(editor.FocusEditor(), "Focused IME editor");
            editor.CaretOffset = document.TextLength;
            var request = new TextInputMethodClientRequestedEventArgs { RoutedEvent = InputElement.TextInputMethodClientRequestedEvent };
            editor.TextEditor.TextArea.RaiseEvent(request);
            var client = request.Client ?? throw new InvalidOperationException("Native editor IME client unavailable.");
            var queries = 0;
            void Query(object? sender, EventArgs args)
            {
                _ = client.SurroundingText;
                _ = client.Selection;
                queries++;
            }
            client.SurroundingTextChanged += Query;
            try
            {
                tabs.SelectedItem = secondTab;
                UpdateLayout();
                Check(queries > 0, "Native IME transition callbacks");
                Check(editor.TextEditor.Document != document && editor.TextEditor.Document.TextLength == 0,
                    "Private empty detached document");
                tabs.SelectedItem = firstTab;
                UpdateLayout();
                Check(editor.Document == document && editor.TextEditor.Document == document && editor.CaretOffset == document.TextLength,
                    "IME-safe host retention and caret restoration");
                Check(editor.FocusEditor(), "Reattached IME focus");
                Check(client.SurroundingText == "second", "Reattached IME surrounding text");
            }
            finally { client.SurroundingTextChanged -= Query; }
        }
        Check(document.Text == "first\nsecond", "Host text survives focused tab lifecycle");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("Smoke failed: " + name);
    }

    private sealed class SmokeCompletionProvider : IEditorCompletionProvider
    {
        public Task<EditorCompletionList> CompleteAsync(EditorCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new EditorCompletionList(request.Version, new(0, 3), [new("Get-Date", "Get-Date", "Host-supplied static result")]));
    }
}
