using System;
using System.Globalization;
using Avalonia.Data.Converters;
using QSideloader.Models;
using QSideloader.Properties;

namespace QSideloader.Converters;

public class UpdateStatusStringValueConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            null => null,
            InstalledGame game => game.IsUpdateAvailable
                ? string.Format(Resources.UpdateAvailableStatus, game.InstalledVersionCode, game.AvailableVersionCode)
                : Resources.UpToDate,
            _ => throw new NotSupportedException()
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}