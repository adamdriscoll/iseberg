using System.Collections.Immutable;
using System.Management.Automation.Runspaces;
using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Input.TextInput;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Search;
using Iseberg.Editor;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(Iseberg.Editor.Tests.EditorTestApplication))]

namespace Iseberg.Editor.Tests;

public sealed class EditorTestApplication : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<EditorTestApplication>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class EditorTests
{
    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void NativeImeClientCanQuerySurroundingTextDuringDetachAndDispose(bool dispose, bool moveFocusFirst)
    {
        var document = new TextDocument("first\nsecond");
        using var control = new PowerShellEditorControl(document);
        var other = new TextBox { Text = "host focus target" };
        var window = new Window { Content = new StackPanel { Children = { control, other } }, Width = 800, Height = 500 };
        window.Show();
        window.UpdateLayout();
        var notifications = new List<string>();
        try
        {
            control.FocusEditor();
            control.CaretOffset = document.TextLength;
            var request = new TextInputMethodClientRequestedEventArgs
            {
                RoutedEvent = InputElement.TextInputMethodClientRequestedEvent
            };
            control.TextEditor.TextArea.RaiseEvent(request);
            var client = Assert.IsAssignableFrom<TextInputMethodClient>(request.Client);
            Assert.True(client.SupportsSurroundingText);
            void QuerySurroundingText(object? sender, EventArgs args)
            {
                notifications.Add(client.SurroundingText);
                _ = client.Selection;
            }
            client.SurroundingTextChanged += QuerySurroundingText;
            try
            {
                if (moveFocusFirst)
                {
                    Assert.True(other.Focus());
                    Assert.True(other.IsKeyboardFocusWithin);
                }
                if (dispose) control.Dispose();
                else window.Content = null;
                Assert.NotEmpty(notifications);
                Assert.Equal("first\nsecond", document.Text);
                Assert.NotSame(document, control.TextEditor.Document);
                Assert.NotNull(control.TextEditor.Document);
                Assert.Equal("", control.TextEditor.Document.Text);
                if (dispose) Assert.True(control.TextEditor.IsReadOnly);
                Assert.True(string.IsNullOrEmpty(client.SurroundingText));
            }
            finally { client.SurroundingTextChanged -= QuerySurroundingText; }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void FocusedTabSwitchesPreserveHostDocumentAndImeClientAfterReattachment()
    {
        var document = new TextDocument("first\nsecond");
        document.Insert(0, "#");
        var expected = document.Text;
        using var control = new PowerShellEditorControl(document);
        using var other = new PowerShellEditorControl(new TextDocument("other\neditor"));
        var firstTab = new TabItem { Header = "First", Content = control };
        var secondTab = new TabItem { Header = "Second", Content = other };
        var tabs = new TabControl { Items = { firstTab, secondTab }, SelectedItem = firstTab };
        var window = new Window { Content = tabs, Width = 800, Height = 500 };
        window.Show();
        window.UpdateLayout();
        try
        {
            for (var iteration = 0; iteration < 10; iteration++)
            {
                Assert.True(control.FocusEditor());
                control.Select(new(7, 3));
                var caret = control.CaretOffset;
                var selection = control.Selection;
                var version = control.DocumentVersion;
                var client = RequestImeClient(control);
                var observed = new List<string>();
                void Query(object? sender, EventArgs args)
                {
                    observed.Add(client.SurroundingText);
                    _ = client.Selection;
                }
                void LostFocus(object? sender, Avalonia.Input.FocusChangedEventArgs args) => client.SurroundingTextChanged -= Query;
                client.SurroundingTextChanged += Query;
                control.TextEditor.TextArea.LostFocus += LostFocus;
                try
                {
                    Assert.Equal("second", client.SurroundingText);
                    tabs.SelectedItem = secondTab;
                    window.UpdateLayout();
                    Assert.NotEmpty(observed);
                    Assert.NotSame(document, control.TextEditor.Document);
                    Assert.Equal("", control.TextEditor.Document.Text);
                    Assert.Equal(expected, document.Text);
                    tabs.SelectedItem = firstTab;
                    window.UpdateLayout();
                    Assert.Same(document, control.Document);
                    Assert.Same(document, control.TextEditor.Document);
                    Assert.Equal(caret, control.CaretOffset);
                    Assert.Equal(selection, control.Selection);
                    Assert.Equal(version, control.DocumentVersion);
                    Assert.True(control.FocusEditor());
                    Assert.Equal("second", RequestImeClient(control).SurroundingText);
                }
                finally
                {
                    client.SurroundingTextChanged -= Query;
                    control.TextEditor.TextArea.LostFocus -= LostFocus;
                }
            }
            Assert.True(document.UndoStack.CanUndo);
            document.UndoStack.Undo();
            Assert.Equal("first\nsecond", document.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void DetachedDocumentReplacementAndDisposalLeaveImeQueryableWithoutHostSubscriptions()
    {
        var first = new TextDocument("first\nsecond");
        var replacement = new TextDocument("replacement\ncontent");
        using var control = new PowerShellEditorControl(first);
        var window = Show(control);
        var observations = 0;
        try
        {
            control.FocusEditor();
            control.CaretOffset = first.TextLength;
            var client = RequestImeClient(control);
            void Query(object? sender, EventArgs args)
            {
                _ = client.SurroundingText;
                _ = client.Selection;
                observations++;
            }
            client.SurroundingTextChanged += Query;
            try
            {
                window.Content = null;
                control.Document = replacement;
                Assert.NotSame(replacement, control.TextEditor.Document);
                Assert.NotNull(control.TextEditor.Document);
                var detachedObservations = observations;
                first.Insert(0, "old host edit\n");
                replacement.Insert(0, "new host edit\n");
                Assert.Equal(detachedObservations, observations);
                window.Content = control;
                window.UpdateLayout();
                Assert.Same(replacement, control.TextEditor.Document);
                control.FocusEditor();
                control.CaretOffset = replacement.TextLength;
                Assert.Equal("content", RequestImeClient(control).SurroundingText);
                control.Dispose();
                Assert.NotSame(replacement, control.TextEditor.Document);
                Assert.Equal("", control.TextEditor.Document.Text);
                Assert.True(control.TextEditor.IsReadOnly);
                var disposedObservations = observations;
                replacement.Insert(0, "# after disposal\n");
                Assert.Equal(disposedObservations, observations);
                Assert.EndsWith("replacement\ncontent", replacement.Text);
            }
            finally { client.SurroundingTextChanged -= Query; }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task FluentRenderingEditsNeverCreateRunspacesOrChangeGlobals()
    {
        var original = Runspace.DefaultRunspace;
        var runspaceCount = RunspaceCreationCount();
        var resources = Application.Current!.Resources;
        var styleCount = Application.Current.Styles.Count;
        var document = new TextDocument("$value = 42");
        using var control = new PowerShellEditorControl(document);
        var window = Show(control);
        try
        {
            Assert.Same(document, Inner(control).Document);
            Assert.True(Inner(control).Bounds.Width > 0);
            Assert.True(control.FocusEditor());
            control.CaretOffset = document.TextLength;
            window.KeyTextInput("\n$value");
            Assert.Equal("$value = 42\n$value", document.Text);
            var analysis = await control.AnalyzeAsync();
            Assert.Equal(EditorAnalysisState.Unavailable, analysis.State);
            Assert.Empty(analysis.Diagnostics);
            Assert.Equal(control.DocumentVersion, analysis.Version);
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            Assert.True(frame.PixelSize.Width > 0 && frame.PixelSize.Height > 0);
            Assert.Same(original, Runspace.DefaultRunspace);
            Assert.Equal(runspaceCount, RunspaceCreationCount());
            Assert.Same(resources, Application.Current.Resources);
            Assert.Equal(styleCount, Application.Current.Styles.Count);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ExistingHostDefaultRunspaceIsNeverReplaced()
    {
        var previous = Runspace.DefaultRunspace;
        using var hostRunspace = RunspaceFactory.CreateRunspace(InitialSessionState.Create());
        Runspace.DefaultRunspace = hostRunspace;
        var count = RunspaceCreationCount();
        try
        {
            using var control = new PowerShellEditorControl(new TextDocument("$x = 1"));
            var window = Show(control);
            try
            {
                foreach (var text in new[] { "$x = 1", "if (", "configuration Example { Node localhost { } }",
                             "\"$(configuration Nested { })\"", "using module MissingModule" })
                {
                    control.Document.Text = text;
                    var result = await control.AnalyzeAsync();
                    Assert.Equal(EditorAnalysisState.Unavailable, result.State);
                    Assert.Empty(result.Diagnostics);
                    using var frame = window.CaptureRenderedFrame();
                    Assert.NotNull(frame);
                    Assert.Same(hostRunspace, Runspace.DefaultRunspace);
                    Assert.Equal(count, RunspaceCreationCount());
                }
                Assert.Same(hostRunspace, Runspace.DefaultRunspace);
                Assert.Equal(RunspaceState.BeforeOpen, hostRunspace.RunspaceStateInfo.State);
                Assert.Equal(count, RunspaceCreationCount());
                window.Content = null;
                control.Dispose();
                Assert.Same(hostRunspace, Runspace.DefaultRunspace);
            }
            finally { window.Close(); }
        }
        finally { Runspace.DefaultRunspace = previous; }
    }

    [AvaloniaFact]
    public async Task ExplicitHostDiagnosticsHaveVersionedUtf16SpansWithoutBuiltinParsing()
    {
        var document = new TextDocument("'😀'; if (");
        using var control = new PowerShellEditorControl(document);
        Assert.Equal(EditorAnalysisState.Unavailable, (await control.AnalyzeAsync()).State);
        control.AnalysisProvider = new ImmediateAnalysisProvider(request => new(request.Version, EditorAnalysisState.Available,
            [new("Host.MissingExpression", "Expected an expression.", EditorDiagnosticSeverity.Error, new(request.Text.Length, 0))]));
        var result = await control.AnalyzeAsync();
        Assert.Equal(EditorAnalysisState.Available, result.State);
        Assert.NotEmpty(result.Diagnostics);
        Assert.All(result.Diagnostics, diagnostic =>
        {
            Assert.Equal(EditorDiagnosticSeverity.Error, diagnostic.Severity);
            Assert.NotEmpty(diagnostic.Code);
            Assert.NotEmpty(diagnostic.Message);
            Assert.InRange(diagnostic.Span.Start, 0, document.TextLength);
            Assert.InRange(diagnostic.Span.End, diagnostic.Span.Start, document.TextLength);
        });
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Span.Start >= 6);
    }

    [AvaloniaFact]
    public async Task DiagnosticFreeHostAnalysisIsDistinctFromUnavailableAndUsesImmutableRequest()
    {
        using var control = new PowerShellEditorControl(new TextDocument("$x = 1"));
        var before = await control.AnalyzeAsync();
        Assert.Equal(EditorAnalysisState.Unavailable, before.State);
        EditorAnalysisRequest? received = null;
        control.AnalysisProvider = new ImmediateAnalysisProvider(request =>
        {
            received = request;
            return new(request.Version, EditorAnalysisState.Available, []);
        });
        var result = await control.AnalyzeAsync();
        Assert.Equal(EditorAnalysisState.Available, result.State);
        Assert.Empty(result.Diagnostics);
        Assert.Equal("$x = 1", received!.Text);
        control.Document.Text = "changed";
        Assert.Equal("$x = 1", received.Text);
        Assert.DoesNotContain(typeof(PowerShellEditorControl).Assembly.GetReferencedAssemblies(),
            assembly => assembly.Name is "System.Management.Automation" or "Iseberg.Core" or "Iseberg");
    }

    [AvaloniaFact]
    public void SnapshotsPreserveOwnershipSelectionLinesAndUndoAcrossDetach()
    {
        var document = new TextDocument("first\r\n😀last\r\n");
        using var control = new PowerShellEditorControl(document);
        var window = Show(control);
        try
        {
            var version = control.DocumentVersion;
            Assert.Equal(version, control.DocumentVersion);
            control.Select(new(7, 2));
            var selected = control.CaptureText(EditorTextScope.SelectionOrCurrentLine);
            Assert.Equal("😀", selected.Text);
            Assert.Equal(new EditorTextSpan(7, 2), selected.Span);
            control.Select(new(9, 0));
            Assert.Equal("😀last", control.CaptureText(EditorTextScope.SelectionOrCurrentLine).Text);
            control.CaretOffset = document.TextLength;
            Assert.Equal("", control.CaptureText(EditorTextScope.SelectionOrCurrentLine).Text);
            document.Insert(0, "#");
            Assert.True(control.DocumentVersion > version);
            Assert.Equal("😀", selected.Text);
            control.Select(new(2, 2));
            window.Content = null;
            Assert.NotSame(document, Inner(control).Document);
            Assert.Equal("", Inner(control).Document.Text);
            document.Insert(0, "!");
            var detachedVersion = control.DocumentVersion;
            window.Content = control;
            window.UpdateLayout();
            Assert.Equal(new EditorTextSpan(2, 2), control.Selection);
            Assert.Equal(detachedVersion, control.DocumentVersion);
            Assert.True(document.UndoStack.CanUndo);
            control.Dispose();
            document.UndoStack.Undo();
            Assert.Equal("#first\r\n😀last\r\n", document.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task DocumentReplacementCancelsWorkAndNeverMutatesEitherDocument()
    {
        var first = new TextDocument("first");
        var second = new TextDocument("second");
        using var control = new PowerShellEditorControl(first);
        var provider = new DeferredProvider();
        control.CompletionProvider = provider;
        var pending = control.RequestCompletionAsync();
        control.Document = second;
        Assert.True(provider.Token.IsCancellationRequested);
        provider.Resolve();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal("first", first.Text);
        Assert.Equal("second", second.Text);
        Assert.Equal(0, control.CaretOffset);
        Assert.Equal("second", control.CaptureText().Text);
    }

    [AvaloniaFact]
    public void ReadOnlyProtectsKeyboardAutomationAndCompletionButNotHostEdits()
    {
        var document = new TextDocument("Get");
        using var control = new PowerShellEditorControl(document) { IsReadOnly = true };
        var window = Show(control);
        try
        {
            control.FocusEditor();
            window.KeyTextInput("x");
            Assert.Equal("Get", document.Text);
            var inner = Inner(control);
            var peer = ControlAutomationPeer.CreatePeerForElement(inner)!;
            var value = peer.GetProvider<IValueProvider>()!;
            Assert.Equal(AutomationControlType.Edit, peer.GetAutomationControlType());
            Assert.Equal("PowerShell script editor", AutomationProperties.GetName(inner));
            Assert.True(value.IsReadOnly);
            Assert.Throws<InvalidOperationException>(() => value.SetValue("x"));
            Assert.Throws<InvalidOperationException>(() => control.ApplyCompletion(List(control), 0));
            document.Text = "host";
            Assert.Equal("host", value.Value);
            control.IsReadOnly = false;
            value.SetValue("allowed");
            Assert.Equal("allowed", document.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ExecutionGesturesAreOptInFocusedHostEventsOnly()
    {
        using var control = new PowerShellEditorControl(new TextDocument("first\nsecond"));
        var other = new TextBox();
        var window = new Window { Content = new StackPanel { Children = { control, other } }, Width = 800, Height = 500 };
        var requests = new List<EditorExecutionRequestedEventArgs>();
        control.ExecutionRequested += (_, request) => requests.Add(request);
        window.Show();
        window.UpdateLayout();
        try
        {
            control.FocusEditor();
            Press(window, Key.F5);
            Press(window, Key.F8);
            Assert.Empty(requests);
            control.EnableExecutionGestures = true;
            control.Select(new(6, 6));
            Press(window, Key.F5);
            Press(window, Key.F8);
            Press(window, Key.Pause, KeyModifiers.Control);
            Assert.Equal(3, requests.Count);
            Assert.Equal("first\nsecond", requests[0].Snapshot!.Text);
            Assert.Equal("second", requests[1].Snapshot!.Text);
            Assert.Equal(EditorExecutionRequestKind.Stop, requests[2].Kind);
            Assert.Null(requests[2].Snapshot);
            other.Focus();
            Press(window, Key.F5);
            Assert.Equal(3, requests.Count);
            Assert.Equal("first\nsecond", control.Document.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void NavigationSelectionUndoRedoAndFindRemainAvailable()
    {
        using var control = new PowerShellEditorControl(new TextDocument("abc"));
        var window = Show(control);
        try
        {
            control.FocusEditor();
            control.CaretOffset = 3;
            Press(window, Key.Left, KeyModifiers.Shift);
            Assert.Equal(new EditorTextSpan(2, 1), control.Selection);
            window.KeyTextInput("d");
            Assert.Equal("abd", control.Document.Text);
            Press(window, ApplicationCommands.Undo.Gesture);
            Assert.Equal("abc", control.Document.Text);
            Press(window, ApplicationCommands.Redo.Gesture);
            Assert.Equal("abd", control.Document.Text);
            Press(window, ApplicationCommands.SelectAll.Gesture);
            Assert.Equal(3, control.Selection.Length);
            Press(window, ApplicationCommands.Find.Gesture);
            Assert.False(Assert.Single(control.GetVisualDescendants().OfType<SearchPanel>()).IsClosed);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task CopyPasteUsesHostClipboardAndRetainsUndo()
    {
        using var control = new PowerShellEditorControl(new TextDocument("abc"));
        var window = Show(control);
        try
        {
            control.FocusEditor();
            control.Select(new(0, 3));
            Inner(control).Copy();
            await Task.Yield();
            control.Select(new(3, 0));
            Inner(control).Paste();
            await Task.Yield();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("abcabc", control.Document.Text);
            Assert.True(control.Document.UndoStack.CanUndo);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task CompletionIsInjectedVersionedValidatedAndUndoable()
    {
        using var control = new PowerShellEditorControl(new TextDocument("Get"));
        control.CaretOffset = 3;
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.RequestCompletionAsync());
        control.CompletionProvider = new ImmediateProvider(request => new(request.Version, new(0, 3),
            [new("Get-Date", "Get-Date", "Host result")]));
        var list = await control.RequestCompletionAsync();
        control.ApplyCompletion(list, 0);
        Assert.Equal("Get-Date", control.Document.Text);
        Assert.Equal(8, control.CaretOffset);
        Assert.Throws<InvalidOperationException>(() => control.ApplyCompletion(list, 0));
        control.Document.UndoStack.Undo();
        Assert.Equal("Get", control.Document.Text);
    }

    [AvaloniaFact]
    public async Task CompletionKeyboardPopupUsesOnlyTheInjectedProvider()
    {
        using var control = new PowerShellEditorControl(new TextDocument("Get"));
        var calls = 0;
        var errors = new List<EditorErrorEventArgs>();
        control.ErrorOccurred += (_, error) => errors.Add(error);
        control.CompletionProvider = new ImmediateProvider(request =>
        {
            calls++;
            return new(request.Version, new(0, 3), [new("Get-Date", "Get-Date")]);
        });
        var window = Show(control);
        try
        {
            control.CaretOffset = 3;
            control.FocusEditor();
            Press(window, Key.Space, KeyModifiers.Control);
            await Task.Yield();
            Assert.Equal(1, calls);
            Assert.True(errors.Count == 0, string.Join("\n", errors.Select(error => error.Exception.ToString())));
            Press(window, Key.Enter);
            Assert.Equal("Get-Date", control.Document.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task CompletionPopupFiltersUserTypingButHostMutationsInvalidateIt()
    {
        using var control = new PowerShellEditorControl(new TextDocument("Get"));
        control.CompletionProvider = new ImmediateProvider(request => new(request.Version, new(0, 3),
            [new("Get-Date", "Date"), new("Get-Process", "Process")]));
        var window = Show(control);
        try
        {
            control.CaretOffset = 3;
            control.FocusEditor();
            await control.ShowCompletionAsync();
            window.KeyTextInput("-D");
            await Task.Yield();
            Assert.True(control.IsCompletionOpen);
            Assert.Equal("Get-Date", control.CompletionPopup!.CompletionList.SelectedItem!.Text);
            Press(window, Key.Tab);
            Assert.Equal("Get-Date", control.Document.Text);
            control.Document.Text = "Get";
            control.CaretOffset = 3;
            await control.ShowCompletionAsync();
            control.Document.Insert(3, "-D");
            Assert.False(control.IsCompletionOpen);
            Assert.Equal("Get-D", control.Document.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task CompletionPreferenceFindAndHostProtectedRangesShareTheActualEditor()
    {
        using var control = new PowerShellEditorControl(new TextDocument("Get")) { CompletionAcceptsEnter = false };
        control.CompletionProvider = new ImmediateProvider(request => new(request.Version, new(0, 3), [new("Get-Date", "Date")]));
        var window = Show(control);
        try
        {
            Assert.Same(control.TextEditor, Inner(control));
            control.FocusEditor();
            control.CaretOffset = 3;
            await control.ShowCompletionAsync();
            control.CloseCompletion();
            Assert.False(control.IsCompletionOpen);
            await control.ShowCompletionAsync();
            Press(window, Key.Enter);
            Assert.StartsWith("Get", control.Document.Text);
            Assert.Contains("\n", control.Document.Text);
            Assert.DoesNotContain("Get-Date", control.Document.Text);
            control.ShowFind();
            Assert.False(Assert.Single(control.GetVisualDescendants().OfType<SearchPanel>()).IsClosed);
            control.TextEditor.TextArea.ReadOnlySectionProvider = new ProtectedDocument();
            Assert.Throws<InvalidOperationException>(() => control.ApplyCompletion(List(control), 0));
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("edit")]
    [InlineData("caret")]
    [InlineData("provider")]
    [InlineData("readonly")]
    [InlineData("detach")]
    [InlineData("dispose")]
    public async Task CancellationAndStaleCompletionCannotPublishOrEdit(string change)
    {
        using var control = new PowerShellEditorControl(new TextDocument("Get"));
        var window = Show(control);
        var provider = new DeferredProvider();
        control.CompletionProvider = provider;
        var task = control.RequestCompletionAsync();
        try
        {
            switch (change)
            {
                case "edit": control.Document.Insert(3, "-"); break;
                case "caret": control.CaretOffset = 1; break;
                case "provider": control.CompletionProvider = null; break;
                case "readonly": control.IsReadOnly = true; break;
                case "detach": window.Content = null; break;
                case "dispose": control.Dispose(); break;
            }
            Assert.True(provider.Token.IsCancellationRequested);
            provider.Resolve();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            Assert.Equal(change == "edit" ? "Get-" : "Get", control.Document.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task CompletionSupersessionAndCallerCancellationArePropagated()
    {
        using var control = new PowerShellEditorControl(new TextDocument("Get"));
        var provider = new DeferredProvider();
        control.CompletionProvider = provider;
        var first = control.RequestCompletionAsync();
        var firstSource = provider.Source;
        var firstVersion = provider.Request!.Version;
        using var cancellation = new CancellationTokenSource();
        var second = control.RequestCompletionAsync(cancellation.Token);
        firstSource.SetResult(new(firstVersion, new(0, 3), [new("Get-Date", "Date")]));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        cancellation.Cancel();
        provider.Resolve();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
    }

    [AvaloniaTheory]
    [InlineData("version")]
    [InlineData("span")]
    [InlineData("items")]
    public async Task InvalidCompletionResponsesFailExplicitly(string failure)
    {
        using var control = new PowerShellEditorControl(new TextDocument("Get"));
        control.CompletionProvider = new ImmediateProvider(request => failure switch
        {
            "version" => new(request.Version + 1, new(0, 3), []),
            "span" => new(request.Version, new(0, 4), []),
            _ => new(request.Version, new(0, 3), default)
        });
        var exception = await Record.ExceptionAsync(() => control.RequestCompletionAsync());
        if (failure == "span") Assert.IsType<ArgumentOutOfRangeException>(exception);
        else Assert.IsType<InvalidOperationException>(exception);
    }

    [AvaloniaFact]
    public async Task ProviderFailurePropagatesAndKeyboardFailureIsSurfaced()
    {
        using var control = new PowerShellEditorControl(new TextDocument("Get"));
        var exception = new IOException("host provider failed");
        control.CompletionProvider = new ThrowingProvider(exception);
        Assert.Same(exception, await Assert.ThrowsAsync<IOException>(() => control.RequestCompletionAsync()));
        var window = Show(control);
        try
        {
            var reported = new TaskCompletionSource<EditorErrorEventArgs>();
            control.ErrorOccurred += (_, error) => reported.TrySetResult(error);
            control.FocusEditor();
            Press(window, Key.Space, KeyModifiers.Control);
            var error = await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("Completion", error.Operation);
            Assert.Same(exception, error.Exception);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task HostAnalysisSupersessionDetachAndDisposeRejectStaleResults()
    {
        using var control = new PowerShellEditorControl(new TextDocument("if ("));
        var window = Show(control);
        var provider = new DeferredAnalysisProvider();
        control.AnalysisProvider = provider;
        try
        {
            var stale = control.AnalyzeAsync();
            control.Document.Text = "$x = 1";
            provider.Resolve();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stale);
            control.AnalysisProvider = new ImmediateAnalysisProvider(request => new(request.Version, EditorAnalysisState.Available, []));
            Assert.Equal(EditorAnalysisState.Available, (await control.AnalyzeAsync()).State);
            provider = new DeferredAnalysisProvider();
            control.AnalysisProvider = provider;
            var detached = control.AnalyzeAsync();
            window.Content = null;
            provider.Resolve();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => detached);
            Assert.Equal(EditorAnalysisState.Unavailable, control.Analysis.State);
            var disposed = control.AnalyzeAsync();
            control.Dispose();
            provider.Resolve();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => disposed);
            Assert.Throws<ObjectDisposedException>(() => control.CaptureText());
            control.Document.Insert(0, "#");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task DetachedEditsInvalidateHostAnalysisAndCancellationDoesNotPublish()
    {
        using var control = new PowerShellEditorControl(new TextDocument("$x = 1"));
        control.AnalysisProvider = new ImmediateAnalysisProvider(request => new(request.Version, EditorAnalysisState.Available, []));
        await control.AnalyzeAsync();
        control.Document.Text = "if (";
        Assert.Equal(EditorAnalysisState.Unavailable, control.Analysis.State);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => control.AnalyzeAsync(cancellation.Token));
        Assert.Equal(EditorAnalysisState.Unavailable, control.Analysis.State);
    }

    [AvaloniaTheory]
    [InlineData("version")]
    [InlineData("span")]
    [InlineData("severity")]
    [InlineData("state")]
    [InlineData("unavailable")]
    [InlineData("failure")]
    public async Task InvalidOrFailedHostAnalysisNeverLooksSuccessful(string failure)
    {
        using var control = new PowerShellEditorControl(new TextDocument("Get"));
        control.AnalysisProvider = new ImmediateAnalysisProvider(request => failure switch
        {
            "version" => new(request.Version + 1, EditorAnalysisState.Available, []),
            "span" => new(request.Version, EditorAnalysisState.Available, [new("X", "X", EditorDiagnosticSeverity.Error, new(0, 4))]),
            "severity" => new(request.Version, EditorAnalysisState.Available, [new("X", "X", (EditorDiagnosticSeverity)99, new(0, 3))]),
            "state" => new(request.Version, EditorAnalysisState.Pending, []),
            "unavailable" => new(request.Version, EditorAnalysisState.Unavailable, [new("X", "X", EditorDiagnosticSeverity.Error, new(0, 3))]),
            _ => throw new IOException("Host analysis failed")
        });
        var exception = await Record.ExceptionAsync(() => control.AnalyzeAsync());
        Assert.NotNull(exception);
        Assert.Equal(EditorAnalysisState.Failed, control.Analysis.State);
        Assert.Empty(control.Analysis.Diagnostics);
    }

    [AvaloniaFact]
    public async Task HostAnalysisProviderReplacementCancelsAndAutomaticErrorsAreSurfaced()
    {
        using var control = new PowerShellEditorControl(new TextDocument("if ("));
        var provider = new DeferredAnalysisProvider();
        control.AnalysisProvider = provider;
        var pending = control.AnalyzeAsync();
        control.AnalysisProvider = null;
        Assert.True(provider.Token.IsCancellationRequested);
        provider.Resolve();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(EditorAnalysisState.Unavailable, control.Analysis.State);
        var reported = new TaskCompletionSource<EditorErrorEventArgs>();
        control.ErrorOccurred += (_, error) => reported.TrySetResult(error);
        control.AnalysisProvider = new ImmediateAnalysisProvider(_ => throw new IOException("Host analysis unavailable"));
        var window = Show(control);
        try
        {
            var error = await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("Analysis", error.Operation);
            Assert.IsType<IOException>(error.Exception);
            Assert.Equal(EditorAnalysisState.Failed, control.Analysis.State);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task CallerCancelledHostAnalysisBecomesUnavailableAndDisposedControlPublishesNothing()
    {
        using var control = new PowerShellEditorControl(new TextDocument("$x"));
        var provider = new DeferredAnalysisProvider();
        control.AnalysisProvider = provider;
        using var cancellation = new CancellationTokenSource();
        var pending = control.AnalyzeAsync(cancellation.Token);
        Assert.Equal(EditorAnalysisState.Pending, control.Analysis.State);
        cancellation.Cancel();
        provider.Resolve();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(EditorAnalysisState.Unavailable, control.Analysis.State);
        var window = Show(control);
        var events = 0;
        control.AnalysisChanged += (_, _) => events++;
        control.Dispose();
        var baseline = events;
        window.Close();
        control.Document.Insert(0, "host");
        Assert.Equal(EditorAnalysisState.Unavailable, control.Analysis.State);
        Assert.Equal(baseline, events);
    }

    [AvaloniaFact]
    public void LexicalHighlightingDoesNotMutateGlobalDefinitionsAndCanBeDisabled()
    {
        var original = HighlightingManager.Instance.GetDefinition("PowerShell");
        var originalBrushes = original.NamedHighlightingColors.Select(color => color.Foreground).ToArray();
        using var first = new PowerShellEditorControl(new TextDocument("$x = 1"));
        using var second = new PowerShellEditorControl(new TextDocument("# comment"));
        var window = new Window { Content = new StackPanel { Children = { first, second } } };
        window.Show();
        try
        {
            Assert.NotSame(original, Inner(first).SyntaxHighlighting);
            Assert.NotSame(Inner(first).SyntaxHighlighting, Inner(second).SyntaxHighlighting);
            window.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
            Assert.Equal(originalBrushes, original.NamedHighlightingColors.Select(color => color.Foreground));
            first.EnableSyntaxHighlighting = false;
            Assert.Null(Inner(first).SyntaxHighlighting);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task PublicMutationsRejectWrongThreadAndInvalidSpans()
    {
        using var control = new PowerShellEditorControl(new TextDocument("abc"));
        Assert.Throws<ArgumentOutOfRangeException>(() => control.Select(new(2, 2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => control.CaretOffset = 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => new EditorTextSpan(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EditorTextSpan(int.MaxValue, 1));
        await Task.Run(() => Assert.Throws<InvalidOperationException>(() => control.CaptureText()));
    }

    private static Window Show(PowerShellEditorControl control)
    {
        var window = new Window { Content = control, Width = 800, Height = 500 };
        window.Show();
        window.UpdateLayout();
        return window;
    }
    private static TextEditor Inner(PowerShellEditorControl control) => control.GetVisualDescendants().OfType<TextEditor>().Single();
    private static TextInputMethodClient RequestImeClient(PowerShellEditorControl control)
    {
        var request = new TextInputMethodClientRequestedEventArgs { RoutedEvent = InputElement.TextInputMethodClientRequestedEvent };
        control.TextEditor.TextArea.RaiseEvent(request);
        return Assert.IsAssignableFrom<TextInputMethodClient>(request.Client);
    }
    private static void Press(Window window, KeyGesture gesture) => Press(window, gesture.Key, gesture.KeyModifiers);
    private static void Press(Window window, Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        var raw = RawInputModifiers.None;
        if (modifiers.HasFlag(KeyModifiers.Control)) raw |= RawInputModifiers.Control;
        if (modifiers.HasFlag(KeyModifiers.Shift)) raw |= RawInputModifiers.Shift;
        if (modifiers.HasFlag(KeyModifiers.Alt)) raw |= RawInputModifiers.Alt;
        if (modifiers.HasFlag(KeyModifiers.Meta)) raw |= RawInputModifiers.Meta;
        window.KeyPress(key, raw, PhysicalKey.None, null);
        window.KeyRelease(key, raw, PhysicalKey.None, null);
    }
    private static EditorCompletionList List(PowerShellEditorControl control) =>
        new(control.DocumentVersion, new(0, 3), [new("Get-Date", "Date")]);

    // The pinned parser has no public local runspace census. Its constructor increments this counter even for later-disposed runspaces.
    private static int RunspaceCreationCount() =>
        (int)(typeof(Runspace).GetField("s_globalId", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
            ?? throw new InvalidOperationException("Pinned PowerShell runspace counter not found."));

    private sealed class ImmediateProvider(Func<EditorCompletionRequest, EditorCompletionList> complete) : IEditorCompletionProvider
    {
        public Task<EditorCompletionList> CompleteAsync(EditorCompletionRequest request, CancellationToken token) =>
            Task.FromResult(complete(request));
    }
    private sealed class ImmediateAnalysisProvider(Func<EditorAnalysisRequest, EditorAnalysisResult> analyze) : IEditorAnalysisProvider
    {
        public Task<EditorAnalysisResult> AnalyzeAsync(EditorAnalysisRequest request, CancellationToken token) =>
            Task.FromResult(analyze(request));
    }
    private sealed class DeferredAnalysisProvider : IEditorAnalysisProvider
    {
        public TaskCompletionSource<EditorAnalysisResult> Source { get; private set; } = new();
        public EditorAnalysisRequest? Request { get; private set; }
        public CancellationToken Token { get; private set; }
        public Task<EditorAnalysisResult> AnalyzeAsync(EditorAnalysisRequest request, CancellationToken token)
        {
            Request = request;
            Token = token;
            Source = new();
            return Source.Task;
        }
        public void Resolve() => Source.SetResult(new(Request!.Version, EditorAnalysisState.Available, []));
    }
    private sealed class DeferredProvider : IEditorCompletionProvider
    {
        public TaskCompletionSource<EditorCompletionList> Source { get; private set; } = new();
        public EditorCompletionRequest? Request { get; private set; }
        public CancellationToken Token { get; private set; }
        public Task<EditorCompletionList> CompleteAsync(EditorCompletionRequest request, CancellationToken token)
        {
            Request = request;
            Token = token;
            Source = new();
            return Source.Task;
        }
        public void Resolve() => Source.SetResult(new(Request!.Version, new(0, 3), [new("Get-Date", "Date")]));
    }
    private sealed class ThrowingProvider(Exception exception) : IEditorCompletionProvider
    {
        public Task<EditorCompletionList> CompleteAsync(EditorCompletionRequest request, CancellationToken token) =>
            Task.FromException<EditorCompletionList>(exception);
    }

    private sealed class ProtectedDocument : AvaloniaEdit.Editing.IReadOnlySectionProvider
    {
        public bool CanInsert(int offset) => false;
        public IEnumerable<ISegment> GetDeletableSegments(ISegment segment) => [];
    }
}
