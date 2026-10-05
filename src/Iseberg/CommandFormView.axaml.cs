using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Iseberg.Core;

namespace Iseberg;

public sealed partial class CommandFormView : UserControl
{
    public CommandForm? Form { get; private set; }
    public CommandFormResult? Result { get; private set; }
    public event Action? CommandChanged;
    public bool Compact { get; init; }

    public CommandFormView()
    {
        InitializeComponent();
        ParameterSetTabs.ItemTemplate = new FuncDataTemplate<CommandParameterSetDescription>((set, _) =>
            new TextBlock { Text = set?.Name == "__AllParameterSets" ? UiText.Get("DefaultParameterSet") : set?.Name });
        ParameterSetTabs.SelectionChanged += (_, _) =>
        {
            if (ParameterSetTabs.SelectedItem is CommandParameterSetDescription set)
                ParameterSetPicker.SelectedItem = set;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                ParameterSetTabs.ContainerFromIndex(ParameterSetTabs.SelectedIndex)?.BringIntoView());
        };
        ParameterSetPicker.SelectionChanged += (_, _) =>
        {
            if (Form is null || ParameterSetPicker.SelectedItem is not CommandParameterSetDescription set) return;
            Form.SelectSet(set.Name);
            ParameterSetTabs.SelectedItem = set;
            RenderParameters();
        };
        ShowMessage(UiText.Get("SelectCommand"));
    }

    public void ShowMessage(string message)
    {
        Form = null;
        Result = null;
        CommandTitle.Text = message;
        SetPanel.IsVisible = false;
        ParameterSetPicker.ItemsSource = null;
        Parameters.Children.Clear();
        CommonParameters.Children.Clear();
        CommonParameterExpander.IsVisible = false;
        CommandPreview.Text = "";
        CommandValidation.Text = "";
        CommandChanged?.Invoke();
    }

    public void ShowCommand(CommandForm form)
    {
        Form = form;
        CommandTitle.Text = form.Description.Name;
        CommandTitle.IsVisible = !Compact;
        SetPanel.IsVisible = true;
        SetPanel.Children[0].IsVisible = ParameterSetPicker.IsVisible = !Compact;
        ParameterSetTabs.IsVisible = Compact;
        ParameterSetTabScroll.IsVisible = Compact;
        ParameterFrame.BorderThickness = Compact ? new Thickness(1) : new Thickness(0);
        ParameterFrame.BorderBrush = DesktopTheme.Brush("BorderBrush");
        Parameters.Margin = Compact ? new Thickness(8,4) : new Thickness(0);
        PreviewExpander.IsVisible = !Compact;
        ((Grid)Content!).RowSpacing = Compact ? 0 : 4;
        CommonParameterExpander.Margin = Compact ? new Thickness(0,5,0,0) : new Thickness(0);
        ParameterSetTabs.ItemsSource = form.Description.ParameterSets;
        ParameterSetPicker.ItemsSource = form.Description.ParameterSets;
        ParameterSetPicker.SelectedItem = form.SelectedSet;
        RenderParameters();
    }

    public string GetCommand()
    {
        if (Result is not { IsValid: true } result)
            throw new InvalidOperationException(CommandValidation.Text ?? UiText.Get("SelectCommand"));
        return result.Script;
    }

    private void RenderParameters()
    {
        if (Form is null) return;
        Parameters.Children.Clear();
        CommonParameters.Children.Clear();
        foreach (var parameter in Form.SelectedSet.Parameters.Where(p => !p.IsCommon))
            Parameters.Children.Add(CreateParameter(parameter));
        var common = Form.SelectedSet.Parameters.Where(p => p.IsCommon).ToArray();
        if (common.Length > 0)
        {
            foreach (var parameter in common) CommonParameters.Children.Add(CreateParameter(parameter));
        }
        CommonParameterExpander.IsVisible = common.Length > 0;
        CommonParameterExpander.IsExpanded = common.Any(p => p.IsMandatory || Form.Value(p.Name).Included);
        if (Form.SelectedSet.Parameters.Count == 0)
            Parameters.Children.Add(new TextBlock { Text = UiText.Get("NoCommandParameters"), TextWrapping = TextWrapping.Wrap });
        UpdateCommand();
    }

    private Control CreateParameter(CommandParameterDescription parameter)
    {
        var value = Form!.Value(parameter.Name);
        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(4) };
        var inputPanel = new StackPanel { Spacing = 4 };
        var include = new CheckBox { Content = UiText.Get("IncludeParameter"), IsChecked = value.Included };
        AutomationProperties.SetName(include, parameter.Name + " - " + UiText.Get("IncludeParameter"));
        panel.Children.Add(include);
        if (!parameter.IsArray && parameter.Kind is (CommandParameterKind.Switch or CommandParameterKind.Boolean))
        {
            var boolean = new ComboBox { ItemsSource = new[] { "$true", "$false" }, SelectedIndex = value.Boolean ? 0 : 1,
                HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(boolean, parameter.Name);
            boolean.SelectionChanged += (_, _) =>
            {
                value.Boolean = boolean.SelectedIndex == 0;
                include.IsChecked = true;
                UpdateCommand();
            };
            inputPanel.Children.Add(boolean);
        }
        else
        {
            var input = new TextBox
            {
                Text = value.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                MinHeight = Compact ? 25 : parameter.IsArray ? 70 : 30, MaxHeight = 180
            };
            AutomationProperties.SetName(input, parameter.Name);
            var expression = new CheckBox { Content = UiText.Get("PowerShellExpression"), IsChecked = value.IsExpression };
            AutomationProperties.SetName(expression, parameter.Name + " - " + UiText.Get("PowerShellExpression"));
            Control? choice = null;
            var updatingChoices = false;
            var comparer = parameter.ChoicesIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            if (parameter.Choices.Count > 0)
            {
                if (parameter.IsArray)
                {
                    var picker = new ListBox
                    {
                        ItemsSource = parameter.Choices, SelectionMode = SelectionMode.Multiple,
                        MaxHeight = 150, MinHeight = 60
                    };
                    var selected = value.Text.Replace("\r\n", "\n").Split('\n');
                    foreach (var entry in parameter.Choices.Where(entry => selected.Contains(entry, comparer)))
                        picker.SelectedItems?.Add(entry);
                    picker.SelectionChanged += (_, _) =>
                    {
                        if (!updatingChoices) input.Text = string.Join("\n", picker.SelectedItems?.Cast<string>() ?? []);
                    };
                    choice = picker;
                }
                else
                {
                    var picker = new ComboBox { ItemsSource = parameter.Choices,
                        SelectedItem = parameter.Choices.FirstOrDefault(entry => comparer.Equals(entry, value.Text)),
                        HorizontalAlignment = HorizontalAlignment.Stretch };
                    picker.SelectionChanged += (_, _) =>
                    {
                        if (!updatingChoices && picker.SelectedItem is string selected) input.Text = selected;
                    };
                    choice = picker;
                }
                AutomationProperties.SetName(choice, parameter.Name);
                inputPanel.Children.Add(choice);
                input.IsVisible = value.IsExpression;
                choice.IsVisible = !value.IsExpression;
            }
            inputPanel.Children.Add(input);
            panel.Children.Add(expression);
            input.TextChanged += (_, _) =>
            {
                if (value.Text == (input.Text ?? "")) return;
                value.Text = input.Text ?? "";
                include.IsChecked = !Compact || value.Text.Length > 0;
                UpdateCommand();
            };
            expression.IsCheckedChanged += (_, _) =>
            {
                value.IsExpression = expression.IsChecked == true;
                if (choice is not null)
                {
                    updatingChoices = true;
                    if (choice is ComboBox picker)
                        picker.SelectedItem = parameter.Choices.FirstOrDefault(entry => comparer.Equals(entry, value.Text));
                    else if (choice is ListBox multiple)
                    {
                        multiple.SelectedItems?.Clear();
                        var selected = value.Text.Replace("\r\n", "\n").Split('\n');
                        foreach (var entry in parameter.Choices.Where(entry => selected.Contains(entry, comparer)))
                            multiple.SelectedItems?.Add(entry);
                    }
                    updatingChoices = false;
                    choice.IsVisible = !value.IsExpression;
                    input.IsVisible = value.IsExpression;
                }
                UpdateCommand();
            };
            panel.Children.Add(new TextBlock
            {
                Text = UiText.Get(parameter.IsArray ? "ArrayParameterHint" : "ParameterValueHint"), TextWrapping = TextWrapping.Wrap
            });
        }
        include.IsCheckedChanged += (_, _) => { value.Included = include.IsChecked == true; UpdateCommand(); };
        var details = parameter.TypeName;
        if (parameter.Position is { } position) details += " | " + string.Format(UiText.Get("ParameterPosition"), position);
        if (parameter.AcceptsPipelineInput) details += " | " + UiText.Get("PipelineInput");
        if (parameter.Aliases.Count > 0) details += "\n" + UiText.Get("ParameterAliases") + " " + string.Join(", ", parameter.Aliases);
        if (parameter.Choices.Count > 0) details += "\n" + string.Join(", ", parameter.Choices);
        if (parameter.HelpMessage.Length > 0) details += "\n" + parameter.HelpMessage;
        panel.Children.Add(new TextBlock { Text = details, FontSize = 11, TextWrapping = TextWrapping.Wrap });
        if (Compact)
        {
            var row = new Grid { ColumnDefinitions = new("42*,58*"), RowDefinitions = new("Auto,Auto"), ColumnSpacing = 6 };
            Control label;
            if (parameter.Kind == CommandParameterKind.Switch && !parameter.IsArray)
            {
                panel.Children.Insert(1, inputPanel);
                var check = new CheckBox { Content = parameter.Name, IsChecked = value.Included && value.Boolean,
                    Margin = new Thickness(0,3) };
                AutomationProperties.SetName(check, parameter.Name);
                check.IsCheckedChanged += (_, _) =>
                {
                    value.Included = check.IsChecked == true;
                    value.Boolean = true;
                    include.IsChecked = value.Included;
                    UpdateCommand();
                };
                label = check;
                Grid.SetColumnSpan(check, 2);
            }
            else
            {
                label = new TextBlock { Text = parameter.Name + ":" + (parameter.IsMandatory ? " *" : ""),
                    VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(inputPanel, 1); row.Children.Add(inputPanel);
            }
            row.Children.Add(label);
            var advanced = new Expander
            {
                Header = UiText.Get("ParameterOptions"), Content = panel, IsVisible = false,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var options = new MenuItem { Header = UiText.Get("ParameterOptions") };
            options.Click += (_, _) =>
            {
                advanced.IsVisible = true;
                advanced.IsExpanded = !advanced.IsExpanded;
                foreach (var input in inputPanel.Children.OfType<TextBox>())
                    input.Height = advanced.IsExpanded ? parameter.IsArray ? 70 : 50 : 25;
            };
            row.ContextMenu = new ContextMenu { Items = { options } };
            ToolTip.SetTip(row, details + "\n" + UiText.Get("ParameterOptionsHint"));
            Grid.SetRow(advanced, 1); Grid.SetColumnSpan(advanced, 2); row.Children.Add(advanced);
            AutomationProperties.SetName(row, parameter.Name);
            return row;
        }
        panel.Children.Insert(1, inputPanel);
        var expander = new Expander
        {
            Header = parameter.Name + (parameter.IsMandatory ? " *" : ""), Content = panel,
            IsExpanded = parameter.IsMandatory || value.Included, HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(expander, parameter.Name + (parameter.IsMandatory ? " - " + UiText.Get("RequiredParameter") : ""));
        return expander;
    }

    private void UpdateCommand()
    {
        Result = Form?.Build();
        CommandPreview.Text = Result?.Script ?? "";
        CommandValidation.Text = Result is null ? "" : string.Join("\n", new[]
        {
            Result.MissingParameters.Count == 0 ? "" : string.Format(UiText.Get("MissingParameters"), string.Join(", ", Result.MissingParameters)),
            Result.InvalidExpressions.Count == 0 ? "" : string.Format(UiText.Get("InvalidParameterExpressions"), string.Join(", ", Result.InvalidExpressions)),
            Result.InvalidChoices.Count == 0 ? "" : string.Format(UiText.Get("InvalidParameterChoices"), string.Join(", ", Result.InvalidChoices))
        }.Where(text => text.Length > 0));
        CommandValidation.IsVisible = !Compact && !string.IsNullOrEmpty(CommandValidation.Text);
        ToolTip.SetTip(this, CommandValidation.Text);
        CommandChanged?.Invoke();
    }
}
