using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using DigitalCenter.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalCenter.App.Converters;

/// <summary>True → Visible, false → Collapsed.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;
        if (Invert)
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Two-way binding between an enum property (e.g. LibraryTab) and a
/// RadioButton IsChecked. The parameter is the enum member name.
/// </summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null || parameter is null)
        {
            return false;
        }

        return string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var isChecked = value is bool b && b;
        if (!isChecked || parameter is null || targetType is null)
        {
            return Binding.DoNothing;
        }

        var enumType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        return enumType.IsEnum
            ? Enum.Parse(enumType, parameter.ToString()!)
            : Binding.DoNothing;
    }
}

/// <summary>Null (or empty string) → Collapsed; with Invert=true → Visible.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var isNull = value is null || (value is string s && s.Length == 0);
        if (Invert)
        {
            isNull = !isNull;
        }

        return isNull ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Zero (or null) → Collapsed — used for the sidebar cart badge pill.</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var count = value switch
        {
            int i => i,
            long l => l,
            _ => 0
        };

        return count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>True → false, false → true (RadioButton binding against a bool).</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b ? !b : true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b ? !b : false;
}

/// <summary>"Grid"/"Table" strings from a ComboBox selection index.</summary>
public sealed class IndexToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var index = value is int i ? i : 0;
        var target = int.Parse(parameter.ToString()!, CultureInfo.InvariantCulture);
        return index == target;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var isChecked = value is bool b && b;
        if (!isChecked)
        {
            return Binding.DoNothing;
        }

        return int.Parse(parameter.ToString()!, CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Loads a cached poster file (MediaTitle.PosterPath, resolved against the
/// application poster cache) as a frozen, downscaled BitmapImage. Images are
/// cached per path so scrolling the poster grid never re-decodes files.
/// </summary>
public sealed class PosterPathToImageConverter : IValueConverter
{
    private static readonly ConcurrentDictionary<string, BitmapImage?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string posterPath || posterPath.Length == 0)
        {
            return null;
        }

        return Cache.GetOrAdd(posterPath, LoadPoster);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static BitmapImage? LoadPoster(string posterPath)
    {
        try
        {
            var app = Application.Current as App;
            var paths = app?.Services?.GetService<AppPaths>();
            if (paths is null)
            {
                return null;
            }

            var absolute = Path.Combine(paths.PostersDirectory, posterPath);
            if (!File.Exists(absolute))
            {
                return null;
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 320; // Cards render up to ~200 DIP wide; keeps memory small and art crisp.
            image.UriSource = new Uri(absolute, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>Map a status key (Ok/Bad/Active/Neutral/Pending) to a brush for status pills.</summary>
public sealed class StatusKeyToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value as string ?? string.Empty;
        return key switch
        {
            "Ok" => FindBrush("OkBrush"),
            "Bad" => FindBrush("DangerBrush"),
            "Active" => FindBrush("PrimaryBrush"),
            "Pending" => FindBrush("WarnBrush"),
            _ => FindBrush("SubTextBrush")
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static object FindBrush(string key) =>
        Application.Current?.TryFindResource(key) ?? System.Windows.Media.Brushes.Gray;
}
