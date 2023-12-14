using System;
using System.Collections.Generic;
using System.Text.Json;
using Avalonia.Controls.Notifications;
using QSideloader.Models;
using QSideloader.ViewModels;

namespace QSideloader.Utilities;

public static class Globals
{
    public static MainWindowViewModel? MainWindowViewModel { get; set; }

    public static SideloaderSettingsViewModel SideloaderSettingsViewModel { get; } = new();

    public static SettingsData SideloaderSettings => SideloaderSettingsViewModel.Settings;

    public static Dictionary<string, string?> Overrides { get; } = GeneralUtils.ParseOverridesConfig();

    public static Updater Updater { get; } = new();
    
    public static JsonSerializerOptions DefaultJsonSerializerOptions { get; } = new()
    {
        WriteIndented = true,
        TypeInfoResolver = JsonSerializerContext.Default
    };

    public static void ShowNotification(string title, string message, NotificationType type,
        TimeSpan? expiration = null)
    {
        MainWindowViewModel?.ShowNotification(title, message, type, expiration);
    }

    public static void ShowErrorNotification(Exception e, string message,
        NotificationType type = NotificationType.Error, TimeSpan? expiration = null)
    {
        MainWindowViewModel?.ShowErrorNotification(e, message, type, expiration);
    }
}