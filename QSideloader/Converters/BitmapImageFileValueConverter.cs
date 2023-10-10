using System;
using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace QSideloader.Converters;

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
        /*return value switch
        {
            null => null,
            string path when targetType.IsAssignableFrom(typeof(Bitmap)) => File.Exists(path) ? new Bitmap(path) : null,
            _ => throw new NotSupportedException()
        };*/
        if (value is null) return null;
        if (value is not string path) throw new NotSupportedException();
        try
        {
            return File.Exists(path) ? new Bitmap(path) : null;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}