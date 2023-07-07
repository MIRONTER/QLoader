using System;
using System.Globalization;
using Avalonia.Data.Converters;
using QSideloader.ViewModels;

namespace QSideloader.Converters;

public class DownloadPruningPolicyValueConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            null => null,
            DownloadsPruningPolicy policy => policy switch
            {
                DownloadsPruningPolicy.KeepAll => "Keep all versions",
                DownloadsPruningPolicy.DeleteAfterInstall => "Delete after install",
                DownloadsPruningPolicy.Keep1Version => "Keep 1 version",
                DownloadsPruningPolicy.Keep2Versions => "Keep 2 versions",
                _ => throw new ArgumentOutOfRangeException(nameof(value))
            },
            _ => throw new NotSupportedException()
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            null => null,
            string str => str switch
            {
                "Keep all versions" => DownloadsPruningPolicy.KeepAll,
                "Delete after install" => DownloadsPruningPolicy.DeleteAfterInstall,
                "Keep 1 version" => DownloadsPruningPolicy.Keep1Version,
                "Keep 2 versions" => DownloadsPruningPolicy.Keep2Versions,
                _ => throw new ArgumentOutOfRangeException(nameof(value))
            },
            _ => throw new NotSupportedException()
        };
    }
}