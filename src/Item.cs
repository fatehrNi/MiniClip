using System;

namespace MiniClip
{
    // 剪贴板内容类型
    internal enum Kind : byte
    {
        Text = 1,
        Image = 2,
        Files = 3,
        Audio = 4
    }

    // 一条剪贴板记录：只常驻元数据，正文按需从 content.log 偏移读取
    internal sealed class ClipItem
    {
        public long Id;              // 单调递增
        public long Ts;              // Unix 毫秒（复制发生时刻）
        public Kind Kind;
        public string App;           // 来源进程名，如 chrome.exe
        public string Title;         // 复制瞬间的前台窗口标题
        public string Preview;       // 展平后的前若干字符，界面直接显示
        public long Bytes;           // content.log 中该条正文的转义字节数（IO 寻址用）
        public long Size;            // 原始大小：文本 UTF-8 字节 / 图片 PNG 字节 / 文件条目数
        public long Chars;           // 正文字符数
        public ulong Hash;           // FNV-1a 64，用于去重
        public long Off;             // content.log 中正文起始偏移
        public bool Pin;             // 固定（不被淘汰）
        public int Hits;             // 重复复制次数
        public bool Trim;            // 正文被截断
        public string Fmt;           // 复制时剪贴板上的格式列表，如 "CF_UNICODETEXT;HTML Format"
        public string Path;          // 来源程序完整路径（可得时）

        public ClipItem()
        {
            Hits = 1;
        }

        public string TimeText
        {
            get
            {
                DateTime t = Time.At(Ts);
                int days = (int)(DateTime.Now.Date.Subtract(t.Date).TotalDays);
                if (days == 0) return "今天 " + t.ToString("HH:mm:ss");
                if (days == 1) return "昨天 " + t.ToString("HH:mm:ss");
                if (t.Year == DateTime.Now.Year) return t.ToString("MM-dd HH:mm:ss");
                return t.ToString("yyyy-MM-dd HH:mm");
            }
        }

        public string KindText
        {
            get
            {
                if (Kind == Kind.Text) return "文本";
                if (Kind == Kind.Image) return "图片";
                if (Kind == Kind.Audio) return "音频";
                return "文件";
            }
        }

        public string SizeText
        {
            get
            {
                if (Kind == Kind.Image || Kind == Kind.Audio) return Store.HumanBytes(Size);
                if (Kind == Kind.Files) return Chars + " 项";
                if (Chars >= 10000) return (Chars / 1000) + "K 字";
                return Chars + " 字";
            }
        }
    }
}
