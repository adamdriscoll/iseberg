using Avalonia;
using Avalonia.Controls;

namespace Iseberg;

public sealed class TerminalOutputHost : ContentControl
{
    protected override Size MeasureOverride(Size availableSize)
    {
        // VT grids require a finite viewport, including during an unconstrained layout pass.
        return base.MeasureOverride(new Size(
            double.IsFinite(availableSize.Width) ? availableSize.Width : Bounds.Width,
            double.IsFinite(availableSize.Height) ? availableSize.Height : Bounds.Height));
    }
}
