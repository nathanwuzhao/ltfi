using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace LTFI.Converters;

/// <summary>Formats a <see cref="TimeSpan"/> as a clock string: <c>mm:ss</c>, or <c>h:mm:ss</c> past an hour.</summary>
public sealed class DurationToClockConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not TimeSpan span)
        {
            return "00:00";
        }

        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes:00}:{span.Seconds:00}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
