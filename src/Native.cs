using System;
using System.Runtime.InteropServices;
using System.Text;

namespace MiniClip
{
    // 所有 Win32 交互集中在这里：事件驱动监听、剪贴板读写、来源归属、进程指标
    internal static class N
    {
        public const int WM_HOTKEY = 0x0312;
        public const int WM_CLIPBOARDUPDATE = 0x031D;
        public const int WM_DISPLAYCHANGE = 0x07E0;

        public const uint CF_UNICODETEXT = 13;
        public const uint CF_BITMAP = 2;
        public const uint CF_DIB = 8;
        public const uint CF_DIBV5 = 17;
        public const uint CF_RIFF = 11;
        public const uint CF_WAVE = 12;
        public const uint CF_HDROP = 15;
        public const uint GMEM_MOVEABLE = 0x0002;
        public const uint GMEM_ZEROINIT = 0x0040;

        public const int SW_HIDE = 0;
        public const int SW_SHOW = 5;
        public const int SW_RESTORE = 9;

        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;
        public const uint MOD_NOREPEAT = 0x4000;

        public const uint GA_ROOTOWNER = 3;

        public const int ATTACH_PARENT_PROCESS = -1;
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern uint GetClipboardSequenceNumber();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool OpenClipboard(IntPtr newOwner);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool CloseClipboard();

        [DllImport("user32.dll")]
        public static extern bool EmptyClipboard();

        [DllImport("user32.dll")]
        public static extern bool IsClipboardFormatAvailable(uint format);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetClipboardData(uint format);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetClipboardData(uint format, IntPtr mem);

        [DllImport("user32.dll")]
        public static extern uint EnumClipboardFormats(uint format);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int GetClipboardFormatName(uint formatId, StringBuilder buf, int maxCount);

        [DllImport("user32.dll")]
        public static extern IntPtr GetClipboardOwner();

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hwnd, StringBuilder buf, int maxCount);

        [DllImport("user32.dll")]
        public static extern int GetWindowTextLength(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern bool IsHungAppWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hwnd, int id);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hwnd, int cmdShow);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int index);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern UIntPtr GlobalSize(IntPtr mem);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GlobalLock(IntPtr mem);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GlobalUnlock(IntPtr mem);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GlobalFree(IntPtr mem);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AllocConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool FreeConsole();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int GetModuleFileName(IntPtr hModule, StringBuilder buf, int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool QueryFullProcessImageName(IntPtr hProcess, uint flags, StringBuilder buf, ref int size);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DuplicateHandle(IntPtr srcProc, IntPtr src, IntPtr dstProc, out IntPtr dst,
            uint access, bool inherit, uint options);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool TerminateThread(IntPtr h, uint code);

        [DllImport("kernel32.dll")]
        public static extern bool SetProcessWorkingSetSize(IntPtr p, IntPtr min, IntPtr max);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForSystem();

        private static float _scale;

        // 系统 DPI 缩放系数（100% => 1.0，150% => 1.5）。布局全部按逻辑像素书写，
        // 再统一乘这个系数：比让 WinForms 自动缩放更可预测，也不会出现列宽超出客户区。
        public static float Scale
        {
            get
            {
                if (_scale <= 0f)
                {
                    float f = 1f;
                    try
                    {
                        uint d = GetDpiForSystem();
                        if (d >= 96) f = d / 96f;
                    }
                    catch { }
                    if (f < 1f) f = 1f;
                    if (f > 3f) f = 3f;
                    _scale = f;
                }
                return _scale;
            }
        }

        public static int Px(int logical) { return (int)(logical * Scale + 0.5f); }

        // ---- 便捷封装 ----

        public static string WindowText(IntPtr hwnd, int max)
        {
            if (hwnd == IntPtr.Zero) return "";
            int len = GetWindowTextLength(hwnd);
            if (len <= 0) return "";
            if (len > max) len = max;
            StringBuilder sb = new StringBuilder(len + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        // 取真正拥有标题的顶层窗口（UWP/浏览器子窗口需要）
        public static IntPtr RootOwner(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return IntPtr.Zero;
            IntPtr r = GetAncestor(hwnd, GA_ROOTOWNER);
            if (r == IntPtr.Zero) return hwnd;
            return r;
        }

        public static int PidOfWindow(IntPtr hwnd)
        {
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            return (int)pid;
        }

        public static string ProcessPath(int pid)
        {
            if (pid <= 0) return "";
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return "";
            try
            {
                StringBuilder sb = new StringBuilder(1024);
                int size = sb.Capacity;
                if (QueryFullProcessImageName(h, 0, sb, ref size)) return sb.ToString(0, size);
                return "";
            }
            finally { CloseHandle(h); }
        }

        public static bool OpenClipboardRetry(int tries, int sleepMs)
        {
            for (int i = 0; i < tries; i++)
            {
                if (OpenClipboard(IntPtr.Zero)) return true;
                System.Threading.Thread.Sleep(sleepMs);
            }
            return false;
        }

        // 独占打开（写入时把 owner 设成自己的窗口，便于识别"自己粘自己"）
        public static bool OpenClipboardOwned(IntPtr owner, int tries, int sleepMs)
        {
            for (int i = 0; i < tries; i++)
            {
                if (OpenClipboard(owner)) return true;
                System.Threading.Thread.Sleep(sleepMs);
            }
            return false;
        }

        public static string ExeName(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            int i = path.LastIndexOf('\\');
            return i < 0 ? path : path.Substring(i + 1);
        }
    }
}
