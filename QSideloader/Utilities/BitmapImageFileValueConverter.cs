using System;
using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace QSideloader.Utilities;

/// <summary>
///     <para>
///         Converts a string path to a bitmap asset.
///     </para>
///     <para>
///         The asset must be in the same assembly as the program. If it isn't,
///         specify "avares://assemblynamehere/" in front of the path to the asset.
///     </para>
/// </summary>
public class BitmapImageFileValueConverter : IValueConverter
{
    public static BitmapImageFileValueConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null)
            return null;

        if (value is string path && targetType.IsAssignableFrom(typeof(Bitmap)))
            return File.Exists(path) ? new Bitmap(path) : null;

        throw new NotSupportedException();
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}