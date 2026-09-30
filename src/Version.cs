using System;

namespace MiniClip
{
    // 版本号的唯一来源：界面、--version、关于框、文件属性注入脚本都从这里取值
    internal static class Ver
    {
        public const string Name = "MiniClip";
        public const string DisplayName = "剪贴板历史";
        public const string V1 = "1";
        public const string V2 = "0";
        public const string V3 = "0";
        public const string V4 = "0";

        public static string Number { get { return V1 + "." + V2 + "." + V3; } }
        public static string Quad { get { return V1 + "." + V2 + "." + V3 + "." + V4; } }

        public const string Description = "轻量剪贴板历史 · 事件驱动 · 零依赖";
        public const string Company = "MiniClip";
        public const string Copyright = "Copyright © 2026";
        public const string License = "MIT License";

        // 标题栏只要名字；版本在关于框和 --version 里看
        public static string Title { get { return Name; } }

        // 反馈/复现问题时直接贴这一段，省得来回问环境
        public static string Brief(Store store)
        {
            string run = "";
            try
            {
                using (System.Diagnostics.Process p = System.Diagnostics.Process.GetCurrentProcess())
                {
                    run = "占用: working_set " + (p.WorkingSet64 / 1048576.0).ToString("0.0") + " MB，私有 "
                        + (p.PrivateMemorySize64 / 1048576.0).ToString("0.0") + " MB，CPU "
                        + (long)p.TotalProcessorTime.TotalMilliseconds + " ms，线程 " + p.Threads.Count
                        + "，句柄 " + p.HandleCount + Environment.NewLine;
                }
            }
            catch { }
            string hist = "";
            try
            {
                if (store != null)
                    hist = "历史: " + store.Count + " 条，正文 " + Store.HumanBytes(store.ContentBytes)
                        + "，索引 " + Store.HumanBytes(store.IndexBytes) + Environment.NewLine;
            }
            catch { }
            return Name + " " + Number + " (" + Quad + ")  " + Description + Environment.NewLine
                + "数据目录: " + Cfg.DataDir + Environment.NewLine
                + "程序目录: " + Cfg.ExeDir + Environment.NewLine
                + "配置: max_items=" + Cfg.MaxItems + " debounce_ms=" + Cfg.DebounceMs
                    + " min_len=" + Cfg.MinLen + " store_mb=" + Cfg.StoreMB
                    + " poll_ms=" + Cfg.PollMs + " hotkey=" + Cfg.Hotkey
                    + " capture_image=" + (Cfg.CaptureImage ? 1 : 0) + Environment.NewLine
                + hist + run
                + "系统: " + Environment.OSVersion.Version + " "
                    + (Environment.Is64BitOperatingSystem ? "x64" : "x86") + "，CLR " + Environment.Version;
        }
    }
}
