using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;

namespace Iseberg;

public static class DesktopTheme
{
    public static bool HighContrast { get; private set; }
    public static double TextScale { get; private set; } = 1;
    public static event Action? Changed;

    public static void Refresh(bool? highContrast = null)
    {
        var contrast = new HighContrastInfo { Size = (uint)Marshal.SizeOf<HighContrastInfo>() };
        HighContrast = highContrast ?? (OperatingSystem.IsWindows() &&
            SystemParametersInfo(0x0042, contrast.Size, ref contrast, 0) && (contrast.Flags & 1) != 0);
        var resources = Application.Current!.Resources;
        var metrics = new NonClientMetrics { Size = (uint)Marshal.SizeOf<NonClientMetrics>() };
        var hasMetrics = OperatingSystem.IsWindows() && SystemParametersInfo(0x0029, metrics.Size, ref metrics, 0);
        var dpi = OperatingSystem.IsWindowsVersionAtLeast(10) ? GetDpiForSystem() : 96;
        var fontSize = hasMetrics ? Math.Max(12, Math.Abs(metrics.MessageFont.Height) * 96.0 / dpi) : 12;
        TextScale = fontSize / 12;
        resources["DesktopFontFamily"] = new FontFamily(hasMetrics && !string.IsNullOrWhiteSpace(metrics.MessageFont.FaceName)
            ? metrics.MessageFont.FaceName : "Segoe UI, Noto Sans, DejaVu Sans");
        resources["DesktopFontSize"] = fontSize;
        void Brush(string name, int index, string fallback)
        {
            var rgb = OperatingSystem.IsWindows() ? GetSysColor(index) : 0;
            resources[name] = new SolidColorBrush(OperatingSystem.IsWindows()
                ? Color.FromRgb((byte)rgb, (byte)(rgb >> 8), (byte)(rgb >> 16)) : Color.Parse(fallback));
        }
        Brush("WindowBrush", 5, "#FFFFFF");
        Brush("WindowTextBrush", 8, "#000000");
        Brush("ControlBrush", 15, "#F0F0F0");
        Brush("ControlTextBrush", 18, "#000000");
        Brush("BorderBrush", 16, "#A0A0A0");
        Brush("DisabledTextBrush", 17, "#808080");
        Brush("SelectionBrush", 13, "#0078D7");
        Brush("SelectionTextBrush", 14, "#FFFFFF");
        resources["HoverBrush"] = HighContrast ? resources["SelectionBrush"]! : new SolidColorBrush(Color.Parse("#DCEAF8"));
        resources["InputBrush"] = HighContrast ? resources["WindowBrush"]! : new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = [new GradientStop(Color.Parse("#F0F0F0"), 0), new GradientStop(Color.Parse("#E5E5E5"), 1)]
        };
        resources["DialogBrush"] = resources["WindowBrush"]!;
        foreach (var (key, color) in new Dictionary<string, string>
        {
            ["OptionsFrameBrush"] = "#ACACAC", ["OptionsTreeBrush"] = "#828790",
            ["OptionsGroupBrush"] = "#808080", ["OptionsInputBorderBrush"] = "#ABADB3",
            ["OptionsButtonBorderBrush"] = "#707070", ["OptionsDisabledBorderBrush"] = "#ADB2B5"
        })
            resources[key] = HighContrast ? resources["BorderBrush"]! : new SolidColorBrush(Color.Parse(color));
        resources["OptionsButtonBrush"] = HighContrast ? resources["ControlBrush"]! : new SolidColorBrush(Color.Parse("#DDDDDD"));
        resources["RedChannelBrush"] = HighContrast ? resources["WindowBrush"]! : new SolidColorBrush(Color.Parse("#F08080"));
        resources["GreenChannelBrush"] = HighContrast ? resources["WindowBrush"]! : new SolidColorBrush(Color.Parse("#80B080"));
        resources["BlueChannelBrush"] = HighContrast ? resources["WindowBrush"]! : new SolidColorBrush(Color.Parse("#8080F0"));
        foreach (var key in new[] { "ThemeBackgroundBrush", "ThemeControlLowBrush", "ThemeControlMidBrush", "ThemeControlHighBrush", "ThemeControlVeryHighBrush" })
            resources[key] = resources["ControlBrush"]!;
        resources["ThemeForegroundBrush"] = resources["ControlTextBrush"]!;
        foreach (var key in new[] { "ThemeBorderLowBrush", "ThemeBorderMidBrush", "ThemeBorderHighBrush" })
            resources[key] = resources["BorderBrush"]!;
        resources["ThemeAccentBrush"] = resources["SelectionBrush"]!;
        resources["ThemeAccentForegroundBrush"] = resources["SelectionTextBrush"]!;
        Changed?.Invoke();
    }

    public static IBrush Brush(string name) => (IBrush)Application.Current!.Resources[name]!;

    public static void Start()
    {
        Refresh();
        if (Application.Current?.PlatformSettings is { } platform)
            platform.ColorValuesChanged += (_, _) => Dispatcher.UIThread.Post(() => Refresh());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HighContrastInfo { public uint Size; public uint Flags; public IntPtr Scheme; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LogFont
    {
        public int Height, Width, Escapement, Orientation, Weight;
        public byte Italic, Underline, StrikeOut, CharacterSet, OutPrecision, ClipPrecision, Quality, PitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string? FaceName;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NonClientMetrics
    {
        public uint Size;
        public int BorderWidth, ScrollWidth, ScrollHeight, CaptionWidth, CaptionHeight;
        public LogFont CaptionFont;
        public int SmallCaptionWidth, SmallCaptionHeight;
        public LogFont SmallCaptionFont;
        public int MenuWidth, MenuHeight;
        public LogFont MenuFont, StatusFont, MessageFont;
        public int PaddedBorderWidth;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, ref HighContrastInfo info, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, ref NonClientMetrics info, uint flags);
    [DllImport("user32.dll")]
    private static extern uint GetSysColor(int index);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();
}
