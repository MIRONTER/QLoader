using System;
using System.IO;

namespace QSideloader.Common
{
    public static class CommonUtils
    {
        public static void TrySetExecutableBit(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException(filePath);
            if (OperatingSystem.IsWindows())
                return;
            try
            {
                var mode = File.GetUnixFileMode(filePath);
                if (mode.HasFlag(UnixFileMode.UserExecute))
                    return;
                mode |= UnixFileMode.UserExecute;
                File.SetUnixFileMode(filePath, mode);
            }
            catch
            {
                // ignored
            }
        }
    }
}