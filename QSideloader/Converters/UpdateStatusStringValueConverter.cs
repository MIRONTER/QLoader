using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using QSideloader.Models;
using QSideloader.Properties;

namespace QSideloader.Converters;

public class UpdateStatusStringValueConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null)
            return null;

        if (value is InstalledGame game)
        {
            return game.AvailableVersionCode > game.InstalledVersionCode 
                ? string.Format(Resources.UpdateStatusUpdateAvailable, game.InstalledVersionCode, game.AvailableVersionCode)
                : Resources.UpdateStatusUpToDate;
        }

        throw new NotSupportedException();
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}