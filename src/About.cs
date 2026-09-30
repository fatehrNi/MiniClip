using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace MiniClip
{
    // 关于 / 诊断信息：把复现问题需要的东西一屏给全，一键复制
    internal sealed class AboutForm : Form
    {
        public AboutForm(Store store)
        {
            string info = Ver.Brief(store);

            SuspendLayout();
            Text = "关于 " + Ver.Name;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(N.Px(520), N.Px(336));
            Font = new Font("Microsoft YaHei UI", 9f);
            KeyPreview = true;

            Label head = new Label();
            head.AutoSize = true;
            head.Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold);
            head.Location = new Point(N.Px(18), N.Px(16));
            head.Text = Ver.Name + " " + Ver.Number;
            Controls.Add(head);

            Label sub = new Label();
            sub.AutoSize = true;
            sub.Location = new Point(N.Px(20), N.Px(48));
            sub.Text = Ver.Description + "　" + Ver.License + "　" + Ver.Copyright;
            Controls.Add(sub);

            TextBox tb = new TextBox();
            tb.Multiline = true;
            tb.ReadOnly = true;
            tb.ScrollBars = ScrollBars.Vertical;
            tb.Location = new Point(N.Px(20), N.Px(74));
            tb.Size = new Size(N.Px(480), N.Px(190));
            tb.Font = new Font("Consolas", 9f);
            tb.Text = info;
            Controls.Add(tb);

            Label priv = new Label();
            priv.Location = new Point(N.Px(20), N.Px(268));
            priv.Size = new Size(N.Px(480), N.Px(34));
            priv.Text = "所有数据只写在本机的 data 目录，程序不联网、不上传。";
            Controls.Add(priv);

            Button copy = new Button();
            copy.Text = "复制诊断信息";
            copy.Size = new Size(N.Px(112), N.Px(28));
            copy.Location = new Point(N.Px(204), N.Px(296));
            copy.Click += delegate
            {
                ClipboardFns.SetText(info);
                copy.Text = "已复制";
            };
            Controls.Add(copy);

            Button dir = new Button();
            dir.Text = "打开数据目录";
            dir.Size = new Size(N.Px(112), N.Px(28));
            dir.Location = new Point(N.Px(322), N.Px(296));
            dir.Click += delegate
            {
                try { Directory.CreateDirectory(Cfg.DataDir); Process.Start("explorer.exe", Cfg.DataDir); }
                catch { }
            };
            Controls.Add(dir);

            Button ok = new Button();
            ok.Text = "关闭";
            ok.Size = new Size(N.Px(76), N.Px(28));
            ok.Location = new Point(N.Px(424), N.Px(296));
            ok.DialogResult = DialogResult.OK;
            Controls.Add(ok);

            AcceptButton = ok;
            CancelButton = ok;
            KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Close(); };
            ResumeLayout(false);
        }
    }

    // 剪贴板写入的极简封装（关于框里复制文本用，不经过历史）
    internal static class ClipboardFns
    {
        public static void SetText(string s)
        {
            try { Cap.WriteText(IntPtr.Zero, s); } catch { }
        }
    }
}
