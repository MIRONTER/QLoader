using System;
using System.Globalization;
using Avalonia.Data.Converters;
using QSideloader.Properties;

namespace QSideloader.Converters;

public class OnDeviceStatusValueConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null)
            return null;

        if (value is not bool status) throw new NotSupportedException();
        return status ? Resources.OnDeviceHeader : "";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string status) throw new NotSupportedException();
        if (status == Resources.OnDeviceHeader)
            return true;
        if (status == "")
            return false;

        throw new NotSupportedException();
    }
}