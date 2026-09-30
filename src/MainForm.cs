using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace MiniClip
{
    internal sealed class MainForm : Form
    {
        private readonly Store _store;
        private readonly Monitor _mon;
        private readonly NotifyIcon _tray;
        private ListView _list;
        private TextBox _search;
        private TextBox _detail;
        private Label _status;
        private SplitContainer _split;
        private System.Windows.Forms.Timer _filterTimer;
        private System.Windows.Forms.Timer _statusTimer;
        private readonly List<ClipItem> _view = new List<ClipItem>();
        private long _viewRev = -1;
        private int _sortCol = -1;
        private bool _sortAsc = true;
        private bool _reallyQuit;
        private long _lastId;

        public MainForm(Store store, Monitor mon)
        {
            _store = store;
            _mon = mon;

            SuspendLayout();
            Text = Ver.Title;
            Name = "MiniClipMain";
            AutoScaleMode = AutoScaleMode.None;
            MinimumSize = new Size(N.Px(640), N.Px(320));
            ShowInTaskbar = true;
            KeyPreview = true;
            Font = new Font("Microsoft YaHei UI", 9f);
            BackColor = SystemColors.Window;
            Icon = MakeIcon();
            PlaceOnScreen();

            Panel top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = N.Px(34);
            top.Padding = new Padding(8, 7, 8, 5);

            _search = new TextBox();
            _search.Dock = DockStyle.Fill;
            _search.Name = "搜索";
            _search.AccessibleName = "搜索";
            _search.TextChanged += OnSearchChanged;
            _search.KeyDown += OnSearchKeyDown;

            Label hint = new Label();
            hint.Dock = DockStyle.Left;
            hint.AutoSize = true;
            hint.Text = "  搜索    ";
            hint.TextAlign = ContentAlignment.MiddleLeft;

            Label tail = new Label();
            tail.Dock = DockStyle.Right;
            tail.AutoSize = true;
            tail.Text = "Enter 取用   Alt+1..9 直取   Del 删除   Ctrl+P 置顶   Esc 收起   ";
            tail.TextAlign = ContentAlignment.MiddleRight;

            top.Controls.Add(_search);
            top.Controls.Add(hint);
            top.Controls.Add(tail);

            _split = new SplitContainer();
            _split.Dock = DockStyle.Fill;
            _split.Orientation = Orientation.Horizontal;
            _split.SplitterWidth = 5;
            _split.Panel1MinSize = 80;
            _split.Panel2MinSize = 40;

            _list = new ListView();
            _list.Dock = DockStyle.Fill;
            _list.View = View.Details;
            _list.VirtualMode = true;
            _list.MultiSelect = false;
            _list.FullRowSelect = true;
            _list.HideSelection = false;
            _list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            _list.Columns.Add("#", N.Px(40), HorizontalAlignment.Right);
            _list.Columns.Add("时间", N.Px(128));
            _list.Columns.Add("来源程序", N.Px(126));
            _list.Columns.Add("窗口标题", N.Px(210));
            _list.Columns.Add("类型", N.Px(46));
            _list.Columns.Add("大小", N.Px(66), HorizontalAlignment.Right);
            _list.Columns.Add("重复", N.Px(46), HorizontalAlignment.Right);
            _list.Columns.Add("内容预览", N.Px(360));
            _list.RetrieveVirtualItem += OnRetrieve;
            _list.SelectedIndexChanged += OnSelected;
            _list.DoubleClick += delegate { CopySelected(); };
            _list.KeyDown += OnListKeyDown;

            _detail = new TextBox();
            _detail.Dock = DockStyle.Fill;
            _detail.Multiline = true;
            _detail.ReadOnly = true;
            // 正文按窗口宽度自动换行：长段落不再挤成一行需要横向拖动
            _detail.ScrollBars = ScrollBars.Vertical;
            _detail.WordWrap = true;
            _detail.BackColor = Color.FromArgb(250, 250, 250);
            _detail.Font = new Font("Consolas", 9f);
            ContextMenu cm = new ContextMenu(new MenuItem[] {
                new MenuItem("复制详情", OnCopyDetail),
                new MenuItem("清空预览", OnClearDetail)
            });
            _detail.ContextMenu = cm;

            _split.Panel1.Controls.Add(_list);
            _split.Panel2.Controls.Add(_detail);

            _status = new Label();
            _status.Dock = DockStyle.Bottom;
            _status.Height = N.Px(22);
            _status.Padding = new Padding(8, 4, 8, 2);
            _status.Text = "";

            Controls.Add(_split);
            Controls.Add(_status);
            Controls.Add(top);
            ResumeLayout(false);

            FormClosing += OnClosing;
            KeyDown += OnFormKeyDown;
            Shown += delegate { BuildView(); FitColumns(); FocusSearchOrList(); UpdateStatus(); };
            Resize += delegate
            {
                int detail = N.Px(200);
                if (_split.Height > detail + 120)
                {
                    try { _split.SplitterDistance = _split.Height - detail; } catch { }
                }
                FitColumns();
            };

            _filterTimer = new System.Windows.Forms.Timer();
            _filterTimer.Interval = 140;
            _filterTimer.Tick += delegate { _filterTimer.Stop(); BuildView(); };

            _statusTimer = new System.Windows.Forms.Timer();
            _statusTimer.Interval = 3000;
            _statusTimer.Tick += delegate { UpdateStatus(); };
            // 不在这里 Start：只有窗口可见时才需要刷新状态栏

            _mon.Changed += OnHistoryChanged;

            _tray = new NotifyIcon();
            _tray.Icon = MakeIcon();
            _tray.Text = Ver.Name + " 剪贴板历史 · " + Cfg.Hotkey.ToUpperInvariant() + " 唤出";
            _tray.ContextMenu = BuildTrayMenu();
            _tray.DoubleClick += delegate { ShowFromTray(); };
            _tray.Visible = true;
            if (Cfg.FirstRun)
            {
                // 第一次跑必须说清"程序在哪、怎么唤出"，否则找不到入口
                _tray.BalloonTipTitle = Ver.Name + " " + Ver.Number + " 已在托盘运行";
                _tray.BalloonTipText = "按 " + Cfg.Hotkey + " 唤出历史；右键托盘图标可以看关于与退出。数据保存在程序旁边的 data 文件夹。";
                _tray.ShowBalloonTip(8000);
            }

            BuildView();
        }

        // ---- 托盘 ----

        private ContextMenu BuildTrayMenu()
        {
            MenuItem miShow = new MenuItem("显示历史  (热键)", OnTrayShow);
            MenuItem miPause = new MenuItem("暂停记录", OnTrayPause);
            miPause.RadioCheck = false;
            MenuItem miClear = new MenuItem("清空历史", OnTrayClear);
            MenuItem miDir = new MenuItem("打开数据目录", OnTrayDir);
            MenuItem miAbout = new MenuItem("关于 / 诊断信息", OnTrayAbout);
            MenuItem miQuit = new MenuItem("退出 " + Ver.Name, OnTrayQuit);
            ContextMenu menu = new ContextMenu();
            menu.MenuItems.Add(miShow);
            menu.MenuItems.Add(new MenuItem("-"));
            menu.MenuItems.Add(miPause);
            menu.MenuItems.Add(miClear);
            menu.MenuItems.Add(miDir);
            menu.MenuItems.Add(miAbout);
            menu.MenuItems.Add(new MenuItem("-"));
            menu.MenuItems.Add(miQuit);
            menu.Popup += delegate
            {
                miPause.Text = (_mon.Enabled ? "暂停记录" : "继续记录");
                miPause.Checked = !_mon.Enabled;
                miShow.Text = "显示历史  (" + (Cfg.HotkeyEnabled ? Cfg.Hotkey : "未启用") + ")";
            };
            return menu;
        }

        private void OnTrayShow(object s, EventArgs e) { ShowFromTray(); }
        private void OnTrayPause(object s, EventArgs e) { _mon.Enabled = !_mon.Enabled; UpdateStatus(); }
        private void OnTrayClear(object s, EventArgs e)
        {
            DialogResult r = MessageBox.Show(this, "清空全部历史？（置顶条目会保留）", "MiniClip",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r == DialogResult.Yes) { _store.Clear(true); BuildView(); UpdateStatus(); }
        }
        private void OnTrayDir(object s, EventArgs e)
        {
            try { Process.Start("explorer.exe", Cfg.DataDir); } catch { }
        }
        private void OnTrayQuit(object s, EventArgs e) { QuitNow(); }

        private void OnTrayAbout(object s, EventArgs e)
        {
            using (AboutForm a = new AboutForm(_store)) a.ShowDialog(this);
        }

        // 运行时代码画图标，省掉一个随包资源文件
        private static Icon MakeIcon()
        {
            using (Bitmap bmp = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (SolidBrush bg = new SolidBrush(Color.FromArgb(232, 236, 242)))
                        g.FillRectangle(bg, 3, 2, 26, 28);
                    using (Pen p = new Pen(Color.FromArgb(90, 110, 140), 1.6f))
                        g.DrawRectangle(p, 3, 2, 26, 28);
                    using (SolidBrush clip = new SolidBrush(Color.FromArgb(40, 110, 200)))
                    {
                        g.FillRectangle(clip, 11, 0, 10, 6);
                        g.FillRectangle(clip, 12, 8, 8, 3);
                    }
                    using (Pen l = new Pen(Color.FromArgb(70, 90, 120), 1.8f))
                    {
                        g.DrawLine(l, 8, 15, 24, 15);
                        g.DrawLine(l, 8, 20, 24, 20);
                        g.DrawLine(l, 8, 25, 18, 25);
                    }
                }
                Icon ico = Icon.FromHandle(bmp.GetHicon());
                return (Icon)ico.Clone();
            }
        }

        // ---- 历史视图 ----

        // 记住的位置可能在副屏被拔掉后落在屏幕外，这里保证一定看得见、且不超过工作区
        private void PlaceOnScreen()
        {
            Screen pri = Screen.PrimaryScreen;
            Rectangle wa = pri.WorkingArea;
            int w = Math.Max(MinimumSize.Width, N.Px(Cfg.WinW));
            int h = Math.Max(MinimumSize.Height, N.Px(Cfg.WinH));
            if (w > wa.Width - 24) w = wa.Width - 24;
            if (h > wa.Height - 24) h = wa.Height - 24;
            int x, y;
            if (Cfg.WinX >= -30000 && Cfg.WinY >= -30000)
            {
                x = Cfg.WinX; y = Cfg.WinY;
                Rectangle want = new Rectangle(x, y, w, h);
                bool onSomeScreen = false;
                Screen[] all = Screen.AllScreens;
                for (int i = 0; i < all.Length; i++)
                    if (all[i].WorkingArea.IntersectsWith(want)) { onSomeScreen = true; break; }
                if (!onSomeScreen) { x = wa.Left + (wa.Width - w) / 2; y = wa.Top + (wa.Height - h) / 3; }
            }
            else
            {
                x = wa.Left + (wa.Width - w) / 2;
                y = wa.Top + (wa.Height - h) / 3;
            }
            StartPosition = FormStartPosition.Manual;
            Location = new Point(x, y);
            ClientSize = new Size(w, h);
        }

        // 预览列吃掉剩余宽度：避免出现横向滚动条
        private void FitColumns()
        {
            if (_list == null || _list.Columns.Count < 2) return;
            int fixedSum = 0;
            for (int i = 0; i < _list.Columns.Count - 1; i++) fixedSum += _list.Columns[i].Width;
            int avail = _list.ClientSize.Width - fixedSum - 6;
            if (avail < 160) avail = 160;
            _list.Columns[_list.Columns.Count - 1].Width = avail;
        }

        private void OnHistoryChanged()
        {
            if (!IsHandleCreated || !Visible) { _viewRev = -1; return; }
            BuildView();
            UpdateStatus();
        }

        private void OnSearchChanged(object s, EventArgs e)
        {
            _filterTimer.Stop();
            _filterTimer.Start();
        }

        private void BuildView()
        {
            string q = _search == null ? "" : _search.Text.Trim();
            _view.Clear();
            List<ClipItem> all = _store.Snapshot();
            for (int i = 0; i < all.Count; i++)
            {
                ClipItem it = all[i];
                if (q.Length > 0 && !Matches(it, q)) continue;
                _view.Add(it);
            }
            if (_sortCol >= 0) SortView();
            _viewRev = _store.Revision;
            if (_list != null && _list.IsHandleCreated)
            {
                long keep = _lastId;
                _list.VirtualListSize = _view.Count;
                _list.Invalidate();
                if (keep != 0) SelectById(keep);
                // 打开面板时默认选中第一条，详情区不留白
                if (_list.SelectedIndices.Count == 0 && _view.Count > 0) _list.SelectedIndices.Add(0);
            }
        }

        private bool Matches(ClipItem it, string q)
        {
            if (it.Preview != null && it.Preview.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (it.App != null && it.App.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (it.Title != null && it.Title.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (it.KindText.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private void SortView()
        {
            Comparison<ClipItem> cmp = null;
            switch (_sortCol)
            {
                case 1: cmp = delegate(ClipItem a, ClipItem b) { return a.Ts.CompareTo(b.Ts); }; break;
                case 2: cmp = delegate(ClipItem a, ClipItem b) { return string.Compare(a.App, b.App, StringComparison.OrdinalIgnoreCase); }; break;
                case 4: cmp = delegate(ClipItem a, ClipItem b) { return ((int)a.Kind).CompareTo((int)b.Kind); }; break;
                case 5: cmp = delegate(ClipItem a, ClipItem b) { return a.Size.CompareTo(b.Size); }; break;
                case 6: cmp = delegate(ClipItem a, ClipItem b) { return a.Hits.CompareTo(b.Hits); }; break;
                default: break;
            }
            if (cmp == null) return;
            if (!_sortAsc) { Comparison<ClipItem> inner = cmp; cmp = delegate(ClipItem a, ClipItem b) { return inner(b, a); }; }
            _view.Sort(cmp);
        }

        private void OnRetrieve(object s, RetrieveVirtualItemEventArgs e)
        {
            List<ClipItem> view = _view;
            if (e.ItemIndex < 0 || e.ItemIndex >= view.Count) { e.Item = new ListViewItem(""); return; }
            ClipItem it = view[e.ItemIndex];
            ListViewItem li = new ListViewItem((e.ItemIndex + 1).ToString());
            li.SubItems.Add(it.TimeText);
            li.SubItems.Add(it.App);
            li.SubItems.Add(it.Title);
            li.SubItems.Add((it.Pin ? "★" : "") + it.KindText);
            li.SubItems.Add(it.SizeText);
            li.SubItems.Add(it.Hits > 1 ? "×" + it.Hits : "");
            li.SubItems.Add(it.Preview);
            if (it.Pin) li.BackColor = Color.FromArgb(255, 248, 225);
            e.Item = li;
        }

        private ClipItem SelectedItem()
        {
            if (_list.SelectedIndices.Count == 0) return null;
            int i = _list.SelectedIndices[0];
            if (i < 0 || i >= _view.Count) return null;
            return _view[i];
        }

        private void SelectById(long id)
        {
            if (id == 0) return;
            for (int i = 0; i < _view.Count; i++)
            {
                if (_view[i].Id == id)
                {
                    _list.SelectedIndices.Add(i);
                    return;
                }
            }
        }

        private void OnSelected(object s, EventArgs e)
        {
            ClipItem it = SelectedItem();
            if (it == null) { _detail.Text = ""; return; }
            _lastId = it.Id;
            ShowDetail(it);
        }

        private void ShowDetail(ClipItem it)
        {
            StringBuilder sb = new StringBuilder(512);
            string nl = Environment.NewLine;   // 多行 TextBox 只认 CRLF，用 \n 会被压成一行
            sb.Append("时间  ").Append(Time.At(it.Ts).ToString("yyyy-MM-dd HH:mm:ss")).Append(nl);
            sb.Append("来源  ").Append(it.App);
            if (it.Path.Length > 0) sb.Append("   ").Append(it.Path);
            sb.Append(nl);
            sb.Append("窗口  ").Append(it.Title).Append(nl);
            sb.Append("类型  ").Append(it.KindText).Append("    大小 ").Append(it.SizeText);
            if (it.Hits > 1) sb.Append("    重复 ×").Append(it.Hits);
            if (it.Trim) sb.Append("    (已截断)");
            sb.Append("    ").Append(it.Pin ? "已置顶" : "").Append(nl);
            sb.Append("格式  ").Append(it.Fmt).Append(nl);
            sb.Append("------ 正文 ------").Append(nl);

            string head = sb.ToString();
            string body = _store.Content(it);
            if (it.Kind == Kind.Image || it.Kind == Kind.Audio)
            {
                string full = Path.Combine(_store.Root, body.Replace('/', Path.DirectorySeparatorChar));
                sb.Append(full).Append(nl).Append(nl)
                  .Append(it.Kind == Kind.Image ? "（图片文件，回车可直接把图片放回剪贴板）"
                                                : "（音频文件，回车可直接把音频放回剪贴板）");
            }
            else if (body.Length > 200000)
            {
                sb.Append(body, 0, 200000).Append(nl).Append("……（预览截断，回车仍复制完整内容）");
            }
            else sb.Append(body);

            _detail.Text = sb.ToString();
            _detail.SelectionStart = head.Length;
            _detail.SelectionLength = 0;
        }

        private void OnCopyDetail(object s, EventArgs e)
        {
            if (_detail.Text.Length == 0) return;
            Cap.WriteText(_mon.SelfHwnd, _detail.Text);
            _mon.AfterSelfWrite();
        }

        private void OnClearDetail(object s, EventArgs e) { _detail.Clear(); }

        // ---- 动作 ----

        private void CopyItem(ClipItem it, bool close)
        {
            if (it == null) return;
            IntPtr owner = _mon.SelfHwnd;
            bool ok;
            string body = _store.Content(it);
            if (it.Kind == Kind.Image)
            {
                string full = Path.Combine(_store.Root, body.Replace('/', Path.DirectorySeparatorChar));
                ok = File.Exists(full) && Cap.WriteImage(owner, full);
            }
            else if (it.Kind == Kind.Audio)
            {
                string full = Path.Combine(_store.Root, body.Replace('/', Path.DirectorySeparatorChar));
                ok = File.Exists(full) && Cap.WriteAudio(owner, full);
            }
            else if (it.Kind == Kind.Files)
            {
                ok = Cap.WriteFiles(owner, body);
            }
            else
            {
                ok = Cap.WriteText(owner, body);
            }
            if (!ok) { Flash("放回剪贴板失败：剪贴板被其它程序占用，重试一次即可"); return; }
            _mon.AfterSelfWrite();
            _store.Touch(it);
            if (close) Hide();
            else BuildView();
        }

        private void CopySelected()
        {
            ClipItem it = SelectedItem();
            if (it != null) CopyItem(it, true);
        }

        private void DeleteSelected()
        {
            ClipItem it = SelectedItem();
            if (it == null) return;
            _store.Delete(it.Id);
            _lastId = 0;
            BuildView();
            UpdateStatus();
        }

        private void PinSelected()
        {
            ClipItem it = SelectedItem();
            if (it == null) return;
            _store.SetPin(it.Id, !it.Pin);
            BuildView();
        }

        private void Flash(string msg)
        {
            _status.Text = msg;
            if (Visible) MessageBox.Show(this, msg, "MiniClip", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        // ---- 键盘 ----

        private void OnFormKeyDown(object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { Hide(); e.Handled = true; }
        }

        private void OnSearchKeyDown(object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Down) { _list.Focus(); if (_view.Count > 0) _list.SelectedIndices.Add(0); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter) { CopySelected(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.A) { _list.Focus(); e.Handled = true; }
        }

        private void OnListKeyDown(object s, KeyEventArgs e)
        {
            int i = _list.SelectedIndices.Count > 0 ? _list.SelectedIndices[0] : -1;
            if (e.KeyCode == Keys.Enter) { CopySelected(); e.Handled = true; }
            else if (e.KeyCode == Keys.Delete) { DeleteSelected(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.D) { DeleteSelected(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.P) { PinSelected(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.C && i >= 0) { CopyItem(_view[i], false); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.F) { FocusSearch(); e.Handled = true; }
            else if (e.Alt && e.KeyCode >= Keys.D1 && e.KeyCode <= Keys.D9)
            {
                int n = e.KeyCode - Keys.D1;
                if (n < _view.Count) CopyItem(_view[n], true);
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode == Keys.Back)
            {
                _search.Clear();
                e.Handled = true;
            }
            else if (!e.Control && !e.Alt && !char.IsControl(e.KeyCode.ToString()[0]) && i < 0 && _view.Count > 0)
            {
                // 直接在列表上打字 -> 跳到搜索框
                FocusSearch();
                _search.AppendText(e.KeyCode.ToString().ToLowerInvariant());
                e.Handled = true;
            }
        }

        private void FocusSearch()
        {
            _search.Focus();
            _search.SelectAll();
        }

        private void FocusSearchOrList()
        {
            if (_search.Text.Length == 0) FocusSearch();
            else _list.Focus();
        }

        // ---- 显示 / 隐藏 ----

        public void ShowFromTray()
        {
            if (Visible && _frontmost) Hide();
            else ShowWindow();
        }

        public void HidePanel() { if (Visible) Hide(); }

        public void CopyById(long id)
        {
            ClipItem it = _store.GetById(id);
            if (it == null) { Tracer.Log("ipc copy id=" + id + " 不存在"); return; }
            CopyItem(it, false);
            Tracer.Log("ipc copied id=" + id);
        }

        private bool _frontmost;

        public void ShowWindow()
        {
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            if (Left < -20000 || Top < -20000) PlaceOnScreen();   // 历史遗留的坏位置自愈
            Show();
            if (!N.SetForegroundWindow(Handle))
            {
                // 后台进程默认没有抢前台的权限，模拟一次 ALT 换取前台权（唤出型窗口的标准做法）
                N.keybd_event(0x12, 0, 0, UIntPtr.Zero);
                N.keybd_event(0x12, 0, 2, UIntPtr.Zero);
                N.SetForegroundWindow(Handle);
            }
            Activate();
            _frontmost = true;
            if (_viewRev != _store.Revision) BuildView();
            FocusSearchOrList();
            UpdateStatus();
            Probe.Counters("show");
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            _frontmost = true;
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            _frontmost = false;
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible)
            {
                _statusTimer.Start();
                FitColumns();       // 首次显示前算列宽会拿到 0 客户区，这里补一次
            }
            else
            {
                // 收起时停掉所有定时器、取消置顶并把内存还给系统：常驻进程只在被使用的一瞬间占资源
                _statusTimer.Stop();
                _filterTimer.Stop();
                Cap.TrimWorkingSet();
                Probe.Counters("hide");
            }
        }

        private void OnClosing(object s, FormClosingEventArgs e)
        {
            if (!_reallyQuit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            if (!_reallyQuit) { e.Cancel = true; QuitNow(); return; }
            Rectangle rb = RestoreBounds;   // 最小化状态下 Left/Top 是 (-32000,-32000)，必须用 RestoreBounds
            if (rb.Width > 100 && rb.Height > 100)
            {
                Cfg.WinW = (int)(rb.Width / N.Scale);   // 存逻辑像素，别每次启动再乘一遍系数
                Cfg.WinH = (int)(rb.Height / N.Scale);
                Cfg.WinX = rb.X;
                Cfg.WinY = rb.Y;
            }
            Cfg.Save();
            _tray.Visible = false;
        }

        public void QuitNow()
        {
            _reallyQuit = true;
            _mon.Flush();
            _tray.Visible = false;
            Application.Exit();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Minimized && _tray != null)
            {
                // 最小化 = 收进托盘。必须先恢复成 Normal 再隐藏：
                // 否则窗口的 Left/Top 会永久停在 (-32000,-32000)，下次唤出就跑到屏幕外。
                WindowState = FormWindowState.Normal;
                Hide();
                Cap.TrimWorkingSet();
            }
        }

        // ---- 状态栏 ----

        private void UpdateStatus()
        {
            Process p = Process.GetCurrentProcess();
            long ws = p.WorkingSet64 / 1024;
            string mode = Cfg.PollMs > 0 ? ("轮询 " + Cfg.PollMs + "ms") : "事件驱动";
            _status.Text = string.Format(
                "{0} 条    新增 {1} / 去重 {2}    正文 {3} + 索引 {4}    内存 {5:0.0} MB    {6}{7}",
                _store.Count, _mon.Captured, _mon.Duplicated,
                Store.HumanBytes(_store.ContentBytes), Store.HumanBytes(_store.IndexBytes),
                ws / 1024.0, mode, _mon.Enabled ? "" : "    【已暂停记录】");
        }
    }
}
