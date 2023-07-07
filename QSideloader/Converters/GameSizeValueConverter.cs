using System;
using System.Globalization;
using Avalonia.Data.Converters;
using ByteSizeLib;

namespace QSideloader.Converters;

public class GameSizeValueConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null)
            return null;

        if (value is not int mBytes) throw new NotSupportedException();
        return ByteSize.FromMebiBytes(mBytes).ToBinaryString();
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string size) return Math.Round(ByteSize.Parse(size).MebiBytes);

        throw new NotSupportedException();
    }
}