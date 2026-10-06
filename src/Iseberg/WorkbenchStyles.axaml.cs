using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace Iseberg;

internal sealed partial class WorkbenchStyles : Styles
{
    public WorkbenchStyles() => AvaloniaXamlLoader.Load(this);
}
