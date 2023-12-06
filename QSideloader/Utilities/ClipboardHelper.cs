using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace QSideloader.Utilities;

public static class ClipboardHelper
{
    public static Task SetTextAsync(string text)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return Task.CompletedTask;
        var mainWindow = desktop.MainWindow!;
        return mainWindow.Clipboard!.SetTextAsync(text);

    }
}