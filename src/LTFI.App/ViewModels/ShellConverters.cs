using Avalonia.Data.Converters;
using Avalonia.Media;

namespace LTFI.ViewModels;

/// <summary>
/// Small bool→brush converters for the bottom-pinned Settings rail item, which lives outside the
/// nav <c>ListBox</c> and so can't get selection styling for free. Colours mirror the palette in
/// App.axaml (BgRailActive / Accent / TextBright / TextFaint).
/// </summary>
public static class ShellConverters
{
    private static readonly IBrush RailActive = new SolidColorBrush(Color.FromRgb(0x15, 0x1B, 0x24));
    private static readonly IBrush Accent = new SolidColorBrush(Color.FromRgb(0x46, 0xD1, 0x7F));
    private static readonly IBrush Bright = new SolidColorBrush(Color.FromRgb(0xEE, 0xF1, 0xF5));
    private static readonly IBrush Faint = new SolidColorBrush(Color.FromRgb(0x5B, 0x63, 0x6F));

    public static readonly IValueConverter RailActiveBg =
        new FuncValueConverter<bool, IBrush>(active => active ? RailActive : Brushes.Transparent);

    public static readonly IValueConverter RailActiveBorder =
        new FuncValueConverter<bool, IBrush>(active => active ? Accent : Brushes.Transparent);

    public static readonly IValueConverter RailActiveText =
        new FuncValueConverter<bool, IBrush>(active => active ? Bright : Faint);
}
