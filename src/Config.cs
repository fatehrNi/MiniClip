using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace MiniClip
{
    internal static class Time
    {
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static long Now()
        {
            return (long)(DateTime.UtcNow - Epoch).TotalMilliseconds;
        }

        // Unix 毫秒 -> 本地时间
        public static DateTime At(long ms)
        {
            return Epoch.AddMilliseconds(ms).ToLocalTime();
        }
    }

    internal static class Util
    {
        // 单行安全转义：正文里绝不允许出现裸的 \t \r \n
        public static string Esc(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder(s.Length + 8);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\') sb.Append("\\\\");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < ' ') { sb.Append("\\u"); sb.Append(((int)c).ToString("x4")); }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        public static string Unesc(string s)
        {
            if (s == null || s.Length == 0) return "";
            if (s.IndexOf('\\') < 0) return s;
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
                i++;
                char d = s[i];
                if (d == 'n') sb.Append('\n');
                else if (d == 'r') sb.Append('\r');
                else if (d == 't') sb.Append('\t');
                else if (d == '\\') sb.Append('\\');
                else if (d == '0') sb.Append('\0');
                else if (d == 'u' && i + 4 < s.Length)
                {
                    int v = int.Parse(s.Substring(i + 1, 4), NumberStyles.HexNumber);
                    sb.Append((char)v);
                    i += 4;
                }
                else sb.Append(d);
            }
            return sb.ToString();
        }

        public static string Ascii(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c >= ' ' && c < 0x7F) sb.Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\t') sb.Append("\\t");
                else { sb.Append("\\u"); sb.Append(((int)c).ToString("x4")); }
            }
            return sb.ToString();
        }

        public static ulong Fnv1a(string s)
        {
            return Fnv1a(s, 0, s == null ? 0 : s.Length);
        }

        public static ulong Fnv1a(string s, int start, int len)
        {
            ulong h = 14695981039346656037UL;
            int end = start + len;
            for (int i = start; i < end; i++)
            {
                h ^= (uint)s[i];
                h *= 1099511628211UL;
                h ^= (uint)(s[i] >> 16);
                h *= 1099511628211UL;
            }
            return h;
        }

        // 不分配新字符串地算出去空白后的长度
        public static int TrimmedLength(string s)
        {
            int a = 0, b = s.Length;
            while (a < b && char.IsWhiteSpace(s[a])) a++;
            while (b > a && char.IsWhiteSpace(s[b - 1])) b--;
            return b - a;
        }

        public static ulong Fnv1aBytes(byte[] b)
        {
            ulong h = 14695981039346656037UL;
            for (int i = 0; i < b.Length; i++)
            {
                h ^= b[i];
                h *= 1099511628211UL;
            }
            return h;
        }

        public static string Flat(string s, int max)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder(max + 2);
            int n = 0;
            for (int i = 0; i < s.Length && n < max; i++)
            {
                char c = s[i];
                if (c == '\r') continue;
                if (c == '\n' || c == '\t' || c < ' ')
                {
                    if (sb.Length > 0 && sb[sb.Length - 1] == ' ') continue;
                    sb.Append(' ');
                }
                else sb.Append(c);
                n++;
            }
            if (s.Length > max) sb.Append("…");
            return sb.ToString();
        }

        // 去掉首尾空白，返回长度是否有效
        public static string TrimAll(string s)
        {
            if (s == null) return "";
            return s.Trim();
        }
    }

    internal static class Cfg
    {
        public static int MaxItems = 2000;
        public static int DebounceMs = 90;
        public static int MinLen = 2;
        public static int MaxChars = 200000;
        public static int StoreMB = 24;
        public static int PollMs = 0;          // 0 = 纯事件驱动（默认，最省）；>0 轮询对照
        public static bool HotkeyEnabled = true;
        public static string Hotkey = "ctrl+alt+v";
        public static bool Autostart = true;
        public static bool CaptureImage = true;
        public static bool CaptureFiles = true;
        public static bool Preview = true;
        public static int WinW = 1080;
        public static int WinH = 640;
        public static int WinX = -32000;
        public static int WinY = -32000;
        public static bool Trace = false;
        public static bool FirstRun = false;

        private static string _exeDir;

        // 便携化：数据跟着程序走（exe 同级 data 目录），不落 C 盘
        public static string ExeDir
        {
            get
            {
                if (_exeDir == null)
                {
                    string dir = null;
                    try
                    {
                        StringBuilder sb = new StringBuilder(1024);
                        if (N.GetModuleFileName(IntPtr.Zero, sb, sb.Capacity) > 0)
                            dir = Path.GetDirectoryName(sb.ToString());
                    }
                    catch { }
                    if (string.IsNullOrEmpty(dir)) dir = AppDomain.CurrentDomain.BaseDirectory;
                    _exeDir = dir.TrimEnd(Path.DirectorySeparatorChar);
                }
                return _exeDir;
            }
        }

        public static string DataDir
        {
            get
            {
                string ov = Environment.GetEnvironmentVariable("MINICLIP_DATA");
                if (!string.IsNullOrEmpty(ov)) return ov;
                if (_dataDir == null)
                {
                    string cand = Path.Combine(ExeDir, "data");
                    try
                    {
                        Directory.CreateDirectory(cand);
                        string probe = Path.Combine(cand, ".writable");
                        File.WriteAllText(probe, "1");
                        File.Delete(probe);
                        _dataDir = cand;
                    }
                    catch
                    {
                        // 只读介质（光盘/受保护目录）才退回用户目录
                        _dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiniClip");
                    }
                }
                return _dataDir;
            }
        }

        private static string _dataDir;

        public static string ConfigPath { get { return Path.Combine(DataDir, "config.txt"); } }

        private static string Str(string[] kv, string key, string def)
        {
            for (int i = 0; i < kv.Length; i++)
            {
                int eq = kv[i].IndexOf('=');
                if (eq <= 0) continue;
                if (string.Equals(kv[i].Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
                    return kv[i].Substring(eq + 1).Trim();
            }
            return def;
        }

        private static int Int(string[] kv, string key, int def)
        {
            string v = Str(kv, key, null);
            if (v == null) return def;
            int r;
            if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out r)) return r;
            return def;
        }

        private static bool Bool(string[] kv, string key, bool def)
        {
            string v = Str(kv, key, null);
            if (v == null) return def;
            if (v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (v == "0" || string.Equals(v, "false", StringComparison.OrdinalIgnoreCase)) return false;
            return def;
        }

        public static void Load()
        {
            string[] kv = new string[0];
            FirstRun = !File.Exists(ConfigPath);
            try
            {
                if (File.Exists(ConfigPath))
                {
                    kv = File.ReadAllLines(ConfigPath, Encoding.UTF8);
                }
            }
            catch { }
            MaxItems = Math.Max(10, Int(kv, "max_items", MaxItems));
            DebounceMs = Math.Max(0, Int(kv, "debounce_ms", DebounceMs));
            MinLen = Math.Max(1, Int(kv, "min_len", MinLen));
            MaxChars = Math.Max(1000, Int(kv, "max_chars", MaxChars));
            StoreMB = Math.Max(1, Int(kv, "store_mb", StoreMB));
            PollMs = Math.Max(0, Int(kv, "poll_ms", PollMs));
            Hotkey = Str(kv, "hotkey", Hotkey);
            HotkeyEnabled = Bool(kv, "hotkey_enabled", HotkeyEnabled);
            Autostart = Bool(kv, "autostart", Autostart);
            CaptureImage = Bool(kv, "capture_image", CaptureImage);
            CaptureFiles = Bool(kv, "capture_files", CaptureFiles);
            Preview = Bool(kv, "preview", Preview);
            WinW = Math.Max(560, Int(kv, "win_w", WinW));
            WinX = Int(kv, "win_x", WinX);
            WinY = Int(kv, "win_y", WinY);
            WinH = Math.Max(320, Int(kv, "win_h", WinH));
            Trace = Bool(kv, "trace", Trace);
        }

        public static void Save()
        {
            StringBuilder sb = new StringBuilder(512);
            sb.AppendLine("# MiniClip 配置（key=value，改完重启生效）");
            sb.AppendLine("max_items=" + MaxItems);
            sb.AppendLine("debounce_ms=" + DebounceMs);
            sb.AppendLine("min_len=" + MinLen);
            sb.AppendLine("max_chars=" + MaxChars);
            sb.AppendLine("store_mb=" + StoreMB);
            sb.AppendLine("poll_ms=" + PollMs);
            sb.AppendLine("hotkey=" + Hotkey);
            sb.AppendLine("hotkey_enabled=" + (HotkeyEnabled ? 1 : 0));
            sb.AppendLine("autostart=" + (Autostart ? 1 : 0));
            sb.AppendLine("capture_image=" + (CaptureImage ? 1 : 0));
            sb.AppendLine("capture_files=" + (CaptureFiles ? 1 : 0));
            sb.AppendLine("preview=" + (Preview ? 1 : 0));
            sb.AppendLine("win_w=" + WinW);
            sb.AppendLine("win_h=" + WinH);
            sb.AppendLine("win_x=" + WinX);
            sb.AppendLine("win_y=" + WinY);
            sb.AppendLine("trace=" + (Trace ? 1 : 0));
            try
            {
                Directory.CreateDirectory(DataDir);
                File.WriteAllText(ConfigPath, sb.ToString(), new UTF8Encoding(true));
            }
            catch { }
        }

        public static uint Modifiers;
        public static uint Vk;

        // "ctrl+alt+v" -> MOD_CONTROL|MOD_ALT, 'V'
        public static bool ParseHotkey()
        {
            Modifiers = 0; Vk = 0;
            if (Hotkey == null) return false;
            string[] parts = Hotkey.ToLowerInvariant().Split(new char[] { '+', ',' });
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                if (p.Length == 0) continue;
                if (p == "ctrl" || p == "control") Modifiers |= N.MOD_CONTROL;
                else if (p == "alt" || p == "menu") Modifiers |= N.MOD_ALT;
                else if (p == "shift") Modifiers |= N.MOD_SHIFT;
                else if (p == "win" || p == "cmd") Modifiers |= N.MOD_WIN;
                else if (p.Length == 1)
                {
                    char c = p[0];
                    if (c >= 'a' && c <= 'z') Vk = (uint)(c - 'a' + 0x41);
                    else if (c >= '1' && c <= '9') Vk = (uint)(c - '1' + 0x31);
                    else if (c == '0') Vk = 0x30;
                }
                else if (p.Length > 1 && (p[0] == 'f' || p[0] == 'F'))
                {
                    int fnum;
                    if (int.TryParse(p.Substring(1), out fnum) && fnum >= 1 && fnum <= 24) Vk = (uint)(0x6F + fnum);
                }
            }
            return Vk != 0 && Modifiers != 0;
        }
    }
}
