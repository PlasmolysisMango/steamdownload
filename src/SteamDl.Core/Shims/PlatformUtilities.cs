// DepotDownloader/PlatformUtilities.cs 的替身实现:
// 原版含 Windows CsWin32 PInvoke(控制台检测/弹窗),这里只保留
// ContentDownloader 实际用到的 SetExecutable(Unix 可执行权限)。
using System;
using System.IO;

namespace DepotDownloader
{
    static class PlatformUtilities
    {
        public static void SetExecutable(string path, bool value)
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            const UnixFileMode ModeExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

            var mode = File.GetUnixFileMode(path);
            var hasExecuteMask = (mode & ModeExecute) == ModeExecute;
            if (hasExecuteMask == value)
            {
                return;
            }

            File.SetUnixFileMode(path, value ? mode | ModeExecute : mode & ~ModeExecute);
        }
    }
}
