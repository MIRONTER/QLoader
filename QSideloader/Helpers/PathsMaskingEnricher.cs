using System.Linq;
using System.Text.RegularExpressions;
using QSideloader.ViewModels;
using Serilog.Core;
using Serilog.Events;

namespace QSideloader.Helpers;

public class PathsMaskingEnricher : ILogEventEnricher
{
    private static SideloaderSettingsViewModel? _sideloaderSettings;
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        _sideloaderSettings ??= Globals.SideloaderSettings;
        var downloadsPathRegex = new Regex(Regex.Escape(_sideloaderSettings.DownloadsLocation));
        var backupsPathRegex = new Regex(Regex.Escape(_sideloaderSettings.BackupsLocation));
        foreach (var property in logEvent.Properties.ToList())
        {
            if (property.Value is ScalarValue {Value: string stringValue})
            {
                switch (stringValue)
                {
                    case { } s when downloadsPathRegex.IsMatch(s):
                        logEvent.AddOrUpdateProperty(new LogEventProperty(property.Key,
                            new ScalarValue(downloadsPathRegex.Replace(s, "_DownloadsLocation_", 1))));
                        break;
                    case { } s when backupsPathRegex.IsMatch(s):
                        logEvent.AddOrUpdateProperty(new LogEventProperty(property.Key,
                            new ScalarValue(backupsPathRegex.Replace(s, "_BackupsLocation_",1))));
                        break;
                }
            }
        }
    }
}