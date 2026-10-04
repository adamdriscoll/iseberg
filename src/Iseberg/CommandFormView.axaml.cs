using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Iseberg.Core;

namespace Iseberg;

public sealed partial class CommandFormView : UserControl
{
    public CommandForm? Form { get; private set; }
    public CommandFormResult? Result { get; private set; }
    public event Action? CommandChanged;

    public CommandFormView()
    {
        InitializeComponent();
        ParameterSetPicker.SelectionChanged += (_, _) =>
        {
            if (Form is null || ParameterSetPicker.SelectedItem is not CommandParameterSetDescription set) return;
            Form.SelectSet(set.Name);
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
        CommandPreview.Text = "";
        CommandValidation.Text = "";
        CommandChanged?.Invoke();
    }

    public void ShowCommand(CommandForm form)
    {
        Form = form;
        CommandTitle.Text = form.Description.Name;
        SetPanel.IsVisible = true;
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
        foreach (var parameter in Form.SelectedSet.Parameters.Where(p => !p.IsCommon))
            Parameters.Children.Add(CreateParameter(parameter));
        var common = Form.SelectedSet.Parameters.Where(p => p.IsCommon).ToArray();
        if (common.Length > 0)
        {
            var panel = new StackPanel { Spacing = 4 };
            foreach (var parameter in common) panel.Children.Add(CreateParameter(parameter));
            Parameters.Children.Add(new Expander
            {
                Header = UiText.Get("CommonParameters"), Content = panel, HorizontalAlignment = HorizontalAlignment.Stretch,
                IsExpanded = common.Any(p => p.IsMandatory || Form.Value(p.Name).Included)
            });
        }
        if (Form.SelectedSet.Parameters.Count == 0)
            Parameters.Children.Add(new TextBlock { Text = UiText.Get("NoCommandParameters"), TextWrapping = TextWrapping.Wrap });
        UpdateCommand();
    }

    private Expander CreateParameter(CommandParameterDescription parameter)
    {
        var value = Form!.Value(parameter.Name);
        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(4) };
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
            panel.Children.Add(boolean);
        }
        else
        {
            var input = new TextBox
            {
                Text = value.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                MinHeight = parameter.IsArray ? 70 : 30, MaxHeight = 180
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
                panel.Children.Add(choice);
                input.IsVisible = value.IsExpression;
                choice.IsVisible = !value.IsExpression;
            }
            panel.Children.Add(input);
            panel.Children.Add(expression);
            input.TextChanged += (_, _) =>
            {
                if (value.Text == (input.Text ?? "")) return;
                value.Text = input.Text ?? "";
                include.IsChecked = true;
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
        CommandChanged?.Invoke();
    }
}
