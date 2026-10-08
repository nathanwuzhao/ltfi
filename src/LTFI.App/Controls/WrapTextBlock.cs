using System;
using Avalonia;
using Avalonia.Controls;

namespace LTFI.Controls;

/// <summary>
/// A <see cref="TextBlock"/> for wrapped text that never loses its last line to layout rounding.
/// <para>
/// Why: <see cref="TextBlock"/> re-lays out its text in <c>ArrangeOverride</c> with the arranged
/// height as a hard <c>MaxHeight</c>, and <c>TextLayout</c> drops any line for which
/// <c>height + lineHeight &gt; MaxHeight</c>. Layout rounding (<c>LayoutHelper.RoundLayoutSizeUp</c>)
/// truncates to 8 decimals before rounding up, so a height a few ULPs above a device-pixel boundary
/// is rounded <em>down</em>. JetBrains Mono at FontSize 10 has a 13.200000000000001 line; 2 lines =
/// 26.400000000000002, which at 125% scaling rounds to 26.4 — and the 2nd line disappears while
/// its space stays reserved (also 4/8 lines at 10, 5 lines at 16; nothing at 100%/150%).
/// </para>
/// <para>
/// Fix: lay the text out with a hair (0.01 DIP, far below any line height) of extra height.
/// The control's bounds are unchanged — <c>ArrangeCore</c> clamps the returned size.
/// Styles keep applying because the style key stays <see cref="TextBlock"/>.
/// </para>
/// </summary>
public class WrapTextBlock : TextBlock
{
    private const double HeightTolerance = 0.01;

    static WrapTextBlock()
    {
        TextWrappingProperty.OverrideDefaultValue<WrapTextBlock>(Avalonia.Media.TextWrapping.Wrap);
    }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    protected override Size ArrangeOverride(Size finalSize) =>
        base.ArrangeOverride(finalSize.WithHeight(finalSize.Height + HeightTolerance));
}
