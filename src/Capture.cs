using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MiniClip
{
    internal sealed class CapResult
    {
        public Kind Kind;
        public string Text;      // 文本正文 / 文件列表（\n 连接）
        public byte[] Png;       // 图片 PNG
        public byte[] Wave;      // 音频 WAV（CF_WAVE / CF_RIFF）
        public int W, H;
        public long Size;        // 原始字节数
        public string Fmts;      // 剪贴板格式清单
    }

    // 剪贴板读写：全部走 Win32，读操作只在专用线程上执行，UI 线程永不接触剪贴板
    internal static class Cap
    {
        private static string StandardName(uint id)
        {
            switch (id)
            {
                case 1: return "CF_LINK";
                case 2: return "CF_BITMAP";
                case 3: return "CF_METAFILEPICT";
                case 4: return "CF_SYLK";
                case 5: return "CF_DIF";
                case 6: return "CF_TIFF";
                case 7: return "CF_OEMTEXT";
                case 8: return "CF_DIB";
                case 9: return "CF_PALETTE";
                case 10: return "CF_PENDATA";
                case 11: return "CF_RIFF";
                case 12: return "CF_WAVE";
                case 13: return "CF_UNICODETEXT";
                case 14: return "CF_ENHMETAFILE";
                case 15: return "CF_HDROP";
                case 16: return "CF_LOCALE";
                case 17: return "CF_DIBV5";
                default: return null;
            }
        }

        private static string FormatList()
        {
            StringBuilder sb = new StringBuilder(96);
            uint f = 0;
            int n = 0;
            while ((f = N.EnumClipboardFormats(f)) != 0 && n < 12)
            {
                if (n > 0) sb.Append(';');
                string name = StandardName(f);
                if (name == null)
                {
                    StringBuilder nb = new StringBuilder(64);
                    if (N.GetClipboardFormatName(f, nb, nb.Capacity) > 0) name = nb.ToString();
                    else name = "0x" + f.ToString("x");
                }
                sb.Append(name);
                n++;
            }
            return sb.ToString();
        }

        private static byte[] MemToBytes(IntPtr mem)
        {
            if (mem == IntPtr.Zero) return null;
            UIntPtr sz = N.GlobalSize(mem);
            long len = (long)sz.ToUInt64();
            if (len <= 0) return null;
            IntPtr p = N.GlobalLock(mem);
            if (p == IntPtr.Zero) return null;
            try
            {
                byte[] b = new byte[len];
                Marshal.Copy(p, b, 0, (int)len);
                return b;
            }
            finally { N.GlobalUnlock(mem); }
        }

        // ---- 读 ----

        // 只读：把剪贴板里的原始字节拷出来就立刻关闭句柄，解码/转PNG/落盘全部在锁外做。
        // 剪贴板是系统级独占资源，多占 1ms 都是在全机器的路上堵车。
        public static CapResult Read()
        {
            if (!N.OpenClipboardRetry(10, 6)) { Tracer.Log("剪贴板被占用，本次跳过"); return null; }

            Kind kind = Kind.Text;
            string fmts = "";
            byte[] raw = null;
            byte[] dib = null;
            byte[] pngFromDdb = null;
            byte[] wave = null;
            string[] files = null;

            try
            {
                IntPtr owner = N.GetClipboardOwner();
                if (owner != IntPtr.Zero && N.IsHungAppWindow(owner))
                {
                    // 延迟渲染的拥有者已经卡住，去问它要数据会把我们一起拖死
                    Tracer.Log("来源程序未响应，跳过 seq 采集");
                    return null;
                }
                fmts = FormatList();

                if (N.IsClipboardFormatAvailable(N.CF_UNICODETEXT))
                {
                    raw = MemToBytes(N.GetClipboardData(N.CF_UNICODETEXT));
                    if (raw == null || raw.Length < 2) return null;
                    kind = Kind.Text;
                }
                else if (N.IsClipboardFormatAvailable(N.CF_DIB) || N.IsClipboardFormatAvailable(N.CF_DIBV5))
                {
                    uint f = N.IsClipboardFormatAvailable(N.CF_DIB) ? N.CF_DIB : N.CF_DIBV5;
                    dib = MemToBytes(N.GetClipboardData(f));
                    kind = Kind.Image;
                }
                else if (N.IsClipboardFormatAvailable(N.CF_BITMAP))
                {
                    IntPtr hb = N.GetClipboardData(N.CF_BITMAP);
                    if (hb == IntPtr.Zero) return null;
                    kind = Kind.Image;
                    // 只有裸 HBITMAP 时必须在锁内转成位图，句柄离开剪贴板就失效
                    using (Bitmap bm = (Bitmap)Bitmap.FromHbitmap(hb))
                    {
                        pngFromDdb = ToPng(bm);
                    }
                }
                else if (N.IsClipboardFormatAvailable(N.CF_WAVE) || N.IsClipboardFormatAvailable(N.CF_RIFF))
                {
                    uint wf = N.IsClipboardFormatAvailable(N.CF_WAVE) ? N.CF_WAVE : N.CF_RIFF;
                    wave = MemToBytes(N.GetClipboardData(wf));
                    kind = Kind.Audio;
                }
                else if (N.IsClipboardFormatAvailable(N.CF_HDROP))
                {
                    files = ParseHDrop(MemToBytes(N.GetClipboardData(N.CF_HDROP)));
                    if (files == null || files.Length == 0) return null;
                    kind = Kind.Files;
                }
                else return null;
            }
            catch (Exception ex)
            {
                Tracer.Log("read fail " + ex.GetType().Name + " " + ex.Message);
                return null;
            }
            finally
            {
                N.CloseClipboard();
            }

            CapResult r = new CapResult();
            r.Fmts = fmts;
            r.Kind = kind;
            if (kind == Kind.Audio)
            {
                if (wave == null || wave.Length < 16) return null;
                r.Wave = wave;
                r.Size = wave.Length;
                return r;
            }
            if (kind == Kind.Text)
            {
                int chars = raw.Length / 2;
                StringBuilder sb = new StringBuilder(chars);
                for (int i = 0; i < chars; i++)
                {
                    char c = (char)(raw[i * 2] | (raw[i * 2 + 1] << 8));
                    if (c == '\0') break;
                    sb.Append(c);
                }
                if (sb.Length == 0) return null;
                r.Text = sb.ToString();
                r.Size = Encoding.UTF8.GetByteCount(r.Text);
                return r;
            }
            if (kind == Kind.Image)
            {
                if (pngFromDdb != null)
                {
                    r.Png = pngFromDdb;
                    r.Size = pngFromDdb.Length;
                    using (MemoryStream ms = new MemoryStream(pngFromDdb))
                    using (Image im = Image.FromStream(ms)) { r.W = im.Width; r.H = im.Height; }
                    return r;
                }
                r.Png = DibToPng(dib, out r.W, out r.H);
                if (r.Png == null) return null;
                r.Size = r.Png.Length;
                return r;
            }
            StringBuilder fl = new StringBuilder(128);
            for (int i = 0; i < files.Length; i++)
            {
                if (i > 0) fl.Append('\n');
                fl.Append(files[i]);
            }
            r.Text = fl.ToString();
            r.Size = files.Length;
            return r;
        }

        private static byte[] ToPng(Image img)
        {
            using (MemoryStream ms = new MemoryStream(65536))
            {
                img.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }

        // CF_DIB/CF_DIBV5 = BITMAPINFOHEADER 起头的裸 DIB，补 14 字节文件头即可交给 GDI+
        private static byte[] DibToPng(byte[] dib, out int w, out int h)
        {
            w = 0; h = 0;
            if (dib == null || dib.Length < 40) return null;
            try
            {
                int biSize = BitConverter.ToInt32(dib, 0);
                int biWidth = BitConverter.ToInt32(dib, 4);
                int biHeight = BitConverter.ToInt32(dib, 8);
                short biBitCount = BitConverter.ToInt16(dib, 14);
                int biClrUsed = BitConverter.ToInt32(dib, 32);
                int palette = biClrUsed != 0 ? biClrUsed : (biBitCount <= 8 ? (1 << biBitCount) : 0);
                int offBits = biSize + palette * 4;
                if (biSize == 124) offBits = biSize + palette * 4;   // DIBV5 调色板紧随其头

                byte[] bmp = new byte[14 + dib.Length];
                bmp[0] = (byte)'B';
                bmp[1] = (byte)'M';
                Put32(bmp, 2, 14 + dib.Length);
                Put32(bmp, 10, offBits);
                Buffer.BlockCopy(dib, 0, bmp, 14, dib.Length);

                using (MemoryStream ms = new MemoryStream(bmp))
                using (Image img = Image.FromStream(ms))
                {
                    w = Math.Abs(biWidth);
                    h = Math.Abs(biHeight);
                    if (w == 0) { w = img.Width; h = img.Height; }
                    return ToPng(img);
                }
            }
            catch (Exception ex)
            {
                Tracer.Log("dib fail " + ex.Message);
                return null;
            }
        }

        private static void Put32(byte[] b, int at, int v)
        {
            b[at] = (byte)(v & 0xFF);
            b[at + 1] = (byte)((v >> 8) & 0xFF);
            b[at + 2] = (byte)((v >> 16) & 0xFF);
            b[at + 3] = (byte)((v >> 24) & 0xFF);
        }

        private static string[] ParseHDrop(byte[] d)
        {
            if (d == null || d.Length < 20) return null;
            int pFiles = BitConverter.ToInt32(d, 0);
            bool wide = BitConverter.ToInt32(d, 16) != 0;
            if (pFiles < 20 || pFiles >= d.Length) pFiles = 20;
            List<string> list = new List<string>(8);
            if (wide)
            {
                StringBuilder sb = new StringBuilder(260);
                for (int i = pFiles; i + 1 < d.Length; i += 2)
                {
                    char c = (char)(d[i] | (d[i + 1] << 8));
                    if (c == '\0') { if (sb.Length == 0) break; list.Add(sb.ToString()); sb.Length = 0; }
                    else sb.Append(c);
                }
            }
            else
            {
                StringBuilder sb = new StringBuilder(260);
                for (int i = pFiles; i < d.Length; i++)
                {
                    char c = (char)d[i];
                    if (c == '\0') { if (sb.Length == 0) break; list.Add(sb.ToString()); sb.Length = 0; }
                    else sb.Append(c);
                }
            }
            return list.Count == 0 ? null : list.ToArray();
        }

        // ---- 写（把历史条目送回剪贴板）----

        public static bool WriteText(IntPtr owner, string text)
        {
            byte[] bytes = Encoding.Unicode.GetBytes((text ?? "") + "\0");
            IntPtr mem = N.GlobalAlloc(N.GMEM_MOVEABLE, new UIntPtr((ulong)bytes.Length));
            if (mem == IntPtr.Zero) return false;
            IntPtr p = N.GlobalLock(mem);
            if (p == IntPtr.Zero) { N.GlobalFree(mem); return false; }
            Marshal.Copy(bytes, 0, p, bytes.Length);
            N.GlobalUnlock(mem);
            if (!N.OpenClipboardOwned(owner, 12, 6)) { N.GlobalFree(mem); return false; }
            try
            {
                N.EmptyClipboard();
                if (N.SetClipboardData(N.CF_UNICODETEXT, mem) == IntPtr.Zero) { N.GlobalFree(mem); return false; }
                return true;
            }
            finally { N.CloseClipboard(); }
        }

        public static bool WriteImage(IntPtr owner, string pngPath)
        {
            try
            {
                using (Bitmap src = new Bitmap(pngPath))
                using (Bitmap bm = new Bitmap(src))     // 统一成 32bpp，避免调色板位图进剪贴板
                {
                    byte[] dib = DibOf(bm);
                    IntPtr hbmp = bm.GetHbitmap();
                    IntPtr memDib = BytesToGlobal(dib);
                    if (hbmp == IntPtr.Zero || memDib == IntPtr.Zero)
                    {
                        if (hbmp != IntPtr.Zero) N.DeleteObject(hbmp);
                        if (memDib != IntPtr.Zero) N.GlobalFree(memDib);
                        return false;
                    }
                    if (!N.OpenClipboardOwned(owner, 12, 6))
                    {
                        N.DeleteObject(hbmp);
                        N.GlobalFree(memDib);
                        return false;
                    }
                    try
                    {
                        N.EmptyClipboard();
                        bool ok = N.SetClipboardData(N.CF_DIB, memDib) != IntPtr.Zero;
                        if (N.SetClipboardData(N.CF_BITMAP, hbmp) == IntPtr.Zero) ok = false;
                        return ok;
                    }
                    finally { N.CloseClipboard(); }
                }
            }
            catch (Exception ex) { Tracer.Log("write img fail " + ex.Message); return false; }
        }

        public static bool WriteAudio(IntPtr owner, string path)
        {
            byte[] data;
            try { data = File.ReadAllBytes(path); } catch { return false; }
            bool pcm = path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".riff", StringComparison.OrdinalIgnoreCase);
            if (!pcm) return WriteFiles(owner, path);
            IntPtr mem = BytesToGlobal(data);
            if (mem == IntPtr.Zero) return false;
            if (!N.OpenClipboardOwned(owner, 12, 6)) { N.GlobalFree(mem); return false; }
            try
            {
                N.EmptyClipboard();
                return N.SetClipboardData(N.CF_WAVE, mem) != IntPtr.Zero;
            }
            finally { N.CloseClipboard(); }
        }

        public static bool WriteFiles(IntPtr owner, string joinedPaths)
        {
            string[] ps = (joinedPaths ?? "").Split('\n');
            List<string> list = new List<string>(ps.Length);
            for (int i = 0; i < ps.Length; i++) if (ps[i].Length > 0) list.Add(ps[i]);
            if (list.Count == 0) return false;
            int chars = 1;
            for (int i = 0; i < list.Count; i++) chars += list[i].Length + 1;
            byte[] buf = new byte[20 + chars * 2 + 2];
            Put32(buf, 0, 20);
            Put32(buf, 16, 1);               // fWide
            int at = 20;
            for (int i = 0; i < list.Count; i++)
            {
                string s = list[i];
                for (int j = 0; j < s.Length; j++) { ushort v = s[j]; buf[at] = (byte)v; buf[at + 1] = (byte)(v >> 8); at += 2; }
                buf[at] = 0; buf[at + 1] = 0; at += 2;
            }
            buf[at] = 0; buf[at + 1] = 0;
            IntPtr mem = BytesToGlobal(buf);
            if (mem == IntPtr.Zero) return false;
            if (!N.OpenClipboardOwned(owner, 12, 6)) { N.GlobalFree(mem); return false; }
            try
            {
                N.EmptyClipboard();
                return N.SetClipboardData(N.CF_HDROP, mem) != IntPtr.Zero;
            }
            finally { N.CloseClipboard(); }
        }

        private static IntPtr BytesToGlobal(byte[] bytes)
        {
            IntPtr mem = N.GlobalAlloc(N.GMEM_MOVEABLE, new UIntPtr((ulong)bytes.Length));
            if (mem == IntPtr.Zero) return IntPtr.Zero;
            IntPtr p = N.GlobalLock(mem);
            if (p == IntPtr.Zero) { N.GlobalFree(mem); return IntPtr.Zero; }
            Marshal.Copy(bytes, 0, p, bytes.Length);
            N.GlobalUnlock(mem);
            return mem;
        }

        // BMP 流去掉 14 字节文件头就是 CF_DIB
        private static byte[] DibOf(Bitmap bm)
        {
            using (MemoryStream ms = new MemoryStream(65536))
            {
                bm.Save(ms, ImageFormat.Bmp);
                byte[] all = ms.ToArray();
                if (all.Length <= 14) return null;
                byte[] dib = new byte[all.Length - 14];
                Buffer.BlockCopy(all, 14, dib, 0, dib.Length);
                return dib;
            }
        }

        public static void TrimWorkingSet()
        {
            try
            {
                IntPtr p = new IntPtr(-1);
                N.SetProcessWorkingSetSize(p, new IntPtr(-1), new IntPtr(-1));
            }
            catch { }
        }
    }
}
