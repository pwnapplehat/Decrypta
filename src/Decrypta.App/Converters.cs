using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Decrypta.Core.Diagnostics;

namespace Decrypta.App;

/// <summary>Bool → Visibility, with an optional Invert flag.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool flag = value is true;
        if (Invert)
        {
            flag = !flag;
        }
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Maps a doctor check status to its signature brush.</summary>
public sealed class CheckStatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var color = value switch
        {
            CheckStatus.Ok => Color.FromRgb(0x34, 0xD3, 0x99),
            CheckStatus.Warn => Color.FromRgb(0xFB, 0xBF, 0x24),
            CheckStatus.Fail => Color.FromRgb(0xFB, 0x71, 0x85),
            _ => Color.FromRgb(0x6B, 0x6B, 0x80),
        };
        return new SolidColorBrush(color);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Maps a doctor check status to a short chip label.</summary>
public sealed class CheckStatusToLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            CheckStatus.Ok => "OK",
            CheckStatus.Warn => "WARN",
            CheckStatus.Fail => "FAIL",
            _ => "?",
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
