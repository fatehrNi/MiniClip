using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace MiniClip
{
    // 追加式双文件存储：
    //   index.log   每行一条元数据（A=新增 U=去重命中 P=置顶 D=删除）
    //   content.log 正文区，元数据里只存 (off, bytes)，按需 seek 读取
    //   images/     图片正文，按内容 hash 命名，天然去重
    // 常驻内存的只有元数据 + 短预览，正文不进堆。
    internal sealed class Store
    {
        public const int Fields = 17;

        private readonly string _dir;
        private readonly string _indexPath;
        private readonly string _contentPath;

        private readonly object _gate = new object();
        private readonly List<ClipItem> _items = new List<ClipItem>();
        private readonly Dictionary<long, ClipItem> _byId = new Dictionary<long, ClipItem>();
        private readonly byte[] _nl = new byte[] { (byte)'\n' };

        private FileStream _indexWr;
        private FileStream _contentWr;
        private FileStream _contentRd;
        private long _contentLen;
        private long _dead;
        private long _nextId = 1;
        private long _revision;

        public Store(string dir)
        {
            _dir = dir;
            _indexPath = Path.Combine(dir, "index.log");
            _contentPath = Path.Combine(dir, "content.log");
        }

        public string Root { get { return _dir; } }
        public string ImageDir { get { return Path.Combine(_dir, "images"); } }
        public string MediaDir { get { return Path.Combine(_dir, "media"); } }
        public long Revision { get { return _revision; } }
        public int Count { get { lock (_gate) return _items.Count; } }
        public long ContentBytes { get { lock (_gate) return _contentLen; } }
        public long DeadBytes { get { lock (_gate) return _dead; } }
        public long IndexBytes { get { return File.Exists(_indexPath) ? new FileInfo(_indexPath).Length : 0; } }

        public static string HumanBytes(long b)
        {
            if (b < 1024) return b + " B";
            if (b < 1048576) return (b / 1024.0).ToString("0.#") + " KB";
            return (b / 1048576.0).ToString("0.##") + " MB";
        }

        private void Open()
        {
            Directory.CreateDirectory(_dir);
            Directory.CreateDirectory(ImageDir);
            _indexWr = new FileStream(_indexPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096);
            _contentWr = new FileStream(_contentPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 32768);
            _contentRd = new FileStream(_contentPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 32768);
            _contentLen = _contentRd.Length;
        }

        private void CloseFiles()
        {
            if (_indexWr != null) { _indexWr.Dispose(); _indexWr = null; }
            if (_contentWr != null) { _contentWr.Dispose(); _contentWr = null; }
            if (_contentRd != null) { _contentRd.Dispose(); _contentRd = null; }
        }

        public int Load()
        {
            lock (_gate)
            {
                _items.Clear();
                _byId.Clear();
                long maxId = 0;
                if (File.Exists(_indexPath))
                {
                    using (FileStream fs = new FileStream(_indexPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 262144))
                    using (StreamReader rd = new StreamReader(fs, Encoding.UTF8))
                    {
                        string line;
                        while ((line = rd.ReadLine()) != null)
                        {
                            if (line.Length < 3 || line[0] == '#') continue;
                            ApplyLine(line);
                        }
                    }
                    foreach (KeyValuePair<long, ClipItem> kv in _byId)
                        if (kv.Key > maxId) maxId = kv.Key;
                }
                _nextId = maxId + 1;
                Open();
                _revision++;
                return _items.Count;
            }
        }

        private void ApplyLine(string line)
        {
            string[] f = line.Split('\t');
            if (f.Length < 2) return;
            long id;
            if (!long.TryParse(f[1], out id)) return;
            char verb = f[0].Length == 1 ? f[0][0] : ' ';
            ClipItem it;
            _byId.TryGetValue(id, out it);

            if (verb == 'A')
            {
                if (it != null || f.Length < Fields) return;
                ClipItem n = new ClipItem();
                n.Id = id;
                n.Ts = Num(f[2]);
                n.Kind = (Kind)(byte)Num(f[3]);
                n.App = Util.Unesc(f[4]);
                n.Title = Util.Unesc(f[5]);
                n.Chars = Num(f[6]);
                n.Size = Num(f[7]);
                n.Off = Num(f[8]);
                n.Bytes = Num(f[9]);
                n.Hash = (ulong)Num(f[10]);
                n.Pin = f[11] == "1";
                n.Hits = (int)Num(f[12], 1);
                n.Trim = f[13] == "1";
                n.Fmt = Util.Unesc(f[14]);
                n.Path = Util.Unesc(f[15]);
                n.Preview = Util.Unesc(f[16]);
                _items.Insert(0, n);
                _byId[id] = n;
            }
            else if (verb == 'U')
            {
                if (it == null) return;
                it.Ts = Num(f[2]);
                it.Hits = (int)Num(f[3], 1);
                _items.Remove(it);
                _items.Insert(0, it);
            }
            else if (verb == 'P')
            {
                if (it == null) return;
                it.Pin = f[2] == "1";
            }
            else if (verb == 'D')
            {
                if (it == null) return;
                Drop(it, false);
            }
            _revision++;
        }

        private static long Num(string s) { long v; return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0; }
        private static long Num(string s, long def) { long v; return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : def; }

        private string LineOf(ClipItem it)
        {
            StringBuilder sb = new StringBuilder(192);
            sb.Append('A').Append('\t').Append(it.Id).Append('\t').Append(it.Ts).Append('\t')
              .Append((int)it.Kind).Append('\t').Append(Util.Esc(it.App)).Append('\t').Append(Util.Esc(it.Title)).Append('\t')
              .Append(it.Chars).Append('\t').Append(it.Size).Append('\t').Append(it.Off).Append('\t').Append(it.Bytes).Append('\t')
              .Append((long)it.Hash).Append('\t').Append(it.Pin ? 1 : 0).Append('\t').Append(it.Hits).Append('\t')
              .Append(it.Trim ? 1 : 0).Append('\t').Append(Util.Esc(it.Fmt)).Append('\t').Append(Util.Esc(it.Path)).Append('\t')
              .Append(Util.Esc(it.Preview));
            return sb.ToString();
        }

        private void WriteIndex(string line)
        {
            byte[] b = Encoding.UTF8.GetBytes(line);
            _indexWr.Write(b, 0, b.Length);
            _indexWr.Write(_nl, 0, 1);
            _indexWr.Flush();
        }

        // 图片条目：body 存相对路径，真实字节在 Size
        public ClipItem Add(ClipItem it, string body)
        {
            lock (_gate)
            {
                byte[] eb = Encoding.UTF8.GetBytes(body == null ? "" : Util.Esc(body));
                it.Off = _contentLen;
                it.Bytes = eb.Length;
                _contentWr.Write(eb, 0, eb.Length);
                _contentWr.Write(_nl, 0, 1);
                _contentWr.Flush();
                _contentLen += eb.Length + 1;

                if (it.Id == 0) it.Id = _nextId++;
                if (it.Ts == 0) it.Ts = Time.Now();
                if (it.Hits == 0) it.Hits = 1;
                _items.Insert(0, it);
                _byId[it.Id] = it;
                _revision++;
                WriteIndex(LineOf(it));
                Evict();
                return it;
            }
        }

        public void Touch(ClipItem it)
        {
            lock (_gate)
            {
                it.Ts = Time.Now();
                it.Hits++;
                _items.Remove(it);
                _items.Insert(0, it);
                _revision++;
                WriteIndex("U\t" + it.Id + "\t" + it.Ts + "\t" + it.Hits);
            }
        }

        public void SetPin(long id, bool pin)
        {
            lock (_gate)
            {
                ClipItem it;
                if (!_byId.TryGetValue(id, out it)) return;
                it.Pin = pin;
                _revision++;
                WriteIndex("P\t" + id + "\t" + (pin ? 1 : 0));
            }
        }

        public void Delete(long id)
        {
            lock (_gate)
            {
                ClipItem it;
                if (!_byId.TryGetValue(id, out it)) return;
                Drop(it, true);
                _revision++;
            }
        }

        public void Clear(bool keepPinned)
        {
            lock (_gate)
            {
                List<ClipItem> copy = new List<ClipItem>(_items);
                for (int i = 0; i < copy.Count; i++)
                {
                    if (keepPinned && copy[i].Pin) continue;
                    Drop(copy[i], true);
                }
                _revision++;
            }
        }

        // 只在内存移除 + 记账；图片文件延到 Compact 时统一清理，避免复制高峰期的同步 IO
        private void Drop(ClipItem it, bool appendRecord)
        {
            _items.Remove(it);
            _byId.Remove(it.Id);
            _dead += it.Bytes + 1;
            if (appendRecord) WriteIndex("D\t" + it.Id);
        }

        private void Evict()
        {
            int over = _items.Count - Cfg.MaxItems;
            if (over <= 0) return;
            for (int i = _items.Count - 1; i >= 0 && over > 0; i--)
            {
                ClipItem it = _items[i];
                if (it.Pin) continue;
                Drop(it, true);
                over--;
            }
        }

        public string Content(ClipItem it)
        {
            if (it == null) return "";
            lock (_gate)
            {
                if (it.Bytes <= 0 || it.Off + it.Bytes > _contentLen) return "";
                _contentRd.Seek(it.Off, SeekOrigin.Begin);
                byte[] buf = new byte[it.Bytes];
                int got = 0;
                while (got < buf.Length)
                {
                    int r = _contentRd.Read(buf, got, buf.Length - got);
                    if (r <= 0) break;
                    got += r;
                }
                if (got != buf.Length) return "";
                return Util.Unesc(Encoding.UTF8.GetString(buf, 0, got));
            }
        }

        public string Content(long id)
        {
            ClipItem it = GetById(id);
            return Content(it);
        }

        public string SaveImage(byte[] png)
        {
            ulong h = Util.Fnv1aBytes(png);
            string name = h.ToString("x16") + ".png";
            string full = Path.Combine(ImageDir, name);
            if (!File.Exists(full)) File.WriteAllBytes(full, png);
            return "images/" + name;
        }

        // 音频等二进制媒体：按内容哈希存 media/，重复复制天然去重
        public string SaveMedia(byte[] data, string ext)
        {
            ulong h = Util.Fnv1aBytes(data);
            string name = h.ToString("x16") + "." + ext;
            string dir = MediaDir;
            Directory.CreateDirectory(dir);
            string full = Path.Combine(dir, name);
            if (!File.Exists(full)) File.WriteAllBytes(full, data);
            return "media/" + name;
        }

        public bool NeedsCompact()
        {
            lock (_gate)
            {
                long limit = (long)Cfg.StoreMB * 1048576L;
                if (_contentLen > limit) return true;
                // 垃圾超过 256KB 且占比 1/4 以上就回收，否则留着追加写更划算
                return _dead > 262144 && _dead * 4 > _contentLen;
            }
        }

        // 重写：按当前 MRU 顺序输出，丢弃墓碑与死正文，顺带删掉不再引用的图片
        public void Compact()
        {
            List<ClipItem> snapshot;
            lock (_gate) snapshot = new List<ClipItem>(_items);

            string tmpC = _contentPath + ".tmp";
            string tmpI = _indexPath + ".tmp";
            long coff = 0;
            using (FileStream old = new FileStream(_contentPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 262144))
            using (FileStream cw = new FileStream(tmpC, FileMode.Create, FileAccess.Write, FileShare.None, 262144))
            using (StreamWriter iw = new StreamWriter(tmpI, false, new UTF8Encoding(false), 262144))
            {
                for (int i = 0; i < snapshot.Count; i++)
                {
                    ClipItem it = snapshot[i];
                    string raw = "";
                    if (it.Bytes > 0 && it.Off + it.Bytes <= old.Length)
                    {
                        old.Seek(it.Off, SeekOrigin.Begin);
                        byte[] buf = new byte[it.Bytes];
                        int got = 0;
                        while (got < buf.Length)
                        {
                            int r = old.Read(buf, got, buf.Length - got);
                            if (r <= 0) break;
                            got += r;
                        }
                        if (got == buf.Length) raw = Encoding.UTF8.GetString(buf, 0, got);
                    }
                    byte[] eb = Encoding.UTF8.GetBytes(raw);
                    cw.Write(eb, 0, eb.Length);
                    cw.Write(_nl, 0, 1);
                    it.Off = coff;
                    it.Bytes = eb.Length;
                    coff += eb.Length + 1;
                    iw.WriteLine(LineOf(it));
                }
                iw.Flush();
                cw.Flush(true);
            }

            lock (_gate)
            {
                CloseFiles();
                try
                {
                    File.Replace(tmpI, _indexPath, null, true);
                    File.Replace(tmpC, _contentPath, null, true);
                }
                catch
                {
                    try { File.Delete(tmpI); } catch { }
                    try { File.Delete(tmpC); } catch { }
                }
                _contentLen = File.Exists(_contentPath) ? new FileInfo(_contentPath).Length : 0;
                _dead = 0;
                Open();
                _revision++;
            }

            SweepImages(snapshot);
            Tracer.Log("compact items=" + snapshot.Count + " contentBytes=" + _contentLen);
        }

        private static string[] MergeFiles(string[] a, string dir, string pattern)
        {
            if (!Directory.Exists(dir)) return a;
            string[] b = Directory.GetFiles(dir, pattern);
            string[] all = new string[a.Length + b.Length];
            Array.Copy(a, all, a.Length);
            Array.Copy(b, 0, all, a.Length, b.Length);
            return all;
        }

        private void SweepImages(List<ClipItem> live)
        {
            try
            {
                HashSet<string> keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < live.Count; i++)
                {
                    if (live[i].Kind != Kind.Image && live[i].Kind != Kind.Audio) continue;
                    keep.Add(Util.Unesc(Path.GetFileName(Content(live[i]))));
                }
                string dir = ImageDir;
                if (!Directory.Exists(dir)) return;
                string[] files = Directory.GetFiles(dir, "*.png");
                files = MergeFiles(files, MediaDir, "*.*");
                long freed = 0;
                for (int i = 0; i < files.Length; i++)
                {
                    if (keep.Count > 0 && !keep.Contains(Path.GetFileName(files[i])))
                    {
                        try { long len = new FileInfo(files[i]).Length; File.Delete(files[i]); freed += len; }
                        catch { }
                    }
                }
                if (freed > 0) Tracer.Log("swept images bytes=" + freed);
            }
            catch { }
        }

        // 只看内容哈希（文本按去空白范围哈希），命中则刷新而不是新增
        public ClipItem FindRecentByHash(ulong hash, Kind kind)
        {
            lock (_gate)
            {
                int n = Math.Min(_items.Count, 40);
                for (int i = 0; i < n; i++)
                {
                    ClipItem it = _items[i];
                    if (it.Kind == kind && it.Hash == hash) return it;
                }
                return null;
            }
        }

        public ClipItem GetById(long id)
        {
            lock (_gate)
            {
                ClipItem it;
                _byId.TryGetValue(id, out it);
                return it;
            }
        }

        public ClipItem At(int index)
        {
            lock (_gate)
            {
                if (index < 0 || index >= _items.Count) return null;
                return _items[index];
            }
        }

        public List<ClipItem> Snapshot()
        {
            lock (_gate) return new List<ClipItem>(_items);
        }

        public void Shutdown()
        {
            lock (_gate) { CloseFiles(); }
        }
    }
}
