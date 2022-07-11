using System;
using Avalonia.Controls.Notifications;
using NetSparkleUpdater;
using QSideloader.ViewModels;

namespace QSideloader.Helpers;

public static class Globals
{
    public static MainWindowViewModel? MainWindowViewModel { get; set; }

    public static SideloaderSettingsViewModel SideloaderSettings { get; } = new();

    public static SparkleUpdater? Updater { get; set; }
    
    public static void ShowNotification(string title, string message, NotificationType type, TimeSpan? expiration = null)
    {
        MainWindowViewModel?.ShowNotification(title, message, type, expiration);
    }
    
    public static void ShowErrorNotification(Exception e, string message, NotificationType type = NotificationType.Error, TimeSpan? expiration = null)
    {
        MainWindowViewModel?.ShowErrorNotification(e, message, type, expiration);
    }
}