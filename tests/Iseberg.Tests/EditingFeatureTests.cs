using System.Management.Automation;
using System.Xml;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.CodeCompletion;
using Iseberg.Core;
using Xunit;
using SessionState = Iseberg.Core.SessionState;

namespace Iseberg.Tests;

public sealed class EditingFeatureTests
{
    [Theory]
    [InlineData("if ($true) { [int]$x = @(1, 2) }", 11, 11, 31)]
    [InlineData("@{ Name = '}' }", 1, 1, 14)]
    [InlineData("'ignore {}' # ignore []", 8, -1, -1)]
    [InlineData("([)]", 0, -1, -1)]
    [InlineData("{}", 1, 0, 1)]
    [InlineData("\"value $($items[0])\"", 8, 8, 18)]
    public void BraceNavigationUsesParserTokens(string text, int caret, int open, int close)
    {
        var pair = EditorAnalysis.MatchingBrace(text, caret);
        if (open < 0) Assert.Null(pair);
        else
        {
            Assert.Equal((open, close), pair);
            Assert.Equal(pair, EditorAnalysis.MatchingBrace(text, close));
            Assert.Equal(pair, EditorAnalysis.MatchingBrace(text, close + 1));
        }
    }

    [Theory]
    [InlineData("$", CompletionResultType.Variable)]
    [InlineData("Get-", CompletionResultType.Command)]
    [InlineData("Get-Process -", CompletionResultType.ParameterName)]
    [InlineData("$item.", CompletionResultType.Property)]
    [InlineData("$item1.", CompletionResultType.Property)]
    [InlineData("\"$", CompletionResultType.Variable)]
    [InlineData("\"value $($item.", CompletionResultType.Property)]
    [InlineData("[string]::", CompletionResultType.Method)]
    [InlineData("[System.", CompletionResultType.Type)]
    [InlineData("Get-ChildItem .\\", CompletionResultType.ProviderContainer)]
    [InlineData("Get-ChildItem './", CompletionResultType.ProviderItem)]
    [InlineData("Get-Process -ErrorAction ", CompletionResultType.ParameterValue)]
    public void AutomaticCompletionHasContextSpecificResultFilters(string text, CompletionResultType expected)
    {
        Assert.Contains(expected, EditorAnalysis.CompletionFilter(text, text.Length)!);
    }

    [Theory]
    [InlineData("# $")]
    [InlineData("'$")]
    [InlineData("Get-Process")]
    [InlineData("1.")]
    [InlineData("$items*.")]
    [InlineData("Get-Process ")]
    [InlineData("$value -")]
    public void OrdinaryTextCommentsAndOperatorsDoNotOpenAutomaticCompletion(string text) =>
        Assert.Null(EditorAnalysis.CompletionFilter(text, text.Length));

    [Fact]
    public void ReplacementSupportsLiteralCaseWordRegexGroupsDirectionAndScope()
    {
        const string text = "cat CAT cats cat.";
        Assert.Equal(4, ReplacementSearch.Find(text, "cat", "$literal", new()).Count);
        Assert.Equal(3, ReplacementSearch.Find(text, "cat", "", new(WholeWord: true)).Count);
        Assert.Equal(3, ReplacementSearch.Find(text, "cat", "", new(MatchCase: true)).Count);
        Assert.Equal("$literal", ReplacementSearch.Find(text, "cat", "$literal", new())[0].Text);
        var regex = ReplacementSearch.Find(text, @"(cat)(s?)", "$2-$1", new(RegularExpression: true));
        Assert.Equal("s-cat", regex[2].Text);
        Assert.Single(ReplacementSearch.Find(text, "cat", "", new(), 4, 3));
        Assert.Equal(4, ReplacementSearch.Next(regex, 2, new())!.Start);
        Assert.Equal(0, ReplacementSearch.Next(regex, 2, new(SearchUp: true))!.Start);
        Assert.Null(ReplacementSearch.Next(regex, text.Length, new(WrapAround: false)));
        Assert.Equal(0, ReplacementSearch.Next(regex, text.Length, new())!.Start);
        Assert.ThrowsAny<ArgumentException>(() => ReplacementSearch.Find(text, "(", "", new(RegularExpression: true)));
    }

    [Fact]
    public void ZeroLengthRegexMatchesRemainFinite() =>
        Assert.Equal(new[] { 0, 2 }, ReplacementSearch.Find("a\nb", "^", ">", new(RegularExpression: true)).Select(m => m.Start));

    [Fact]
    public void BuiltInCatalogHasEveryIseTitleAndValidTemplates()
    {
        string[] titles =
        [
            "if", "if-else", "for", "foreach", "function", "Cmdlet (advanced function)",
            "Cmdlet (advanced function) - complete", "switch", "while", "do-while", "do-until",
            "try-catch-finally", "try-finally", "Comment block", "Workflow InlineScript", "Workflow Parallel",
            "Workflow Sequence", "Workflow ForEachParallel", "Workflow (simple)", "Workflow (advanced)",
            "DSC Resource Provider (simple)", "DSC Configuration (simple)", "DSC Configuration (using ConfigurationData)",
            "DSC ConfigurationData", "DSC Resource with Class (simple)", "class (simple)"
        ];
        Assert.Equal(titles, SnippetCatalog.BuiltIns.Select(s => s.Title));
        Assert.All(SnippetCatalog.BuiltIns, snippet =>
        {
            Assert.True(snippet.IsBuiltIn);
            Assert.Equal("Iseberg", snippet.Author);
            Assert.DoesNotContain("<caret>", snippet.Code);
            Assert.InRange(snippet.CaretOffset, 0, snippet.Code.Length);
            if (snippet.Compatibility.Length == 0) Assert.Empty(EditorAnalysis.Analyze(snippet.Code).Errors);
        });
    }

    [Fact]
    public void SnippetXmlRoundTripsCodeMetadataCaretAndIndentWithoutNormalizingCrLf()
    {
        var snippet = new PowerShellSnippet("Name <&>", "A description", "Author", "if ($true) {\r\n    'a & b'\r\n}", 17, false);
        var xml = SnippetCatalog.Serialize([snippet]);
        Assert.Equal(snippet, Assert.Single(SnippetCatalog.Parse(xml)));
        Assert.Throws<XmlException>(() => SnippetCatalog.Parse("<!DOCTYPE Snippets [<!ENTITY value 'bad'>]><Snippets/>"));
        Assert.Throws<InvalidDataException>(() => SnippetCatalog.Parse("<Snippets/>"));
        Assert.Throws<InvalidDataException>(() => SnippetCatalog.Parse(xml.Replace("CaretOffset=\"17\"", "CaretOffset=\"999\"")));
        Assert.Throws<InvalidDataException>(() => SnippetCatalog.Parse(xml.Replace("Version=\"1.0.0\"", "Version=\"2.0.0\"")));
    }

    [Fact]
    public async Task CustomSnippetDiscoveryIsRecursiveDeduplicatedAndReportsBadFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "iseberg-snippets-" + Guid.NewGuid().ToString("N"));
        var nested = Path.Combine(root, "nested");
        var first = Path.Combine(root, "first.snippets.ps1xml");
        var second = Path.Combine(nested, "second.snippets.ps1xml");
        var invalid = Path.Combine(root, "invalid.snippets.ps1xml");
        try
        {
            var snippet = new PowerShellSnippet("Custom", "Description", "", "Get-Date");
            await SnippetCatalog.SaveAsync(first, [snippet]);
            await SnippetCatalog.SaveAsync(second, [snippet]);
            await File.WriteAllTextAsync(invalid, "<Snippets/>");
            var result = await SnippetCatalog.LoadAsync([root]);
            Assert.Equal(snippet, Assert.Single(result.Snippets));
            Assert.Contains("invalid.snippets.ps1xml", Assert.Single(result.Errors));
        }
        finally
        {
            File.Delete(first); File.Delete(second); File.Delete(invalid);
            if (Directory.Exists(nested)) Directory.Delete(nested);
            if (Directory.Exists(root)) Directory.Delete(root);
        }
    }

    [Fact]
    public void ConsoleProtectsTranscriptAndPromptPreservesDraftAndBoundsOutput()
    {
        var buffer = new ConsoleBuffer();
        buffer.Append(new("old output\n", OutputKind.Output));
        buffer.ShowPrompt("PS> ");
        buffer.Input = "Get-Date";
        Assert.False(buffer.CanInsert(0));
        Assert.False(buffer.CanInsert(buffer.InputStart - 1));
        Assert.True(buffer.CanInsert(buffer.InputStart));
        var segment = Assert.Single(buffer.GetDeletableSegments(new SimpleSegment(0, buffer.Document.TextLength)));
        Assert.Equal(buffer.InputStart, segment.Offset);
        Assert.Equal("Get-Date".Length, segment.Length);
        buffer.Append(new("async output\n", OutputKind.Warning));
        Assert.EndsWith("PS> Get-Date", buffer.Document.Text);
        Assert.False(buffer.Document.UndoStack.CanUndo);
        buffer.HidePrompt();
        Assert.False(buffer.CanInsert(buffer.Document.TextLength));
        Assert.Equal("Get-Date", buffer.Input);
        buffer.ShowPrompt("longer prompt> ");
        Assert.EndsWith("longer prompt> Get-Date", buffer.Document.Text);
        buffer.Append(new(new string('x', 600_000), OutputKind.Error));
        Assert.InRange(buffer.TranscriptEnd, 0, 500_000);
        Assert.Equal("Get-Date", buffer.Input);
        Assert.Equal(buffer.TranscriptEnd + "longer prompt> ".Length, buffer.InputStart);
        buffer.Clear();
        Assert.Equal("longer prompt> Get-Date", buffer.Document.Text);
        Assert.Empty(buffer.Spans);
    }

    [AvaloniaFact]
    public void ConsoleSelectionCrossesTranscriptButTypingAndAutomationCannotOverwriteIt()
    {
        var buffer = new ConsoleBuffer();
        buffer.Append(new("transcript\n", OutputKind.Output));
        buffer.ShowPrompt("PS> ");
        var editor = new AccessibleTextEditor { Document = buffer.Document };
        editor.TextArea.ReadOnlySectionProvider = buffer;
        var window = new Window { Content = editor };
        try
        {
            window.Show();
            editor.TextArea.Focus();
            editor.CaretOffset = 0;
            editor.TextArea.PerformTextInput("not allowed");
            Assert.Equal("transcript\nPS> ", editor.Text);
            editor.CaretOffset = buffer.InputStart;
            editor.TextArea.PerformTextInput("Get-Date");
            Assert.Equal("Get-Date", buffer.Input);
            editor.SelectAll();
            Assert.Equal(editor.Document.TextLength, editor.SelectionLength);
            Assert.Contains("transcript\nPS> Get-Date", editor.SelectedText);
            editor.Cut();
            Assert.Equal("transcript\nPS> ", editor.Text);
            var provider = ControlAutomationPeer.CreatePeerForElement(editor)!.GetProvider<IValueProvider>()!;
            Assert.Throws<InvalidOperationException>(() => provider.SetValue("overwrite transcript"));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ReplaceAllInSelectionPreservesOutsideTextAndIsOneUndoStep()
    {
        var editor = new TextEditor { Text = "cat CAT cats cat" };
        editor.Document.UndoStack.ClearAll();
        editor.Select(4, 8);
        var dialog = new ReplaceWindow(editor);
        try
        {
            dialog.Show();
            dialog.FindControl<TextBox>("FindText")!.Text = "cat";
            dialog.FindControl<TextBox>("ReplacementText")!.Text = "dog";
            dialog.FindControl<CheckBox>("WholeWord")!.IsChecked = true;
            dialog.FindControl<CheckBox>("SelectionOnly")!.IsChecked = true;
            dialog.FindControl<Button>("ReplaceAll")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("cat dog cats cat", editor.Text);
            editor.Undo();
            Assert.Equal("cat CAT cats cat", editor.Text);
            Assert.False(editor.Document.UndoStack.CanUndo);
            dialog.FindControl<CheckBox>("RegularExpression")!.IsChecked = true;
            dialog.FindControl<TextBox>("FindText")!.Text = "(";
            dialog.FindControl<Button>("ReplaceAll")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("cat CAT cats cat", editor.Text);
            Assert.NotEmpty(dialog.FindControl<TextBlock>("ReplaceStatus")!.Text!);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public void SnippetInsertionUsesDocumentIndentationLineEndingsCaretAndUndo()
    {
        var editor = new TextEditor { Text = "    old\r\n" };
        editor.Document.UndoStack.ClearAll();
        editor.Select(4, 3);
        var snippet = new PowerShellSnippet("Custom", "", "", "if ($true) {\n    \n}", 17);
        MainWindow.InsertSnippet(editor, snippet, "\r\n");
        Assert.Equal("    if ($true) {\r\n        \r\n    }\r\n", editor.Text);
        Assert.Equal(0, editor.SelectionLength);
        Assert.Equal("    if ($true) {\r\n        ".Length, editor.CaretOffset);
        editor.Undo();
        Assert.Equal("    old\r\n", editor.Text);
    }

    [AvaloniaFact]
    public void SnippetPickerSearchesCustomCatalogWithNoBuiltIns()
    {
        var dialog = new SnippetWindow([new("Custom", "personal template", "user", "Get-Date")]);
        try
        {
            dialog.Show();
            dialog.FindControl<TextBox>("SnippetSearch")!.Text = "personal";
            Dispatcher.UIThread.RunJobs();
            Assert.Single(dialog.FindControl<ListBox>("SnippetList")!.Items);
            Assert.Equal("Get-Date", dialog.FindControl<TextBox>("SnippetPreview")!.Text);
            dialog.FindControl<TextBox>("SnippetSearch")!.Text = "absent";
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(dialog.FindControl<ListBox>("SnippetList")!.Items);
            Assert.False(dialog.FindControl<Button>("InsertSnippet")!.IsEnabled);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public async Task ConsoleHistoryAndDraftAreIndependentAndSubmitEchoIsNotDuplicated()
    {
        var window = new MainWindow([], initializeOnOpen: false, preferences: new UserSettings { ConsoleIntelliSense = false });
        var session = new SessionModel("test");
        await session.Engine.InitializeAsync();
        try
        {
            window.Workbench.Sessions.Add(session);
            window.Workbench.SelectedSession = session;
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var editor = window.FindEditor("ConsoleEditor")!;
            session.History.AddRange(["first", "second"]);
            session.HistoryIndex = 2;
            session.Input = "'draft'";
            editor.CaretOffset = editor.Document.TextLength;
            void Key(Key key) => editor.TextArea.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
            Key(Avalonia.Input.Key.Up);
            Assert.Equal("second", session.Input);
            Key(Avalonia.Input.Key.Down);
            Assert.Equal("'draft'", session.Input);
            Key(Avalonia.Input.Key.Enter);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (session.Engine.State != SessionState.Ready && DateTime.UtcNow < deadline) await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
            session.FlushOutput();
            Assert.Equal(SessionState.Ready, session.Engine.State);
            Assert.Equal("", session.Input);
            Assert.Equal(1, session.OutputSpans.Count(s => s.Kind == OutputKind.Command));
            Assert.Contains("draft", editor.Text);
            Assert.True(session.Console.HasPrompt);
            editor.CaretOffset = editor.Document.TextLength;
            session.Input = "if ($true) {";
            editor.CaretOffset = editor.Document.TextLength;
            Key(Avalonia.Input.Key.Enter);
            Assert.EndsWith(Environment.NewLine, session.Input);
            Assert.Equal(SessionState.Ready, session.Engine.State);
        }
        finally { await session.Engine.DisposeAsync(); window.Close(); }
    }

    [Fact]
    public async Task CompletionCancellationDoesNotBreakTheRunspace()
    {
        await using var session = new PowerShellSession();
        await session.InitializeAsync();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.CompleteAsync("Get-", 4, canceled.Token));
        Assert.NotEmpty((await session.CompleteAsync("Get-Process", 11)).Matches);
        Assert.Equal(SessionState.Ready, session.State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3500)]
    public async Task CompletionTimeoutStopsAnActiveArgumentCompleterAndReleasesTheGate(int startupDelayMilliseconds)
    {
        await using var session = new PowerShellSession();
        await session.InitializeAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Output += entry =>
        {
            if (entry.Text.Trim() == "argument-completer-started") started.TrySetResult();
            if (entry.Text.Trim() == "argument-completer-finished") finished.TrySetResult();
        };
        await session.ExecuteAsync($$"""
            function Get-ParitySlow { param([string] $Value) }
            Register-ArgumentCompleter -CommandName Get-ParitySlow -ParameterName Value -ScriptBlock {
                Start-Sleep -Milliseconds {{startupDelayMilliseconds}}
                $Host.UI.WriteLine('argument-completer-started')
                Start-Sleep -Seconds 30
                $Host.UI.WriteLine('argument-completer-finished')
                'slow'
            }
            """);
        const string text = "Get-ParitySlow -Value ";
        using var timeout = new CancellationTokenSource();
        var completion = session.CompleteAsync(text, text.Length, timeout.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(15));
            timeout.CancelAfter(TimeSpan.FromMilliseconds(200));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(finished.Task.IsCompleted, "The argument completer finished instead of being stopped by the timeout.");
        }
        finally { timeout.Cancel(); }
        Assert.NotEmpty((await session.CompleteAsync("Get-Process", 11)).Matches);
        await session.ExecuteAsync("'still ready'");
        Assert.Equal(SessionState.Ready, session.State);
    }

    [AvaloniaFact]
    public async Task ConsoleCompletionAcceptsEnterIntoTheEditableTailWithoutExecuting()
    {
        var window = new MainWindow([], initializeOnOpen: false, preferences: new UserSettings { ConsoleIntelliSense = false });
        var session = new SessionModel("test");
        await session.Engine.InitializeAsync();
        try
        {
            await session.Engine.ExecuteAsync("function Get-ParityUniqueCommand { 'executed' }");
            await session.Engine.CompleteAsync("Get-ParityUni", 13);
            window.Workbench.Sessions.Add(session);
            window.Workbench.SelectedSession = session;
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var editor = window.FindEditor("ConsoleEditor")!;
            session.Input = "Get-ParityUni";
            editor.CaretOffset = editor.Document.TextLength;
            editor.TextArea.Focus();
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.Control);
            await Task.Delay(1000);
            Dispatcher.UIThread.RunJobs();
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Assert.Equal("Get-ParityUniqueCommand", session.Input);
            Assert.Equal(SessionState.Ready, session.Engine.State);
            Assert.DoesNotContain(session.OutputSpans, s => s.Kind == OutputKind.Command && editor.Document.GetText(s.Start, s.End - s.Start).Contains("Get-ParityUni\n"));
        }
        finally { await session.Engine.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task ScriptEnterPreferenceInsertsANewlineInsteadOfAcceptingCompletion()
    {
        var window = new MainWindow([], initializeOnOpen: false, preferences: new UserSettings
        {
            ScriptIntelliSense = false, ScriptCompletionOnEnter = false
        });
        var session = new SessionModel("test");
        await session.Engine.InitializeAsync();
        try
        {
            await session.Engine.ExecuteAsync("function Get-ParityUniqueCommand { 'executed' }");
            await session.Engine.CompleteAsync("Get-ParityUni", 13);
            var file = new ScriptTab(new ScriptFile("test.ps1") { Text = "Get-ParityUni" });
            session.Files.Add(file); session.SelectedFile = file;
            window.Workbench.Sessions.Add(session); window.Workbench.SelectedSession = session;
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var editor = window.FindEditor("ScriptEditor")!;
            editor.CaretOffset = editor.Document.TextLength;
            editor.TextArea.Focus();
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.Control);
            await Task.Delay(1000);
            Dispatcher.UIThread.RunJobs();
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Assert.StartsWith("Get-ParityUni", editor.Text);
            Assert.DoesNotContain("UniqueCommand", editor.Text);
            Assert.Contains("\n", editor.Text);
        }
        finally { await session.Engine.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task BraceShortcutsNavigateAndSelectInBothEditors()
    {
        var window = new MainWindow([], initializeOnOpen: false);
        var session = new SessionModel("test");
        await session.Engine.InitializeAsync();
        try
        {
            var file = new ScriptTab(new ScriptFile("test.ps1") { Text = "if ($true) {\n    'text'\n}" });
            session.Files.Add(file); session.SelectedFile = file;
            window.Workbench.Sessions.Add(session); window.Workbench.SelectedSession = session;
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var editor = window.FindEditor("ScriptEditor")!;
            editor.TextArea.Focus();
            editor.CaretOffset = 11;
            window.KeyPressQwerty(PhysicalKey.BracketRight, RawInputModifiers.Control);
            Assert.Equal(editor.Document.TextLength - 1, editor.CaretOffset);
            window.KeyPressQwerty(PhysicalKey.BracketRight, RawInputModifiers.Control | RawInputModifiers.Shift);
            Assert.Equal("{\n    'text'\n}", editor.SelectedText);
            var console = window.FindEditor("ConsoleEditor")!;
            session.Input = "@(1, 2)";
            console.TextArea.Focus();
            console.CaretOffset = session.Console.InputStart + 1;
            window.KeyPressQwerty(PhysicalKey.BracketRight, RawInputModifiers.Control);
            Assert.Equal(console.Document.TextLength - 1, console.CaretOffset);
            session.Input = "{}";
            console.CaretOffset = session.Console.InputStart + 1;
            window.KeyPressQwerty(PhysicalKey.BracketRight, RawInputModifiers.Control);
            Assert.Equal(session.Console.InputStart, console.CaretOffset);
        }
        finally { await session.Engine.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public void ReplaceNextUsesTheSelectedMatchAndDoesNotReplaceTheSameOccurrenceRepeatedly()
    {
        var editor = new TextEditor { Text = "one one" };
        editor.Select(0, 3);
        var dialog = new ReplaceWindow(editor);
        try
        {
            dialog.Show();
            dialog.FindControl<TextBox>("ReplacementText")!.Text = "two";
            dialog.FindControl<CheckBox>("WrapAround")!.IsChecked = false;
            Dispatcher.UIThread.RunJobs();
            var next = dialog.FindControl<Button>("ReplaceNext")!;
            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("two one", editor.Text);
            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("two two", editor.Text);
            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(UiText.Get("NoMatch"), dialog.FindControl<TextBlock>("ReplaceStatus")!.Text);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public async Task AutomaticVariableCompletionFiltersWhileTypingAndEscapeOnlyDismissesTheList()
    {
        var window = new MainWindow([], initializeOnOpen: false);
        var session = new SessionModel("test");
        await session.Engine.InitializeAsync();
        try
        {
            await session.Engine.ExecuteAsync("$ParityAutomaticVariable = 42");
            await session.Engine.CompleteAsync("$", 1);
            window.Workbench.Sessions.Add(session); window.Workbench.SelectedSession = session;
            window.Show(); Dispatcher.UIThread.RunJobs();
            var editor = window.FindEditor("ConsoleEditor")!;
            editor.CaretOffset = editor.Document.TextLength;
            editor.TextArea.Focus();
            window.KeyTextInput("$");
            await Task.Delay(1000);
            window.KeyTextInput("ParityAutomatic");
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Assert.Equal("$ParityAutomaticVariable", session.Input);
            Assert.Equal(SessionState.Ready, session.Engine.State);
            session.Input = "";
            editor.CaretOffset = editor.Document.TextLength;
            window.KeyTextInput("$");
            await Task.Delay(1000);
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.Equal("$", session.Input);
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.Equal("", session.Input);
        }
        finally { await session.Engine.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task ConsoleTabCyclesForwardAndBackwardWithoutDuplicatingThePrefix()
    {
        var window = new MainWindow([], initializeOnOpen: false, preferences: new UserSettings { ConsoleIntelliSense = false });
        var session = new SessionModel("test");
        await session.Engine.InitializeAsync();
        try
        {
            await session.Engine.ExecuteAsync("function Get-ParityCycleA {}; function Get-ParityCycleB {}");
            await session.Engine.CompleteAsync("Get-ParityCycle", 15);
            window.Workbench.Sessions.Add(session); window.Workbench.SelectedSession = session;
            window.Show(); Dispatcher.UIThread.RunJobs();
            var editor = window.FindEditor("ConsoleEditor")!;
            session.Input = "Get-ParityCycle";
            editor.CaretOffset = editor.Document.TextLength;
            editor.TextArea.Focus();
            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.Shift);
            await WaitUntilAsync(() => session.Input == "Get-ParityCycleB");
            Assert.Equal("Get-ParityCycleB", session.Input);
            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            Assert.Equal("Get-ParityCycleA", session.Input);
            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            Assert.Equal("Get-ParityCycleB", session.Input);
        }
        finally { await session.Engine.DisposeAsync(); window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("ConsoleEditor", false)]
    [InlineData("ScriptEditor", false)]
    [InlineData("ConsoleEditor", true)]
    [InlineData("ScriptEditor", true)]
    public async Task TypingParameterDashShowsParametersAfterCommandCompletion(string editorName, bool pauseAtCommandDash)
    {
        var window = new MainWindow([], initializeOnOpen: false);
        var session = new SessionModel("test");
        await session.Engine.InitializeAsync();
        try
        {
            await session.Engine.CompleteAsync("Get-Process -", "Get-Process -".Length);
            var file = new ScriptTab(new ScriptFile("test.ps1"));
            session.Files.Add(file); session.SelectedFile = file;
            window.Workbench.Sessions.Add(session); window.Workbench.SelectedSession = session;
            window.Show(); Dispatcher.UIThread.RunJobs();
            var editor = window.FindEditor(editorName)!;
            editor.CaretOffset = editor.Document.TextLength;
            editor.TextArea.Focus();
            foreach (var character in "Get-")
                window.KeyTextInput(character.ToString());
            if (pauseAtCommandDash)
                await WaitUntilAsync(() => Completion(window)?.CompletionList.ListBox.Items.OfType<ICompletionData>()
                    .Any(item => item.Text == "Get-Process") == true);
            foreach (var character in "Process -")
                window.KeyTextInput(character.ToString());
            await WaitUntilAsync(() => Completion(window) is { } popup && popup.CompletionList.IsVisible &&
                popup.CompletionList.ListBox.Items.OfType<ICompletionData>().Any(item => item.Text == "-Name"));
            window.KeyTextInput("N");
            window.KeyTextInput("a");
            await WaitUntilAsync(() => Completion(window)?.CompletionList.SelectedItem?.Text == "-Name");
            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            Assert.Equal("Get-Process -Name", editorName == "ConsoleEditor" ? session.Input : editor.Text);
        }
        finally { await session.Engine.DisposeAsync(); window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("ConsoleEditor", "ErrorAction ", "Continue")]
    [InlineData("ScriptEditor", "ErrorAction ", "Continue")]
    [InlineData("ConsoleEditor", "Name $", "$ParityParameterVariable")]
    [InlineData("ScriptEditor", "Name $", "$ParityParameterVariable")]
    public async Task CompletionRefreshesForValuesAndVariablesAfterAnOpenParameterList(string editorName, string suffix, string expected)
    {
        var window = new MainWindow([], initializeOnOpen: false);
        var session = new SessionModel("test");
        await session.Engine.InitializeAsync();
        try
        {
            await session.Engine.ExecuteAsync("$ParityParameterVariable = 'test'");
            await session.Engine.CompleteAsync("Get-Process -", "Get-Process -".Length);
            var file = new ScriptTab(new ScriptFile("test.ps1"));
            session.Files.Add(file); session.SelectedFile = file;
            window.Workbench.Sessions.Add(session); window.Workbench.SelectedSession = session;
            window.Show(); Dispatcher.UIThread.RunJobs();
            var editor = window.FindEditor(editorName)!;
            if (editorName == "ConsoleEditor") session.Input = "Get-Process ";
            else editor.Text = "Get-Process ";
            editor.CaretOffset = editor.Document.TextLength;
            editor.TextArea.Focus();
            window.KeyTextInput("-");
            await WaitUntilAsync(() => Completion(window)?.CompletionList.ListBox.Items.OfType<ICompletionData>()
                .Any(item => item.Text == "-Name") == true);
            foreach (var character in suffix) window.KeyTextInput(character.ToString());
            await WaitUntilAsync(() => Completion(window) is { } popup && popup.CompletionList.IsVisible &&
                popup.CompletionList.ListBox.Items.OfType<ICompletionData>().Any(item => item.Text == expected));
            Completion(window)!.CompletionList.SelectItem(expected);
            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            Assert.Equal("Get-Process -" + suffix.TrimEnd('$') + expected, editorName == "ConsoleEditor" ? session.Input : editor.Text);
        }
        finally { await session.Engine.DisposeAsync(); window.Close(); }
    }

    private static CompletionWindow? Completion(MainWindow window) => typeof(WorkbenchControl)
        .GetProperty("Completion", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .GetValue(window.Editor) as CompletionWindow;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(condition(), "Timed out waiting for the editing result.");
    }
}
