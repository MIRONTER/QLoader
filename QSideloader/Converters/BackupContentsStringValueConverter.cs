using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using QSideloader.Models;
using QSideloader.Properties;

namespace QSideloader.Converters;

public class BackupContentsStringValueConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        switch (value)
        {
            case null:
                return null;
            case Backup backup:
            {
                var contentsList = new List<string>();
                if (backup.ContainsApk)
                    contentsList.Add("Apk");
                if (backup.ContainsObb)
                    contentsList.Add("Obb");
                if (backup.ContainsSharedData || backup.ContainsPrivateData)
                    contentsList.Add(Resources.Data);
                return string.Join(", ", contentsList);
            }
            default:
                throw new NotSupportedException();
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}