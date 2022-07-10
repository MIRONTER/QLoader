using NetSparkleUpdater;
using QSideloader.ViewModels;

namespace QSideloader.Helpers;

public static class Globals
{
    public static MainWindowViewModel? MainWindowViewModel { get; set; }

    public static SideloaderSettingsViewModel SideloaderSettings { get; } = new();

    public static SparkleUpdater? Updater { get; set; }
}