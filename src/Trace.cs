using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace MiniClip
{
    // 只在 --trace 时开启：记录每个环节的耗时与判定结果，用于验证而不是猜
    internal static class Tracer
    {
        private static readonly object _g = new object();
        private static StreamWriter _w;
        private static long _t0;

        public static void Open()
        {
            if (!Cfg.Trace) return;
            try
            {
                Directory.CreateDirectory(Cfg.DataDir);
                _w = new StreamWriter(
                    new FileStream(Path.Combine(Cfg.DataDir, "trace.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                    new UTF8Encoding(false), 4096);
                _w.AutoFlush = true;
                _t0 = Stopwatch.GetTimestamp();
            }
            catch { _w = null; }
        }

        public static void Log(string s)
        {
            if (_w == null) return;
            lock (_g)
            {
                try
                {
                    long ms = (Stopwatch.GetTimestamp() - _t0) * 1000 / Stopwatch.Frequency;
                    _w.Write("+" + ms + "ms " + DateTime.Now.ToString("HH:mm:ss.fff") + "  ");
                    _w.WriteLine(s);
                }
                catch { }
            }
        }

        public static void Close()
        {
            if (_w == null) return;
            lock (_g) { try { _w.Dispose(); } catch { } _w = null; }
        }
    }
}
