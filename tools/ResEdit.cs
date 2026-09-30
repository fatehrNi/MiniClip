using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

// 给编译好的 exe 注入 Win32 版本资源（RT_VERSION）。
// 这台机器上没有 rc.exe / SDK，但 kernel32 自带 UpdateResource 系列 API，
// 所以版本资源可以自己在内存里拼出来再写进 PE，不需要任何外部工具。
//
// 用法： resedit.exe <目标exe> <key=value 描述文件>
//   version=1.0.0.0
//   productversion=1.0.0
//   description=...
//   product=...
//   company=...
//   copyright=...
//   original=MiniClip.exe
internal static class ResEdit
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr BeginUpdateResource(string fileName, bool deleteExisting);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool UpdateResource(IntPtr handle, IntPtr type, IntPtr name, ushort language, byte[] data, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool EndUpdateResource(IntPtr handle, bool discard);

    private static readonly IntPtr RT_VERSION = new IntPtr(16);
    private static readonly IntPtr ID_VERSION = new IntPtr(1);
    // csc 已经写了一份“语言中性”的版本资源；
    // 想覆盖它就必须用同一个语言 ID，否则加载器继续读原来那份空的。
    private const ushort LANG = 0x0000;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr LoadLibraryEx(string f, IntPtr h, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr FindResource(IntPtr h, IntPtr name, IntPtr type);
    [DllImport("kernel32.dll")]
    static extern uint SizeofResource(IntPtr h, IntPtr r);
    [DllImport("kernel32.dll")]
    static extern IntPtr LoadResource(IntPtr h, IntPtr r);
    const uint LOAD_LIBRARY_AS_DATAFILE = 0x00000002;

    [STAThread]
    private static int Main(string[] argv)
    {
        if (argv.Length >= 2 && argv[0] == "-read") return ReadBack(argv[1]);
        if (argv.Length < 2)
        {
            Console.Error.WriteLine("用法: resedit.exe <exe> <version.txt>");
            return 1;
        }
        Dictionary<string, string> kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in File.ReadAllLines(argv[1], Encoding.UTF8))
        {
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            kv[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
        }

        int[] v = ParseQuad(Get(kv, "version", "0.0.0.0"));
        byte[] blob = Build(v, kv);

        IntPtr h = BeginUpdateResource(argv[0], false);
        if (h == IntPtr.Zero) { Console.Error.WriteLine("BeginUpdateResource 失败 err=" + Marshal.GetLastWin32Error()); return 2; }
        if (!UpdateResource(h, RT_VERSION, ID_VERSION, LANG, blob, (uint)blob.Length))
        {
            Console.Error.WriteLine("UpdateResource 失败 err=" + Marshal.GetLastWin32Error());
            EndUpdateResource(h, true);
            return 3;
        }
        if (!EndUpdateResource(h, false))
        {
            Console.Error.WriteLine("EndUpdateResource 失败 err=" + Marshal.GetLastWin32Error());
            return 4;
        }
        Console.WriteLine("version resource injected: " + Get(kv, "product", "?") + " " + Get(kv, "version", "?"));
        if (Environment.GetEnvironmentVariable("RESEDIT_DUMP") == "1") Dump(blob);
        return 0;
    }

    // 用同一个 Walk 读别人家的正确资源，结构差异一目了然
    private static int ReadBack(string path)
    {
        IntPtr mod = LoadLibraryEx(path, IntPtr.Zero, LOAD_LIBRARY_AS_DATAFILE);
        if (mod == IntPtr.Zero) { Console.Error.WriteLine("LoadLibraryEx 失败 " + Marshal.GetLastWin32Error()); return 5; }
        IntPtr rs = FindResource(mod, ID_VERSION, RT_VERSION);
        if (rs == IntPtr.Zero) { Console.Error.WriteLine("无 RT_VERSION 资源"); return 6; }
        uint size = SizeofResource(mod, rs);
        IntPtr data = LoadResource(mod, rs);
        byte[] b = new byte[size];
        Marshal.Copy(data, b, 0, (int)size);
        Console.WriteLine("-- " + path + "  size=" + size);
        Walk(b, 0, BitConverter.ToUInt16(b, 0), "");
        return 0;
    }

    // 自己拼的结构自己再走一遍：字段错位这种问题看一眼就定位了
    private static void Dump(byte[] b)
    {
        Walk(b, 0, BitConverter.ToUInt16(b, 0), "");
    }

    private static void Walk(byte[] b, int off, int len, string indent)
    {
        int end = off + len;
        int valueLen = BitConverter.ToUInt16(b, off + 2);
        int type = BitConverter.ToUInt16(b, off + 4);
        int p = off + 6;
        var sb = new StringBuilder();
        while (p + 1 < b.Length)
        {
            char c = (char)(b[p] | (b[p + 1] << 8));
            p += 2;
            if (c == (char)0) break;
            sb.Append(c);
        }
        Console.WriteLine(indent + "node '" + sb + "' valueLen=" + valueLen + " type=" + type
            + " total=" + len + " keyEnd=" + p);
        if (valueLen > 0 && type == 0)
        {
            int v = (p + 3) & ~3;
            Console.WriteLine(indent + "  binary value at " + v + " (pad " + (v - p) + ")");
            p = v + valueLen;
        }
        else if (valueLen > 0 && type == 1)
        {
            int v = (p + 3) & ~3;
            string val = "";
            for (int i = 0; i < valueLen; i++) val += (char)(b[v + i * 2] | (b[v + i * 2 + 1] << 8));
            Console.WriteLine(indent + "  text value at " + v + " = [" + val.TrimEnd((char)0) + "]");
            p = v + valueLen * 2;
        }
        p = (p + 3) & ~3;
        while (p + 4 < end && p + 2 <= b.Length)
        {
            int childLen = BitConverter.ToUInt16(b, p);
            if (childLen <= 0) break;
            Walk(b, p, childLen, indent + "  ");
            p = (p + childLen + 3) & ~3;
        }
    }

    private static string Get(Dictionary<string, string> kv, string k, string def)
    {
        string s;
        return kv.TryGetValue(k, out s) && s.Length > 0 ? s : def;
    }

    private static int[] ParseQuad(string s)
    {
        string[] p = s.Split('.');
        int[] r = new int[4];
        for (int i = 0; i < 4; i++)
        {
            int n;
            r[i] = (i < p.Length && int.TryParse(p[i], out n)) ? n : 0;
        }
        return r;
    }

    // ---- VS_VERSIONINFO 拼装：每个节点都按 32 位对齐 ----

    private static byte[] Build(int[] v, Dictionary<string, string> kv)
    {
        byte[] fixedInfo = FixedInfo(v);
        List<byte[]> strings = new List<byte[]>();
        AddString(strings, "CompanyName", Get(kv, "company", ""));
        AddString(strings, "FileDescription", Get(kv, "description", ""));
        AddString(strings, "FileVersion", Get(kv, "fileversion", Get(kv, "version", "")));
        AddString(strings, "InternalName", Get(kv, "internal", "MiniClip"));
        AddString(strings, "LegalCopyright", Get(kv, "copyright", ""));
        AddString(strings, "LegalTrademarks", Get(kv, "trademarks", ""));
        AddString(strings, "OriginalFilename", Get(kv, "original", "MiniClip.exe"));
        AddString(strings, "ProductName", Get(kv, "product", "MiniClip"));
        AddString(strings, "ProductVersion", Get(kv, "productversion", Get(kv, "version", "")));

        byte[] block = Node(LANG.ToString("X4") + "04B0", null, strings); // 块名必须大写
        List<byte[]> sfi = new List<byte[]>();
        sfi.Add(block);
        byte[] stringFileInfo = Node("StringFileInfo", null, sfi);

        byte[] trans = new byte[] { (byte)(LANG & 0xFF), (byte)(LANG >> 8), 0xB0, 0x04 }; // 语言 + 代码页 1200
        List<byte[]> vfi = new List<byte[]>();
        vfi.Add(Node("Translation", trans, null));
        byte[] varFileInfo = Node("VarFileInfo", null, vfi);

        List<byte[]> top = new List<byte[]>();
        top.Add(stringFileInfo);
        top.Add(varFileInfo);
        return Node("VS_VERSION_INFO", fixedInfo, top);
    }

    private static void AddString(List<byte[]> list, string key, string value)
    {
        byte[] val = ToWcharsZ(value);
        // 字符串节点里 wValueLength 统计的是 WCHAR 个数
        list.Add(NodeText(key, val, val.Length / 2));
    }

    private static byte[] FixedInfo(int[] v)
    {
        MemoryStream ms = new MemoryStream(52);
        W(ms, unchecked((int)0xFEEF04BD));
        W(ms, 0x00010000);
        W(ms, (v[0] << 16) | (v[1] & 0xFFFF));
        W(ms, (v[2] << 16) | (v[3] & 0xFFFF));
        W(ms, (v[0] << 16) | (v[1] & 0xFFFF));
        W(ms, (v[2] << 16) | (v[3] & 0xFFFF));
        W(ms, 0x0000003F);   // flags mask
        W(ms, 0);            // flags
        W(ms, 0x00040004);   // VOS_NT_WINDOWS32
        W(ms, 1);            // VFT_APP
        W(ms, 0);            // subtype
        W(ms, 0); W(ms, 0);  // date
        return ms.ToArray();
    }

    private static void W(MemoryStream ms, int value)
    {
        byte[] b = BitConverter.GetBytes(value);
        ms.Write(b, 0, 4);
    }

    private static void H(MemoryStream ms, int value)
    {
        byte[] b = BitConverter.GetBytes((ushort)value);
        ms.Write(b, 0, 2);
    }

    private static byte[] ToWcharsZ(string s)
    {
        byte[] b = Encoding.Unicode.GetBytes(s ?? "");
        byte[] o = new byte[b.Length + 2];
        Array.Copy(b, o, b.Length);
        return o;
    }

    private static void Align(MemoryStream ms)
    {
        while (ms.Position % 4 != 0) ms.WriteByte(0);
    }

    // value == null -> 文本容器节点 (wType=1)；否则二进制节点 (wType=0)
    private static byte[] Node(string key, byte[] value, List<byte[]> children)
    {
        MemoryStream ms = new MemoryStream();
        H(ms, 0);                                   // 长度先占位
        H(ms, value == null ? 0 : value.Length);
        H(ms, value == null ? 1 : 0);
        ms.Write(Encoding.Unicode.GetBytes(key + "\0"), 0, (key.Length + 1) * 2);
        Align(ms);
        if (value != null) ms.Write(value, 0, value.Length);
        if (children != null)
        {
            Align(ms);
            for (int i = 0; i < children.Count; i++) ms.Write(children[i], 0, children[i].Length);
        }
        byte[] all = ms.ToArray();
        int len = all.Length;
        all[0] = (byte)(len & 0xFF);
        all[1] = (byte)((len >> 8) & 0xFF);
        return all;
    }

    private static byte[] NodeText(string key, byte[] value, int wcharCount)
    {
        MemoryStream ms = new MemoryStream();
        H(ms, 0);
        H(ms, wcharCount);
        H(ms, 1);                                   // 文本
        ms.Write(Encoding.Unicode.GetBytes(key + "\0"), 0, (key.Length + 1) * 2);
        Align(ms);
        ms.Write(value, 0, value.Length);
        Align(ms);
        byte[] all = ms.ToArray();
        int len = all.Length;
        all[0] = (byte)(len & 0xFF);
        all[1] = (byte)((len >> 8) & 0xFF);
        return all;
    }
}
