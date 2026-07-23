// DepotDownloader/Ansi.cs 的替身实现:
// 原版通过 ANSI 转义序列向终端汇报进度;这里改为抛出事件,
// 供 JobManager 换算成 Web UI 的进度条,是全程字节级进度的唯一钩子。
using System;

namespace DepotDownloader
{
    static class Ansi
    {
        public enum ProgressState
        {
            Hidden = 0,
            Default = 1,
            Error = 2,
            Indeterminate = 3,
            Warning = 4,
        }

        /// <summary>ContentDownloader 每完成一个 chunk 调用一次 (已下载未压缩字节, 总字节)。</summary>
        public static event Action<ulong, ulong> BytesProgress;

        public static void Init()
        {
        }

        public static void Progress(ulong downloaded, ulong total)
        {
            BytesProgress?.Invoke(downloaded, total);
        }

        public static void Progress(ProgressState state, byte progress = 0)
        {
            // 状态类进度(不确定/隐藏)对 Web UI 无意义,忽略
        }
    }
}
