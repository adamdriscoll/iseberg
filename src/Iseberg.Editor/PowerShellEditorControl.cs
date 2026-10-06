using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Search;

namespace Iseberg.Editor;

/// <summary>Engine-independent text editing. All operations and document mutations require the UI thread.</summary>
public sealed class PowerShellEditorControl : UserControl, IDisposable
{
    public static readonly DirectProperty<PowerShellEditorControl, TextDocument> DocumentProperty =
        AvaloniaProperty.RegisterDirect<PowerShellEditorControl, TextDocument>(nameof(Document), control => control.Document,
            (control, value) => control.Document = value);
    public static readonly StyledProperty<bool> IsReadOnlyProperty =
        AvaloniaProperty.Register<PowerShellEditorControl, bool>(nameof(IsReadOnly));
    public static readonly StyledProperty<bool> EnableExecutionGesturesProperty =
        AvaloniaProperty.Register<PowerShellEditorControl, bool>(nameof(EnableExecutionGestures));
    public static readonly StyledProperty<bool> EnableSyntaxHighlightingProperty =
        AvaloniaProperty.Register<PowerShellEditorControl, bool>(nameof(EnableSyntaxHighlighting), true);
    public static readonly StyledProperty<string> EditorNameProperty =
        AvaloniaProperty.Register<PowerShellEditorControl, string>(nameof(EditorName), "PowerShell script editor");

    private readonly AccessibleTextEditor editor = new() { ShowLineNumbers = true };
    private readonly SearchPanel search;
    private TextDocument document = new();
    private ITextSourceVersion? observedVersion;
    private CancellationTokenSource? analysisCancellation;
    private CancellationTokenSource? completionCancellation;
    private CompletionWindow? completionWindow;
    private IEditorCompletionProvider? completionProvider;
    private IEditorAnalysisProvider? analysisProvider;
    private EditorAnalysisResult analysis = new(0, EditorAnalysisState.Unavailable, []);
    private long version;
    private long lifetime;
    private bool attached;
    private bool disposed;
    private int savedCaret;
    private int savedSelectionStart;
    private int savedSelectionLength;

    public PowerShellEditorControl()
    {
        Dispatcher.UIThread.VerifyAccess();
        observedVersion = document.Version;
        editor.Document = document;
        search = SearchPanel.Install(editor);
        Styles.Add(EditorStyles());
        Content = editor;
        AutomationProperties.SetName(editor, EditorName);
        editor.TextArea.Caret.PositionChanged += OnCaretChanged;
        editor.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        ActualThemeVariantChanged += (_, _) => UpdateColors();
        UpdateColors();
    }

    public PowerShellEditorControl(TextDocument document) : this() => Document = document;

    /// <summary>Host-owned, never disposed or cleared by this control. Replacing it invalidates pending work.</summary>
    public TextDocument Document
    {
        get => document;
        set
        {
            VerifyUsable();
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(document, value)) return;
            UnsubscribeDocument();
            CancelWork();
            var previous = document;
            document = value;
            observedVersion = value.Version;
            version++;
            savedCaret = savedSelectionStart = savedSelectionLength = 0;
            editor.Document = attached ? value : null;
            InvalidateAnalysis();
            if (attached)
            {
                SubscribeDocument();
                Observe(AnalyzeCoreAsync(CancellationToken.None, debounce: true), "Analysis");
            }
            RaisePropertyChanged(DocumentProperty, previous, value);
        }
    }

    public bool IsReadOnly { get => GetValue(IsReadOnlyProperty); set => SetValue(IsReadOnlyProperty, value); }
    public bool EnableExecutionGestures { get => GetValue(EnableExecutionGesturesProperty); set => SetValue(EnableExecutionGesturesProperty, value); }
    /// <summary>Disable for host high-contrast palettes. No application resources are modified.</summary>
    public bool EnableSyntaxHighlighting { get => GetValue(EnableSyntaxHighlightingProperty); set => SetValue(EnableSyntaxHighlightingProperty, value); }
    public string EditorName { get => GetValue(EditorNameProperty); set => SetValue(EditorNameProperty, value); }
    public long DocumentVersion { get { VerifyUsable(); SynchronizeVersion(); return version; } }
    public EditorAnalysisResult Analysis
    {
        get { Dispatcher.UIThread.VerifyAccess(); if (!disposed) SynchronizeVersion(); return analysis; }
        private set => analysis = value;
    }
    public IEditorCompletionProvider? CompletionProvider
    {
        get => completionProvider;
        set { VerifyUsable(); CancelCompletion(); completionProvider = value; }
    }
    public IEditorAnalysisProvider? AnalysisProvider
    {
        get => analysisProvider;
        set
        {
            VerifyUsable();
            analysisCancellation?.Cancel();
            analysisProvider = value;
            InvalidateAnalysis();
            if (attached) Observe(AnalyzeCoreAsync(CancellationToken.None, debounce: true), "Analysis");
        }
    }

    public int CaretOffset
    {
        get { VerifyUsable(); return attached ? editor.CaretOffset : Math.Min(savedCaret, document.TextLength); }
        set
        {
            VerifyUsable();
            new EditorTextSpan(value, 0).Validate(document.TextLength);
            CancelCompletion();
            savedCaret = value;
            if (attached) editor.CaretOffset = value;
        }
    }
    public EditorTextSpan Selection
    {
        get
        {
            VerifyUsable();
            return attached ? new(editor.SelectionStart, editor.SelectionLength) :
                new(Math.Min(savedSelectionStart, document.TextLength), Math.Min(savedSelectionLength, document.TextLength - Math.Min(savedSelectionStart, document.TextLength)));
        }
    }

    public event EventHandler? AnalysisChanged;
    public event EventHandler<EditorExecutionRequestedEventArgs>? ExecutionRequested;
    /// <summary>Background/keyboard failures. Explicit awaited operations instead propagate their exceptions.</summary>
    public event EventHandler<EditorErrorEventArgs>? ErrorOccurred;

    public void Select(EditorTextSpan span)
    {
        VerifyUsable();
        span.Validate(document.TextLength);
        CancelCompletion();
        savedSelectionStart = span.Start;
        savedSelectionLength = span.Length;
        savedCaret = span.End;
        if (attached) editor.Select(span.Start, span.Length);
    }

    public bool FocusEditor()
    {
        VerifyUsable();
        return editor.TextArea.Focus();
    }

    public EditorTextSnapshot CaptureText(EditorTextScope scope = EditorTextScope.Document)
    {
        VerifyUsable();
        SynchronizeVersion();
        var span = scope switch
        {
            EditorTextScope.Document => new EditorTextSpan(0, document.TextLength),
            EditorTextScope.SelectionOrCurrentLine when Selection.Length > 0 => Selection,
            EditorTextScope.SelectionOrCurrentLine => LineSpan(),
            _ => throw new ArgumentOutOfRangeException(nameof(scope))
        };
        return new(document.GetText(span.Start, span.Length), version, span);
    }

    public Task<EditorAnalysisResult> AnalyzeAsync(CancellationToken cancellationToken = default)
    {
        VerifyUsable();
        return AnalyzeCoreAsync(cancellationToken, debounce: false);
    }

    private async Task<EditorAnalysisResult> AnalyzeCoreAsync(CancellationToken cancellationToken, bool debounce)
    {
        SynchronizeVersion();
        analysisCancellation?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        analysisCancellation = cancellation;
        var snapshot = CaptureText();
        var generation = lifetime;
        var provider = analysisProvider;
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            if (provider is null)
            {
                PublishAnalysis(new(version, EditorAnalysisState.Unavailable, []));
                return analysis;
            }
            PublishAnalysis(new(snapshot.Version, EditorAnalysisState.Pending, []));
            if (debounce) await Task.Delay(100, cancellation.Token);
            var result = await provider.AnalyzeAsync(new(snapshot.Text, snapshot.Version), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            SynchronizeVersion();
            if (disposed || generation != lifetime || snapshot.Version != version)
                throw new OperationCanceledException("The analysis snapshot is stale.");
            ArgumentNullException.ThrowIfNull(result);
            if (result.Version != snapshot.Version || result.State is not (EditorAnalysisState.Available or EditorAnalysisState.Unavailable))
                throw new InvalidOperationException("Analysis provider returned an invalid version or state.");
            if (result.Diagnostics.IsDefault || result.State == EditorAnalysisState.Unavailable && !result.Diagnostics.IsEmpty)
                throw new InvalidOperationException("Analysis provider returned invalid diagnostics.");
            foreach (var diagnostic in result.Diagnostics)
            {
                if (diagnostic is null || diagnostic.Code is null || diagnostic.Message is null || !Enum.IsDefined(diagnostic.Severity))
                    throw new InvalidOperationException("Analysis provider returned an invalid diagnostic.");
                diagnostic.Span.Validate(snapshot.Text.Length);
            }
            PublishAnalysis(result);
            cancellation.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (Exception) when (cancellation.IsCancellationRequested)
        {
            MarkAnalysisCancelled(generation, snapshot.Version, cancellation);
            throw new OperationCanceledException(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            MarkAnalysisCancelled(generation, snapshot.Version, cancellation);
            throw;
        }
        catch
        {
            if (!disposed && generation == lifetime && snapshot.Version == version)
                PublishAnalysis(new(version, EditorAnalysisState.Failed, []));
            throw;
        }
        finally
        {
            if (ReferenceEquals(analysisCancellation, cancellation)) analysisCancellation = null;
        }
    }

    private void MarkAnalysisCancelled(long generation, long snapshotVersion, CancellationTokenSource cancellation)
    {
        if (!disposed && generation == lifetime && snapshotVersion == version && ReferenceEquals(analysisCancellation, cancellation))
            PublishAnalysis(new(version, EditorAnalysisState.Unavailable, []));
    }

    /// <summary>Requests only the injected provider; validates and rejects stale results. Does not execute PowerShell.</summary>
    public async Task<EditorCompletionList> RequestCompletionAsync(CancellationToken cancellationToken = default)
    {
        VerifyUsable();
        var provider = completionProvider ?? throw new InvalidOperationException("No completion provider is configured.");
        CancelCompletion();
        var snapshot = CaptureText();
        var caret = CaretOffset;
        var generation = lifetime;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        completionCancellation = cancellation;
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            var result = await provider.CompleteAsync(new(snapshot.Text, snapshot.Version, caret), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            SynchronizeVersion();
            if (disposed || generation != lifetime || version != snapshot.Version || CaretOffset != caret)
                throw new OperationCanceledException("The completion snapshot is stale.");
            ArgumentNullException.ThrowIfNull(result);
            if (result.Version != snapshot.Version)
                throw new InvalidOperationException("Completion provider returned the wrong document version.");
            result.ReplacementSpan.Validate(snapshot.Text.Length);
            if (result.Items.IsDefault || result.Items.Any(item => item is null || item.Text is null || item.DisplayText is null))
                throw new InvalidOperationException("Completion provider returned invalid items.");
            return result;
        }
        catch (Exception) when (cancellation.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellation.Token);
        }
        finally
        {
            if (ReferenceEquals(completionCancellation, cancellation)) completionCancellation = null;
        }
    }

    public void ApplyCompletion(EditorCompletionList completion, int itemIndex)
    {
        VerifyUsable();
        ArgumentNullException.ThrowIfNull(completion);
        SynchronizeVersion();
        if (IsReadOnly) throw new InvalidOperationException("The editor is read-only.");
        if (completion.Version != version) throw new InvalidOperationException("The completion snapshot is stale.");
        completion.ReplacementSpan.Validate(document.TextLength);
        if (completion.Items.IsDefault) throw new ArgumentException("Completion items are uninitialized.", nameof(completion));
        ArgumentOutOfRangeException.ThrowIfNegative(itemIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(itemIndex, completion.Items.Length);
        var item = completion.Items[itemIndex] ?? throw new ArgumentException("Completion item is null.", nameof(completion));
        ArgumentNullException.ThrowIfNull(item.Text);
        document.Replace(completion.ReplacementSpan.Start, completion.ReplacementSpan.Length, item.Text);
        CaretOffset = completion.ReplacementSpan.Start + item.Text.Length;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (disposed) return;
        var selection = Selection;
        SynchronizeVersion();
        editor.Document = document;
        editor.Select(selection.Start, selection.Length);
        editor.CaretOffset = Math.Min(savedCaret, document.TextLength);
        attached = true;
        SubscribeDocument();
        Observe(AnalyzeCoreAsync(CancellationToken.None, debounce: true), "Analysis");
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (disposed)
        {
            attached = false;
            base.OnDetachedFromVisualTree(e);
            return;
        }
        savedCaret = editor.CaretOffset;
        savedSelectionStart = editor.SelectionStart;
        savedSelectionLength = editor.SelectionLength;
        attached = false;
        lifetime++;
        CancelWork();
        UnsubscribeDocument();
        editor.Document = null;
        InvalidateAnalysis();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsReadOnlyProperty)
        {
            editor.IsReadOnly = IsReadOnly;
            CancelCompletion();
        }
        else if (change.Property == EditorNameProperty) AutomationProperties.SetName(editor, EditorName);
        else if (change.Property == EnableSyntaxHighlightingProperty) UpdateColors();
    }

    private void UpdateColors()
    {
        editor.SyntaxHighlighting = EnableSyntaxHighlighting ? EditorHighlighting.Create(ActualThemeVariant == ThemeVariant.Dark) : null;
    }

    private EditorTextSpan LineSpan()
    {
        var line = document.GetLineByOffset(CaretOffset);
        return new(line.Offset, line.Length);
    }

    private void SubscribeDocument() { document.Changed -= OnDocumentChanged; document.Changed += OnDocumentChanged; }
    private void UnsubscribeDocument() => document.Changed -= OnDocumentChanged;
    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs args)
    {
        SynchronizeVersion();
        CancelCompletion();
        InvalidateAnalysis();
        Observe(AnalyzeCoreAsync(CancellationToken.None, debounce: true), "Analysis");
    }

    private void SynchronizeVersion()
    {
        if (observedVersion?.CompareAge(document.Version) == 0) return;
        observedVersion = document.Version;
        version++;
        InvalidateAnalysis();
    }

    private void InvalidateAnalysis()
    {
        PublishAnalysis(new(version, attached && analysisProvider is not null ? EditorAnalysisState.Pending : EditorAnalysisState.Unavailable, []));
    }

    private void PublishAnalysis(EditorAnalysisResult result)
    {
        if (analysis == result) return;
        Analysis = result;
        AnalysisChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnCaretChanged(object? sender, EventArgs args) => CancelCompletion();
    private void CancelCompletion()
    {
        completionCancellation?.Cancel();
        completionWindow?.Close();
        completionWindow = null;
    }
    private void CancelWork() { analysisCancellation?.Cancel(); CancelCompletion(); }

    private void OnKeyDown(object? sender, KeyEventArgs args)
    {
        if (disposed) return;
        if (args.Key == Key.Space && args.KeyModifiers == KeyModifiers.Control && CompletionProvider is not null && !IsReadOnly)
        {
            args.Handled = true;
            Observe(ShowCompletionAsync(), "Completion");
        }
        else if (EnableExecutionGestures && args.KeyModifiers == KeyModifiers.None && args.Key is Key.F5 or Key.F8 ||
                 EnableExecutionGestures && args.KeyModifiers == KeyModifiers.Control && args.Key == Key.Pause)
        {
            args.Handled = true;
            var kind = args.Key == Key.F5 ? EditorExecutionRequestKind.Document :
                args.Key == Key.F8 ? EditorExecutionRequestKind.SelectionOrCurrentLine : EditorExecutionRequestKind.Stop;
            ExecutionRequested?.Invoke(this, new(kind, kind == EditorExecutionRequestKind.Stop ? null :
                CaptureText(kind == EditorExecutionRequestKind.Document ? EditorTextScope.Document : EditorTextScope.SelectionOrCurrentLine)));
        }
    }

    private async Task ShowCompletionAsync()
    {
        var result = await RequestCompletionAsync();
        if (!attached || IsReadOnly || result.Items.IsEmpty) return;
        var window = new CompletionWindow(editor.TextArea)
        {
            StartOffset = result.ReplacementSpan.Start,
            EndOffset = result.ReplacementSpan.End,
            CloseWhenCaretAtBeginning = false
        };
        try
        {
            // AvaloniaEdit 12 completion is a popup, outside the editor's scoped style tree.
            window.CompletionList.Styles.Add(EditorStyles());
            if (!this.TryFindResource(typeof(CompletionList), out var theme) || theme is not ControlTheme completionTheme)
                throw new InvalidOperationException("The editor completion theme is unavailable.");
            window.CompletionList.Theme = completionTheme;
            foreach (var item in result.Items) window.CompletionList.CompletionData.Add(new CompletionData(this, result, item));
            completionWindow = window;
            window.Closed += (_, _) => { if (ReferenceEquals(completionWindow, window)) completionWindow = null; };
            window.Show();
            window.CompletionList.ApplyTemplate();
            window.CompletionList.SelectItem(document.GetText(result.ReplacementSpan.Start,
                Math.Clamp(CaretOffset - result.ReplacementSpan.Start, 0, result.ReplacementSpan.Length)));
        }
        catch
        {
            window.Close();
            throw;
        }
    }

    private static StyleInclude EditorStyles() => new(new Uri("avares://Iseberg.Editor/"))
    {
        Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml")
    };

    private async void Observe(Task task, string operation)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            ReportError(operation, exception);
        }
    }

    private void ReportError(string operation, Exception exception)
    {
        Trace.TraceError("Iseberg editor {0}: {1}", operation, exception);
        ErrorOccurred?.Invoke(this, new(operation, exception));
    }

    private void VerifyUsable()
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    public void Dispose()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (disposed) return;
        disposed = true;
        attached = false;
        lifetime++;
        CancelWork();
        UnsubscribeDocument();
        search.Uninstall();
        editor.TextArea.Caret.PositionChanged -= OnCaretChanged;
        editor.RemoveHandler(KeyDownEvent, OnKeyDown);
        editor.Document = null;
        Analysis = new(version, EditorAnalysisState.Unavailable, []);
    }

    private sealed class CompletionData(PowerShellEditorControl owner, EditorCompletionList list, EditorCompletionItem item) : ICompletionData
    {
        public Avalonia.Media.IImage? Image => null;
        public string Text => item.Text;
        public object Content => item.DisplayText;
        public object Description => item.Description ?? "";
        public double Priority => 0;
        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        {
            try { owner.ApplyCompletion(list, list.Items.IndexOf(item)); }
            catch (Exception exception) { owner.ReportError("Completion", exception); }
        }
    }
}
