using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace QSideloader.Utilities;

public static class ClipboardHelper
{
    public static async Task SetTextAsync(string text)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = desktop.MainWindow!;
            await mainWindow.Clipboard!.SetTextAsync(text);
        }
    }
}