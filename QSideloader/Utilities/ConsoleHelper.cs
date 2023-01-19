using System.Runtime.InteropServices;
using Serilog;

namespace QSideloader.Utilities;

// Source: https://stackoverflow.com/a/36720802 (modified)
internal static partial class ConsoleHelper {
    // ReSharper disable InconsistentNaming

    private const uint ENABLE_QUICK_EDIT = 0x0040;

    // STD_INPUT_HANDLE (DWORD): -10 is the standard input device.
    private const int STD_INPUT_HANDLE = -10;
    
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllocConsole();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GetStdHandle(int nStdHandle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(nint hConsoleHandle, out uint lpMode);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleMode(nint hConsoleHandle, uint dwMode);

    private static bool DisableQuickEdit() {

        var consoleHandle = GetStdHandle(STD_INPUT_HANDLE);

        // get current console mode
        if (!GetConsoleMode(consoleHandle, out var consoleMode)) {
            // ERROR: Unable to get console mode.
            return false;
        }

        // Clear the quick edit bit in the mode flags
        consoleMode &= ~ENABLE_QUICK_EDIT;

        // set the new mode
        return SetConsoleMode(consoleHandle, consoleMode);
        // ERROR: Unable to set console mode
    }
    
    public static void AllocateConsole(bool noQuickEdit = true) {
        if (!AllocConsole()) {
            Log.Warning("Could not allocate console");
            return;
        }

        if (!noQuickEdit) return;
        if (!DisableQuickEdit()) {
            Log.Warning("Could not disable quick edit");
        }
    }
}