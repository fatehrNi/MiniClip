using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// 测试用小工具：用原生 Win32 往剪贴板放"真实字节"或"延迟渲染"的内容。
// .NET 的 Clipboard 类没有 SetAudioStream，而且它会立即 flush，无法制造延迟渲染场景，
// 所以音频保存与"来源程序卡住时的自愈"必须靠这个工具才能验证。
//
//   clipput.exe wav   <文件> [保持秒]     放 CF_WAVE（音频）
//   clipput.exe png   <文件> [保持秒]     放 CF_DIB（位图）
//   clipput.exe text  <字符串> [保持秒]   放 CF_UNICODETEXT
//   clipput.exe hang  [保持秒]            放"延迟渲染"文本（没人应答，读取方会被挂住）
internal static class ClipPut
{
    private const uint CF_UNICODETEXT = 13, CF_DIB = 8, CF_WAVE = 12, GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr o);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint f, IntPtr mem);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr h);

    [STAThread]
    private static int Main(string[] argv)
    {
        if (argv.Length < 1) { Console.Error.WriteLine("用法: clipput.exe wav|png|text|hang ..."); return 1; }
        string mode = argv[0].ToLowerInvariant();
        int hold = 6;
        byte[] data = null;
        uint fmt = CF_UNICODETEXT;

        try
        {
            if (mode == "hang")
            {
                if (argv.Length > 1) hold = int.Parse(argv[1]);
                data = null;                       // NULL = 延迟渲染，等别人来问我要数据
                fmt = CF_UNICODETEXT;
            }
            else
            {
                string arg = argv.Length > 1 ? argv[1] : "";
                if (argv.Length > 2) hold = int.Parse(argv[2]);
                if (mode == "wav") { data = System.IO.File.ReadAllBytes(arg); fmt = CF_WAVE; }
                else if (mode == "png") { data = DibOf(arg); fmt = CF_DIB; }
                else if (mode == "text") { data = Encoding.Unicode.GetBytes(arg + "\0"); fmt = CF_UNICODETEXT; }
                else { Console.Error.WriteLine("未知模式 " + mode); return 1; }
            }
        }
        catch (Exception ex) { Console.Error.WriteLine("准备数据失败: " + ex.Message); return 2; }

        // 延迟渲染必须由一个真实窗口认领属主，否则 SetClipboardData(fmt, NULL) 会失败。
        // 建完窗口后故意不泵消息 —— 就得到一个"问它要数据永远不回答"的来源程序。
        IntPtr owner = IntPtr.Zero;
        if (mode == "hang")
        {
            System.Windows.Forms.Form f = new System.Windows.Forms.Form();
            f.ShowInTaskbar = false;
            owner = f.Handle;
        }

        IntPtr mem = IntPtr.Zero;
        if (data != null)
        {
            mem = GlobalAlloc(GMEM_MOVEABLE, new UIntPtr((ulong)data.Length));
            if (mem == IntPtr.Zero) { Console.Error.WriteLine("GlobalAlloc 失败"); return 3; }
            IntPtr p = GlobalLock(mem);
            Marshal.Copy(data, 0, p, data.Length);
            GlobalUnlock(mem);
        }

        for (int i = 0; i < 40 && !OpenClipboard(owner); i++) Thread.Sleep(50);
        EmptyClipboard();
        bool ok = SetClipboardData(fmt, mem) != IntPtr.Zero;
        CloseClipboard();
        Console.WriteLine("clipput: " + mode + " set=" + ok + " 保持 " + hold + "s");
        Thread.Sleep(hold * 1000);
        return ok ? 0 : 4;
    }

    // PNG/JPG → CF_DIB：借 GDI+ 存成 BMP 再剥掉 14 字节文件头
    private static byte[] DibOf(string file)
    {
        using (System.Drawing.Image im = System.Drawing.Image.FromFile(file))
        using (System.Drawing.Bitmap bm = new System.Drawing.Bitmap(im))
        using (MemoryStream ms = new MemoryStream())
        {
            bm.Save(ms, System.Drawing.Imaging.ImageFormat.Bmp);
            byte[] all = ms.ToArray();
            byte[] dib = new byte[all.Length - 14];
            Array.Copy(all, 14, dib, 0, dib.Length);
            return dib;
        }
    }
}
