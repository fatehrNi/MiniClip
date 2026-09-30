using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace MiniClip
{
    // 一次复制的现场快照（在事件到达的瞬间抓取，之后 UI 焦点可能已经变了）
    internal sealed class Shot
    {
        public long Ts;
        public uint Seq;
        public string App = "";
        public string Title = "";
        public string Path = "";
    }

    internal sealed class Monitor : IDisposable
    {
        private readonly Store _store;
        private readonly HostWnd _wnd;
        private readonly System.Windows.Forms.Timer _debounce;
        private Thread _worker;
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly object _slot = new object();
        private Shot _pending;
        private readonly ManualResetEvent _stopEvent = new ManualResetEvent(false);
        private uint _lastSeq;
        private volatile bool _enabled = true;

        public event Action Changed;          // UI 线程：历史有更新
        public event Action HotkeyPressed;    // 全局热键
        public Action ShowRequest;            // 外部指令 / 热键要求显示窗口
        public Action HideRequest;            // 外部指令要求收起
        public Action<long> CopyRequest;      // 外部指令要求把某条送回剪贴板
        public Action QuitRequest;
        public int Captured;
        public int Duplicated;

        public Monitor(Store store)
        {
            _store = store;
            _wnd = new HostWnd(this);
            _debounce = new System.Windows.Forms.Timer();
            _debounce.Interval = Math.Max(1, Cfg.DebounceMs);
            _debounce.Tick += OnDebounce;

            if (Cfg.PollMs > 0)
            {
                // 轮询对照组：仅用于基准测试，证明事件驱动更省
                _poll = new System.Windows.Forms.Timer();
                _poll.Interval = Cfg.PollMs;
                _poll.Tick += OnPoll;
                _poll.Start();
                Tracer.Log("mode=poll " + Cfg.PollMs + "ms");
            }
            else
            {
                bool ok = N.AddClipboardFormatListener(_wnd.Handle);
                Tracer.Log("mode=event listener ok=" + ok + " hwnd=" + _wnd.Handle.ToInt64().ToString("x")
                    + " err=" + Marshal.GetLastWin32Error());
            }

            StartWorker();
            _watchdog = new System.Windows.Forms.Timer();
            _watchdog.Interval = 1000;
            _watchdog.Tick += OnWatchdog;
            _watchdog.Start();
        }

        private System.Windows.Forms.Timer _watchdog;
        private long _readSince;
        private IntPtr _readerHandle = IntPtr.Zero;
        private int _stuckStrikes;

        private void NativeSelf(out IntPtr real)
        {
            real = IntPtr.Zero;
            try
            {
                N.DuplicateHandle(N.GetCurrentProcess(), N.GetCurrentThread(), N.GetCurrentProcess(),
                    out real, 0x0001 | 0x0040, false, 0);   // THREAD_TERMINATE | THREAD_QUERY_INFORMATION
            }
            catch { }
            _readerHandle = real;
        }

        private void OnWatchdog(object sender, EventArgs e)
        {
            long since = _readSince;
            if (since == 0)
            {
                if (_stuckStrikes > 0 && ClipboardResponsive()) { _stuckStrikes = 0; Tracer.Log("剪贴板已恢复"); }
                return;
            }
            long el = Time.Now() - since;
            if (el < 4000) return;
            if (_stuckStrikes == 0)
            {
                _stuckStrikes = 1;
                Tracer.Log("CRITICAL 读取卡住 " + el + "ms，终止读线程以释放剪贴板");
                try
                {
                    if (_readerHandle != IntPtr.Zero) N.TerminateThread(_readerHandle, 2);
                }
                catch (Exception ex) { Tracer.Log("terminate fail " + ex.Message); }
                _readSince = 0;
                StartWorker();
                return;
            }
            if (ClipboardResponsive()) { _readSince = 0; return; }
            if (_stuckStrikes >= 2)
            {
                // 还锁不住就自我重启：剪贴板被冻住比少记一条严重得多
                Tracer.Log("CRITICAL 剪贴板仍被占用，重启进程");
                try
                {
                    Process.Start(Application.ExecutablePath);
                    Environment.Exit(4);
                }
                catch { }
            }
            _stuckStrikes = 2;
        }

        private bool ClipboardResponsive()
        {
            if (N.OpenClipboard(_wnd.Handle)) { N.CloseClipboard(); return true; }
            return false;
        }

        private System.Windows.Forms.Timer _poll;
        private uint _pollSeq;
        private int _pollTicks;

        private void OnPoll(object s, EventArgs e)
        {
            // 轮询对照组：每次 tick 都是一次系统唤醒，用日志计数，与事件驱动做对比
            _pollTicks++;
            if ((_pollTicks % 50) == 0) Tracer.Log("poll ticks=" + _pollTicks);
            uint sq = N.GetClipboardSequenceNumber();
            if (sq != _pollSeq) { _pollSeq = sq; OnClipboardUpdate(); }
        }

        public bool Enabled { get { return _enabled; } set { _enabled = value; } }

        public bool RegisterHotkey(int id, uint mods, uint vk)
        {
            return N.RegisterHotKey(_wnd.Handle, id, mods | N.MOD_NOREPEAT, vk);
        }

        public void UnregisterHotkey(int id) { N.UnregisterHotKey(_wnd.Handle, id); }

        public IntPtr Handle { get { return _wnd.Handle; } }

        internal void OnClipboardUpdate()
        {
            if (!_enabled) { Tracer.Log("skip paused"); return; }
            uint seq = N.GetClipboardSequenceNumber();
            if (seq == _lastSeq) return;
            _lastSeq = seq;
            // 自己写回剪贴板不算一次新复制
            if (N.GetClipboardOwner() == _wnd.Handle) { Tracer.Log("skip self seq=" + seq); return; }

            Shot shot = new Shot();
            shot.Ts = Time.Now();
            shot.Seq = seq;
            GrabSource(shot);
            Tracer.Log("update seq=" + seq + " app=" + shot.App + " title=" + shot.Title);

            lock (_slot) _pending = shot;
            _debounce.Stop();
            _debounce.Start();          // 连续复制时只在停手后读一次
        }

        // 复制瞬间的"出自何处"：前台窗口 -> 根属主窗口 -> 进程
        private static void GrabSource(Shot s)
        {
            IntPtr fg = N.GetForegroundWindow();
            if (fg == IntPtr.Zero) { s.App = ""; return; }
            IntPtr root = N.RootOwner(fg);
            IntPtr target = root != IntPtr.Zero && N.GetWindowTextLength(root) > 0 ? root : fg;
            s.Title = N.WindowText(target, 200);
            int pid = N.PidOfWindow(target);
            if (pid <= 0) { pid = N.PidOfWindow(fg); s.Title = N.WindowText(fg, 200); }
            string path = N.ProcessPath(pid);
            s.Path = path;
            if (path.Length > 0) s.App = N.ExeName(path);
            else s.App = "pid:" + pid;
            if (s.Title.Length == 0) s.Title = s.App;
        }

        private void OnDebounce(object sender, EventArgs e)
        {
            // 只发信号，不取任务：任务由工作线程唯一领取，避免被消费两次
            _debounce.Stop();
            _wake.Set();
        }

        // 读线程可能卡在"向未响应的来源程序要延迟渲染数据"上，而它此刻正占着剪贴板 ——
        // 那会把全系统剪贴板冻住。看门狗负责：记录 → 终止卡死线程 → 验证解锁 → 必要时自我重启。
        private void StartWorker()
        {
            _worker = new Thread(WorkerLoop);
            _worker.IsBackground = true;
            _worker.Priority = ThreadPriority.BelowNormal;
            _worker.Name = "miniclip-io";
            _worker.Start();
        }

        private void WorkerLoop()
        {
            IntPtr self;
            NativeSelf(out self);
            WaitHandle[] handles = new WaitHandle[] { _stopEvent, _wake };
            while (true)
            {
                // 无限等待：空闲时这个线程一次都不会被唤醒
                int idx = WaitHandle.WaitAny(handles);
                if (idx == 0) break;
                Shot s;
                lock (_slot) { s = _pending; _pending = null; }
                if (s == null) continue;
                try { Capture(s); }
                catch (Exception ex) { Tracer.Log("worker " + ex.GetType().Name + " " + ex.Message); }
            }
        }

        private void Capture(Shot s)
        {
            long t0 = Time.Now();
            _readSince = t0;
            try { DoCapture(s, t0); }
            finally { Post(); _readSince = 0; }   // 无论成不成，等待方都要被唤醒
        }

        private void DoCapture(Shot s, long t0)
        {
            CapResult r = Cap.Read();
            long readMs = Time.Now() - t0;
            if (r == null) { Tracer.Log("seq=" + s.Seq + " 无可保存内容 " + readMs + "ms"); return; }

            ClipItem it = new ClipItem();
            it.Ts = s.Ts;
            it.App = s.App;
            it.Title = s.Title;
            it.Path = s.Path;
            it.Fmt = r.Fmts;
            it.Kind = r.Kind;

            string body;
            if (r.Kind == Kind.Image)
            {
                if (!Cfg.CaptureImage || r.Png == null) return;
                string rel = _store.SaveImage(r.Png);
                body = rel;
                it.Size = r.Png.Length;
                it.Preview = "图片 " + r.W + "×" + r.H;
                it.Hash = Util.Fnv1aBytes(r.Png);
            }
            else if (r.Kind == Kind.Audio)
            {
                // 直接复制的音频内容（CF_WAVE / CF_RIFF）按 WAV 存下来，回车可原样放回
                body = _store.SaveMedia(r.Wave, "wav");
                it.Size = r.Wave.Length;
                it.Preview = "音频 " + Store.HumanBytes(r.Wave.Length);
                it.Hash = Util.Fnv1aBytes(r.Wave);
            }
            else if (r.Kind == Kind.Files)
            {
                if (!Cfg.CaptureFiles) return;
                string[] ps = r.Text.Split('\n');
                Kind mk;
                string snap = SnapshotMedia(ps, out mk);
                if (snap != null)
                {
                    it.Kind = mk;
                    body = snap;
                    it.Size = new FileInfo(Path.Combine(_store.Root, snap)).Length;
                    it.Preview = Path.GetFileName(ps[0]) + "  " + Store.HumanBytes(it.Size);
                    it.Hash = Util.Fnv1a(snap);
                }
                else
                {
                    body = r.Text;
                    it.Chars = ps.Length;
                    it.Size = r.Size;
                    it.Preview = Util.Flat(ps[0], 60) + (ps.Length > 1 ? " 等 " + ps.Length + " 项" : "");
                    it.Hash = Util.Fnv1a(body);
                }
            }
            else
            {
                string t = r.Text;
                if (Util.TrimmedLength(t) < Cfg.MinLen) return;
                if (t.Length > Cfg.MaxChars) { t = t.Substring(0, Cfg.MaxChars); it.Trim = true; }
                body = t;
                it.Chars = t.Length;
                it.Size = r.Size;
                int a = 0, b = t.Length;
                while (a < b && char.IsWhiteSpace(t[a])) a++;
                while (b > a && char.IsWhiteSpace(t[b - 1])) b--;
                it.Hash = Util.Fnv1a(t, a, b - a);
                it.Preview = Util.Flat(t.Substring(a, b - a), 80);
            }

            // 去重：与最近内容相同则只刷新时间与次数
            ClipItem dup = _store.FindRecentByHash(it.Hash, it.Kind);
            if (dup != null)
            {
                dup.Ts = it.Ts;
                dup.App = it.App;
                dup.Title = it.Title;
                dup.Path = it.Path;
                _store.Touch(dup);
                Duplicated++;
                Tracer.Log("dup id=" + dup.Id + " hits=" + dup.Hits + " from " + it.App);
                Post();
                return;
            }

            _store.Add(it, body);
            Captured++;
            if ((Captured & 7) == 0) Probe.Counters("cap" + Captured);
            Tracer.Log("add id=" + it.Id + " kind=" + (int)it.Kind + " size=" + it.Size + " read=" + readMs + "ms from " + it.App + " | " + it.Title);

            if (_store.NeedsCompact())
            {
                _store.Compact();
                Tracer.Log("compacted content=" + _store.ContentBytes);
            }
            Post();
        }

        private static readonly string[] ImgExt = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };
        private static readonly string[] AudExt = { ".wav", ".mp3", ".flac", ".ogg", ".m4a", ".aac", ".amr", ".wma", ".opus" };
        private const long MediaCap = 32L * 1048576L;

        // 复制的是单个图片/音频文件时，把文件本体也存一份进 media/：
        // 聊天软件的图片与语音往往落在临时目录，源文件被清理后原记录就成了打不开的死路径
        private string SnapshotMedia(string[] ps, out Kind kind)
        {
            kind = Kind.Files;
            if (ps.Length != 1 || !Cfg.CaptureImage) return null;
            string ext = Path.GetExtension(ps[0]).ToLowerInvariant();
            bool img = Array.IndexOf(ImgExt, ext) >= 0;
            if (!img && Array.IndexOf(AudExt, ext) < 0) return null;
            try
            {
                FileInfo fi = new FileInfo(ps[0]);
                if (!fi.Exists || fi.Length <= 0 || fi.Length > MediaCap) return null;
                byte[] data = File.ReadAllBytes(ps[0]);
                kind = img ? Kind.Image : Kind.Audio;
                return _store.SaveMedia(data, ext.Substring(1));
            }
            catch { return null; }
        }

        private void Post()
        {
            Action h = Changed;
            if (h == null) return;
            try { if (_wnd.IsHandleCreated) _wnd.BeginInvoke(h); }
            catch (Exception ex) { Tracer.Log("post " + ex.Message); }
        }

        // 我们自己写回剪贴板之后，把这次的序列号记下来，事件回来时直接跳过
        public void AfterSelfWrite()
        {
            _lastSeq = N.GetClipboardSequenceNumber();
        }

        public IntPtr SelfHwnd { get { return _wnd.Handle; } }

        public void Flush()
        {
            // 正文是即时落盘的，这里仅补一次可能的压缩
            if (_store.NeedsCompact()) _store.Compact();
        }

        public void Dispose()
        {
            if (_poll != null) { _poll.Stop(); _poll.Dispose(); _poll = null; }
            _debounce.Stop();
            _debounce.Dispose();
            N.RemoveClipboardFormatListener(_wnd.Handle);
            _stopEvent.Set();
            try { _worker.Join(800); } catch { }
        }

        // 隐藏窗口：承载剪贴板监听、全局热键与进程间指令。
        // 用真正的顶层窗口（不显示），这样 FindWindow 能找到它，别的进程才能下指令。
        private sealed class HostWnd : Form
        {
            private readonly Monitor _m;

            public HostWnd(Monitor m)
            {
                _m = m;
                Text = Program.HostTitle;
                FormBorderStyle = FormBorderStyle.FixedToolWindow;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                Location = new System.Drawing.Point(-32000, -32000);
                Size = new System.Drawing.Size(0, 0);
                IntPtr h = Handle;     // 只建句柄，从不 Show
            }

            protected override bool ShowWithoutActivation { get { return true; } }

            protected override void WndProc(ref Message msg)
            {
                if (Probe.Enabled) Tracer.Log("host msg " + msg.Msg.ToString("x"));
                if (msg.Msg == N.WM_CLIPBOARDUPDATE) { _m.OnClipboardUpdate(); return; }
                if (msg.Msg == N.WM_HOTKEY)
                {
                    Action h = _m.HotkeyPressed;
                    if (h != null) h();
                    return;
                }
                if (msg.Msg == Program.IpcMsg)
                {
                    int verb = msg.WParam.ToInt32();
                    if (verb == Program.VerbQuit) { Action q = _m.QuitRequest; if (q != null) q(); }
                    else if (verb == Program.VerbHide) { Action h = _m.HideRequest; if (h != null) h(); }
                    else if (verb == Program.VerbCopy)
                    {
                        Action<long> c = _m.CopyRequest;
                        if (c != null) c(msg.LParam.ToInt64());
                    }
                    else { Action s = _m.ShowRequest; if (s != null) s(); }
                    return;
                }
                base.WndProc(ref msg);
            }
        }
    }
}
