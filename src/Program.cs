using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace MiniClip
{
    internal static class Program
    {
        public const string HostTitle = "MiniClip::Host";
        public const int VerbShow = 1;
        public const int VerbHide = 2;
        public const int VerbQuit = 3;
        public const int VerbCopy = 4;
        public static uint IpcMsg;

        private const int HotkeyId = 0x5A01;

        [STAThread]
        private static int Main(string[] argv)
        {
            InstallCrashHandlers();
            IpcMsg = N.RegisterWindowMessage("MiniClip.Ipc.v1");
            string cmd = argv.Length > 0 ? argv[0].ToLowerInvariant() : "";

            switch (cmd)
            {
                case "--help":
                case "-h":
                case "/?":
                    Out(); return 0;
                case "--version":
                    AttachOut();
                    Console.WriteLine(Ver.Name + " " + Ver.Number + " (" + Ver.Quad + ")");
                    Console.WriteLine(Ver.Description);
                    Console.WriteLine(Ver.License + "  " + Ver.Copyright);
                    return 0;
                case "--dump": return Dump(argv);
                case "--get": return Get(argv);
                case "--stat": return Stat();
                case "--show": return Remote(VerbShow);
                case "--hide": return Remote(VerbHide);
                case "--quit": return Remote(VerbQuit);
                case "--copy":
                    {
                        long cid;
                        if (argv.Length < 2 || !long.TryParse(argv[1], out cid))
                        {
                            Console.Error.WriteLine("用法: --copy <id>");
                            return 1;
                        }
                        return Remote(VerbCopy, cid);
                    }
                case "--autostart": return Autostart(argv);
                case "--bench-capture": return BenchCapture(argv);
                case "--bench-store": return BenchStore(argv);
                case "--bench-load": return BenchLoad(argv);
            }
            return Run(argv);
        }

        private static void InstallCrashHandlers()
        {
            // 常驻程序不能被一个 UI 异常直接打死；异常必须落到磁盘上，否则无法追溯
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { Crash("ui", e.Exception); };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { Crash("domain", e.ExceptionObject as Exception); };
        }

        private static void Crash(string where, Exception ex)
        {
            try
            {
                Directory.CreateDirectory(Cfg.DataDir);
                string text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + where + "] "
                    + (ex == null ? "(null)" : ex.ToString()) + Environment.NewLine;
                File.AppendAllText(Path.Combine(Cfg.DataDir, "crash.log"), text, new UTF8Encoding(false));
                Tracer.Log("CRASH " + where + " " + (ex == null ? "" : ex.Message));
            }
            catch { }
        }

        private static void AttachOut()
        {
            if (!N.AttachConsole(N.ATTACH_PARENT_PROCESS)) N.AllocConsole();
            try { Console.OutputEncoding = new UTF8Encoding(); } catch { }
        }

        private static int Run(string[] argv)
        {
            bool owned;
            using (Mutex m = new Mutex(true, "Local\\MiniClip.Single", out owned))
            {
                if (!owned)
                {
                    // 已经在一个托盘实例里了，本次调用等于"唤出面板"
                    Remote(VerbShow);
                    return 0;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                Cfg.Load();
                if (Has(argv, "--trace")) Cfg.Trace = true;
                Tracer.Open();
                if (Cfg.FirstRun) Cfg.Save();   // 首启落一份默认配置，用户能有地方改
                Stopwatch sw = Stopwatch.StartNew();
                Directory.CreateDirectory(Cfg.DataDir);

                Store store = new Store(Cfg.DataDir);
                int n = store.Load();
                long loadMs = sw.ElapsedMilliseconds;
                Tracer.Log("loaded items=" + n + " ms=" + loadMs);

                Monitor mon = new Monitor(store);
                MainForm ui = new MainForm(store, mon);

                if (Cfg.HotkeyEnabled && Cfg.ParseHotkey())
                {
                    if (mon.RegisterHotkey(HotkeyId, Cfg.Modifiers, Cfg.Vk))
                        Tracer.Log("hotkey registered " + Cfg.Hotkey);
                    else
                        Tracer.Log("hotkey register FAILED: " + Cfg.Hotkey);
                }
                else Tracer.Log("hotkey disabled");
                mon.HotkeyPressed += delegate { ui.ShowFromTray(); };
                mon.ShowRequest = delegate { ui.ShowWindow(); };
                mon.HideRequest = delegate { ui.HidePanel(); };
                mon.CopyRequest = delegate(long id) { ui.CopyById(id); };
                mon.QuitRequest = delegate { ui.QuitNow(); };

                WritePid();
                Tracer.Log("ready ms=" + sw.ElapsedMilliseconds + " store=" + n + " hwnd=" + mon.Handle.ToInt64().ToString("x"));

                Application.ApplicationExit += delegate
                {
                    try
                    {
                        mon.Flush();
                        mon.Dispose();
                        store.Shutdown();
                        Tracer.Log("exit");
                        Tracer.Close();
                    }
                    catch { }
                    try { File.Delete(Path.Combine(Cfg.DataDir, "pid.txt")); } catch { }
                };

                // 一次性：启动抖动结束后把没在用的页交还系统，之后只有打开面板才会再占
                System.Windows.Forms.Timer trimmer = new System.Windows.Forms.Timer();
                trimmer.Interval = 2500;
                trimmer.Tick += delegate
                {
                    trimmer.Stop();
                    trimmer.Dispose();
                    Cap.TrimWorkingSet();
                    Tracer.Log("trimmed working set");
                };
                trimmer.Start();

                // 双击启动就直接显示主界面；只有开机自启（--silent）才安静留在托盘
                if (!Has(argv, "--silent") && !Has(argv, "/s"))
                {
                    System.Windows.Forms.Timer opener = new System.Windows.Forms.Timer();
                    opener.Interval = 150;
                    opener.Tick += delegate
                    {
                        opener.Stop();
                        opener.Dispose();
                        ui.ShowWindow();
                    };
                    opener.Start();
                }

                Application.Run();
                return 0;
            }
        }

        private static bool Has(string[] argv, string flag)
        {
            for (int i = 0; i < argv.Length; i++) if (argv[i].ToLowerInvariant() == flag) return true;
            return false;
        }

        private static string Opt(string[] argv, string name, string def)
        {
            for (int i = 0; i < argv.Length - 1; i++)
                if (argv[i].ToLowerInvariant() == name) return argv[i + 1];
            return def;
        }

        private static void WritePid()
        {
            try { File.WriteAllText(Path.Combine(Cfg.DataDir, "pid.txt"), Process.GetCurrentProcess().Id.ToString()); }
            catch { }
        }

        // ---- 远程指令 ----

        private static int Remote(int verb) { return Remote(verb, 0); }

        private static int Remote(int verb, long arg)
        {
            IntPtr h = N.FindWindow(null, HostTitle);
            if (h == IntPtr.Zero)
            {
                Console.Error.WriteLine("MiniClip 未在运行");
                return 2;
            }
            N.PostMessage(h, IpcMsg, new IntPtr(verb), new IntPtr(arg));
            return 0;
        }

        // ---- 命令行读历史 ----

        private static int Dump(string[] argv)
        {
            int limit = 20;
            for (int i = 1; i < argv.Length; i++)
            {
                int v;
                if (int.TryParse(argv[i], out v)) { limit = v; break; }
            }
            Store s = new Store(Cfg.DataDir);
            s.Load();
            List<ClipItem> all = s.Snapshot();
            StringBuilder sb = new StringBuilder(4096);
            int shown = 0;
            for (int i = 0; i < all.Count && shown < limit; i++, shown++)
            {
                ClipItem it = all[i];
                sb.Append("id=").Append(it.Id)
                  .Append("\tts=").Append(it.Ts).Append("\ttime=").Append(Time.At(it.Ts).ToString("yyyy-MM-dd HH:mm:ss"))
                  .Append("\tkind=").Append((int)it.Kind).Append("\tchars=").Append(it.Chars)
                  .Append("\tsize=").Append(it.Size).Append("\thits=").Append(it.Hits).Append("\tpin=").Append(it.Pin ? 1 : 0)
                  .Append("\thash=").Append(it.Hash.ToString("x16"))
                  .Append("\tapp=").Append(Util.Ascii(it.App))
                  .Append("\ttitle=").Append(Util.Ascii(it.Title))
                  .Append("\tfmt=").Append(Util.Ascii(it.Fmt))
                  .Append("\tpreview=").Append(Util.Ascii(it.Preview))
                  .Append('\n');
            }
            Out(sb.ToString());
            Console.Error.WriteLine("total=" + all.Count + "  contentBytes=" + s.ContentBytes + "  indexBytes=" + s.IndexBytes);
            return 0;
        }

        private static int Get(string[] argv)
        {
            long id;
            if (argv.Length < 2 || !long.TryParse(argv[1], out id)) { Console.Error.WriteLine("用法: --get <id> [输出文件]"); return 1; }
            Store s = new Store(Cfg.DataDir);
            s.Load();
            string body = s.Content(id);
            if (argv.Length >= 3)
            {
                File.WriteAllText(argv[2], body, new UTF8Encoding(false));
                Console.Error.WriteLine("wrote " + body.Length + " chars to " + argv[2]);
                return 0;
            }
            Out(body);
            return 0;
        }

        private static void Out(string s)
        {
            AttachOut();
            Console.Out.Write(s);
            Console.Out.Flush();
        }

        private static void Out()
        {
            AttachOut();
            Console.Out.WriteLine(
@"MiniClip —— 轻量剪贴板历史（Windows / .NET Framework 4.x，单文件，无安装）

运行：
  MiniClip.exe                 启动常驻托盘（事件驱动监听，零轮询）
  全局热键 " + Cfg.Hotkey + @"        唤出/收起历史面板
  面板内：Enter 复制选中项   Del 删除   Ctrl+P 置顶   Alt+1..9 快速取用   Esc 收起
  Ctrl+F 搜索   搜索框支持按来源程序/窗口标题/内容过滤

命令行（可脚本验证）：
  MiniClip.exe --dump [n]          打印最近 n 条元数据（ASCII 转义，便于 diff）
  MiniClip.exe --get <id> [file]   输出某条正文
  MiniClip.exe --stat              打印运行中实例的 CPU/内存/句柄占用
  MiniClip.exe --show | --hide | --quit     唤起面板 / 收起 / 退出实例
  MiniClip.exe --copy <id>       把某条历史放回剪贴板（脚本可用）
  MiniClip.exe --autostart on|off  开机自启（HKCU Run）
  MiniClip.exe --bench-capture <n> 端到端延迟基准：写入 n 次剪贴板并统计落库耗时
  MiniClip.exe --bench-store <n>   批量写入 n 条，统计吞吐
  MiniClip.exe --bench-load        冷启动加载 n 条耗时
  数据目录：默认在 <exe 所在目录>\data（便携，不写 C 盘）；环境变量 MINICLIP_DATA=<dir> 可覆盖
  参数 --trace                     写 trace.log，观察每个环节耗时
");
        }

        // ---- 自启 ----

        private static int Autostart(string[] argv)
        {
            bool on = argv.Length < 2 || argv[1].ToLowerInvariant() != "off";
            string exe = Application.ExecutablePath;
            Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", true);
            if (k == null) { Console.Error.WriteLine("无法打开启动项注册表"); return 1; }
            try
            {
                // 开机自启带 --silent：登录时安静进托盘，不弹主界面
                if (on) k.SetValue("MiniClip", "\"" + exe + "\" --silent");
                else k.DeleteValue("MiniClip", false);
            }
            finally { k.Close(); }
            Console.Error.WriteLine("autostart=" + (on ? "on " + exe : "off"));
            return 0;
        }

        // ---- 指标 ----

        private static int Stat()
        {
            int pid = 0;
            string pf = Path.Combine(Cfg.DataDir, "pid.txt");
            if (File.Exists(pf)) int.TryParse(File.ReadAllText(pf), out pid);
            Process p = null;
            if (pid > 0) { try { p = Process.GetProcessById(pid); } catch { } }
            if (p == null)
            {
                Process[] ps = Process.GetProcessesByName("miniclip");
                for (int i = 0; i < ps.Length; i++) if (ps[i].Id != Process.GetCurrentProcess().Id) { p = ps[i]; break; }
            }
            if (p == null) { Console.Error.WriteLine("MiniClip 未在运行"); return 2; }

            StringBuilder sb = new StringBuilder(256);
            sb.Append("pid=").Append(p.Id).Append('\n')
              .Append("name=").Append(p.ProcessName).Append('\n')
              .Append("working_set_mb=").Append((p.WorkingSet64 / 1048576.0).ToString("0.00")).Append('\n')
              .Append("private_mb=").Append((p.PrivateMemorySize64 / 1048576.0).ToString("0.00")).Append('\n')
              .Append("cpu_ms=").Append((long)p.TotalProcessorTime.TotalMilliseconds).Append('\n')
              .Append("threads=").Append(p.Threads.Count).Append('\n')
              .Append("handles=").Append(p.HandleCount).Append('\n');
            Store s = new Store(Cfg.DataDir);
            s.Load();
            sb.Append("items=").Append(s.Count).Append('\n')
              .Append("index_bytes=").Append(s.IndexBytes).Append('\n')
              .Append("content_bytes=").Append(s.ContentBytes).Append('\n');
            s.Shutdown();
            Out(sb.ToString());
            return 0;
        }

        // ---- 基准 ----

        private static int ArgI(string[] argv, int idx, int def)
        {
            int v;
            if (argv.Length > idx && int.TryParse(argv[idx], out v)) return v;
            return def;
        }

        // 端到端：从写剪贴板到条目出现在库里，包含防抖 + 事件派发 + 读取 + 落盘
        private sealed class Bench
        {
            public List<long> Lat = new List<long>();
            public long T0;
            public int Sent;
            public bool Busy;
            public int Target;
            public ApplicationContext Ctx;
        }

        private static int BenchCapture(string[] argv)
        {
            int n = ArgI(argv, 1, 20);
            Cfg.Load();
            Store store = new Store(Path.Combine(Cfg.DataDir, "bench", Guid.NewGuid().ToString("N").Substring(0, 8)));
            Application.EnableVisualStyles();

            Monitor mon = new Monitor(store);
            Bench b = new Bench();
            b.Target = n;
            b.Ctx = new ApplicationContext();

            mon.Changed += delegate
            {
                if (!b.Busy) return;
                b.Busy = false;
                b.Lat.Add(Time.Now() - b.T0);
            };

            System.Windows.Forms.Timer driver = new System.Windows.Forms.Timer();
            driver.Interval = 50;
            int deadline = Environment.TickCount + 20000 + b.Target * 500;
            driver.Tick += delegate
            {
                if (Environment.TickCount > deadline)
                {
                    Tracer.Log("bench 超时：已发 " + b.Sent + " 收到 " + b.Lat.Count);
                    b.Ctx.ExitThread();
                    return;
                }
                if (b.Busy) return;
                if (b.Sent >= b.Target) { b.Ctx.ExitThread(); return; }
                b.Sent++;
                string payload = "BENCH-" + b.Sent + "-" + Guid.NewGuid().ToString("N").Substring(0, 10);
                b.T0 = Time.Now();
                b.Busy = true;
                Cap.WriteText(IntPtr.Zero, payload);   // owner=NULL：不走"自己写回"抑制
            };
            driver.Start();
            Application.Run(b.Ctx);
            driver.Stop();
            mon.Dispose();

            b.Lat.Sort();
            StringBuilder sb = new StringBuilder(256);
            sb.Append("sent=").Append(b.Sent).Append('\n')
              .Append("samples=").Append(b.Lat.Count).Append('\n');
            if (b.Lat.Count > 0)
            {
                long sum = 0;
                for (int i = 0; i < b.Lat.Count; i++) sum += b.Lat[i];
                sb.Append("debounce_ms=").Append(Cfg.DebounceMs).Append('\n')
                  .Append("min_ms=").Append(b.Lat[0]).Append('\n')
                  .Append("mean_ms=").Append(sum / b.Lat.Count).Append('\n')
                  .Append("p50_ms=").Append(b.Lat[b.Lat.Count / 2]).Append('\n')
                  .Append("p95_ms=").Append(b.Lat[Math.Min(b.Lat.Count - 1, (int)(b.Lat.Count * 0.95))]).Append('\n')
                  .Append("max_ms=").Append(b.Lat[b.Lat.Count - 1]).Append('\n');
            }
            sb.Append("captured=").Append(mon.Captured).Append('\n')
              .Append("dup=").Append(mon.Duplicated).Append('\n')
              .Append("items_in_store=").Append(store.Count).Append('\n');
            store.Shutdown();
            Out(sb.ToString());
            return 0;
        }

        // 纯吞吐：绕开剪贴板，直接压 store
        private static int BenchStore(string[] argv)
        {
            int n = ArgI(argv, 1, 2000);
            int chars = ArgI(argv, 2, 200);
            Cfg.Load();
            Store store = new Store(Path.Combine(Cfg.DataDir, "bench", Guid.NewGuid().ToString("N").Substring(0, 8)));
            store.Load();
            Random rnd = new Random(12345);
            Stopwatch sw = Stopwatch.StartNew();
            for (int i = 0; i < n; i++)
            {
                StringBuilder body = new StringBuilder(chars);
                for (int j = 0; j < chars; j++) body.Append((char)('a' + rnd.Next(26)));
                string s = body.ToString();
                ClipItem it = new ClipItem();
                it.Ts = Time.Now();
                it.Kind = Kind.Text;
                it.App = "gen.exe";
                it.Title = "生成窗口 " + i;
                it.Chars = s.Length;
                it.Size = s.Length;
                it.Hash = Util.Fnv1a(s);
                it.Preview = Util.Flat(s, 80);
                it.Fmt = "CF_UNICODETEXT";
                store.Add(it, s);
            }
            long addMs = sw.ElapsedMilliseconds;
            long bytes = store.ContentBytes;
            int cnt = store.Count;
            store.Shutdown();
            Store again = new Store(store.Root);
            Stopwatch s2 = Stopwatch.StartNew();
            int reloaded = again.Load();
            long loadMs = s2.ElapsedMilliseconds;
            StringBuilder sb = new StringBuilder(256);
            sb.Append("items=").Append(cnt).Append('\n')
              .Append("add_ms=").Append(addMs).Append('\n')
              .Append("add_per_sec=").Append(cnt * 1000L / Math.Max(1L, addMs)).Append('\n')
              .Append("content_bytes=").Append(bytes).Append('\n')
              .Append("reload_items=").Append(reloaded).Append('\n')
              .Append("reload_ms=").Append(loadMs).Append('\n');
            again.Shutdown();
            Out(sb.ToString());
            return 0;
        }

        private static int BenchLoad(string[] argv)
        {
            Cfg.Load();
            Store store = new Store(Cfg.DataDir);
            Stopwatch sw = Stopwatch.StartNew();
            int n = store.Load();
            long ms = sw.ElapsedMilliseconds;
            long mem = GC.GetTotalMemory(false);
            StringBuilder sb = new StringBuilder(128);
            sb.Append("items=").Append(n).Append('\n')
              .Append("load_ms=").Append(ms).Append('\n')
              .Append("managed_heap_kb=").Append(mem / 1024).Append('\n')
              .Append("content_bytes=").Append(store.ContentBytes).Append('\n')
              .Append("index_bytes=").Append(store.IndexBytes).Append('\n');
            store.Shutdown();
            Out(sb.ToString());
            return 0;
        }
    }
}
