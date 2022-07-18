using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using QSideloader.ViewModels;
using Serilog.Core;
using Serilog.Events;

namespace QSideloader.Helpers;

public class PathsMaskingEnricher : ILogEventEnricher
{
    private static SideloaderSettingsViewModel? _sideloaderSettings;
    private static readonly SemaphoreSlim Semaphore = new(1, 1);
    private static Regex? _downloadsPathRegex;
    private static Regex? _backupsPathRegex;

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        _sideloaderSettings ??= Globals.SideloaderSettings;
        Semaphore.Wait();
        try
        {
            if (_downloadsPathRegex is null || _downloadsPathRegex.ToString() != Regex.Escape(_sideloaderSettings.DownloadsLocation))
                _downloadsPathRegex = new Regex(Regex.Escape(_sideloaderSettings.DownloadsLocation), RegexOptions.Compiled);
            if (_backupsPathRegex is null || _backupsPathRegex.ToString() != Regex.Escape(_sideloaderSettings.BackupsLocation))
                _backupsPathRegex = new Regex(Regex.Escape(_sideloaderSettings.BackupsLocation), RegexOptions.Compiled);
        }
        finally
        {
            Semaphore.Release();
        }
        foreach (var property in logEvent.Properties.ToList())
        {
            if (property.Value is ScalarValue {Value: string stringValue})
            {
                switch (stringValue)
                {
                    case { } s when _downloadsPathRegex.IsMatch(s):
                        logEvent.AddOrUpdateProperty(new LogEventProperty(property.Key,
                            new ScalarValue(_downloadsPathRegex.Replace(s, "_DownloadsLocation_", 1))));
                        break;
                    case { } s when _backupsPathRegex.IsMatch(s):
                        logEvent.AddOrUpdateProperty(new LogEventProperty(property.Key,
                            new ScalarValue(_backupsPathRegex.Replace(s, "_BackupsLocation_",1))));
                        break;
                }
            }
        }
    }
}