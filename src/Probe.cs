using System;
using System.Diagnostics;

namespace MiniClip
{
    // MINICLIP_DIAG=1 时打开：把内部计数器写进 trace.log，用于定位增长而不是猜
    internal static class Probe
    {
        public static bool Enabled = Environment.GetEnvironmentVariable("MINICLIP_DIAG") == "1";

        private static Process _self;
        private static uint _handles;

        public static void Counters(string where)
        {
            if (!Enabled) return;
            try
            {
                // 诊断本身不能改变被测对象：句柄数直接问内核，不新建 Process 句柄
                _handles = 0;
                if (_self == null) _self = Process.GetCurrentProcess();
                GetProcessHandleCount(_self.Handle, ref _handles);
                Tracer.Log("diag " + where + " threads=" + _self.Threads.Count +
                    " handles=" + _handles +
                    " ws_mb=" + (_self.WorkingSet64 / 1048576.0).ToString("0.0") +
                    " gc1=" + GC.CollectionCount(1) + " gc2=" + GC.CollectionCount(2));
            }
            catch { }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool GetProcessHandleCount(IntPtr h, ref uint count);
    }
}
