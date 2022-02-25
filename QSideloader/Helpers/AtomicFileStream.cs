// Source: https://antonymale.co.uk/windows-atomic-file-writes.html

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace QSideloader.Helpers;

public class AtomicFileStream : FileStream
{
    private readonly string _path;
    private readonly string _tempPath;

    private AtomicFileStream(string path, string tempPath, FileMode mode, FileAccess access, FileShare share,
        int bufferSize, FileOptions options)
        : base(tempPath, mode, access, share, bufferSize, options)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _tempPath = tempPath ?? throw new ArgumentNullException(nameof(tempPath));
    }

    public static AtomicFileStream Open(string path, FileMode mode, FileAccess access, FileShare share,
        int bufferSize, FileOptions options)
    {
        return Open(path, path + ".tmp", mode, access, share, bufferSize, options);
    }

    public static AtomicFileStream Open(string path, string tempPath, FileMode mode, FileAccess access,
        FileShare share, int bufferSize, FileOptions options)
    {
        if (access == FileAccess.Read)
            throw new ArgumentException(
                "If you're just opening the file for reading, AtomicFileStream won't help you at all");

        if (File.Exists(tempPath))
            File.Delete(tempPath);

        if (File.Exists(path) &&
            (mode == FileMode.Append || mode == FileMode.Open || mode == FileMode.OpenOrCreate))
            File.Copy(path, tempPath);

        return new AtomicFileStream(path, tempPath, mode, access, share, bufferSize, options);
    }

    public override void Close()
    {
        base.Close();

        var success = NativeMethods.MoveFileEx(_tempPath, _path,
            MoveFileFlags.ReplaceExisting | MoveFileFlags.WriteThrough);
        if (!success)
            Marshal.ThrowExceptionForHR(Marshal.GetLastWin32Error());
    }

    [Flags]
    private enum MoveFileFlags
    {
        None = 0,
        ReplaceExisting = 1,
        CopyAllowed = 2,
        DelayUntilReboot = 4,
        WriteThrough = 8,
        CreateHardlink = 16,
        FailIfNotTrackable = 32
    }

    private static class NativeMethods
    {
        [DllImport("Kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool MoveFileEx(
            [In] string lpExistingFileName,
            [In] string lpNewFileName,
            [In] MoveFileFlags dwFlags);
    }
}