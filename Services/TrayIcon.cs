using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SoulLink.Services
{
    /// <summary>托盘图标 + 右键菜单（无 WinForms 窗口，配合 UseWPF 也能工作）。</summary>
    public sealed class TrayIcon : IDisposable
    {
        private readonly NotifyIcon _icon;
        private readonly Icon _appIcon;

        public event Action? ShowRequested;
        public event Action? QuickOpenRequested;
        public event Action? ExitRequested;

        public TrayIcon(string tooltip)
        {
            _appIcon = BuildIcon();
            _icon = new NotifyIcon
            {
                Icon = _appIcon,
                Text = Trim(tooltip, 63),
                Visible = true,
                ContextMenuStrip = BuildMenu()
            };
            _icon.DoubleClick += (_, __) => ShowRequested?.Invoke();
            _icon.BalloonTipClicked += (_, __) => ShowRequested?.Invoke();
        }

        private ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip { ShowImageMargin = false, Font = new Font("Microsoft YaHei UI", 9f) };
            menu.Items.Add(new ToolStripMenuItem("打开 SoulLink", null, (_, __) => ShowRequested?.Invoke())
            { Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold) });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("执行上次的动作", null, (_, __) => QuickOpenRequested?.Invoke()));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("退出", null, (_, __) => ExitRequested?.Invoke()));
            return menu;
        }

        public void SetTip(string text) => _icon.Text = Trim(text, 63);

        public void Balloon(string title, string text, ToolTipIcon icon = ToolTipIcon.Info)
        {
            try { _icon.ShowBalloonTip(3000, title, Trim(text, 250), icon); } catch { }
        }

        private static string Trim(string s, int n) => string.IsNullOrEmpty(s) || s.Length <= n ? s ?? "" : s.Substring(0, n);

        /// <summary>程序内绘制图标：紫青渐变圆环 + 中间光点（与界面风格一致）。</summary>
        public static Icon BuildIcon()
        {
            using (var bmp = new Bitmap(64, 64))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);

                    using (var brush = new LinearGradientBrush(new Rectangle(0, 0, 64, 64),
                               Color.FromArgb(124, 92, 255), Color.FromArgb(64, 224, 255), 45f))
                    using (var pen = new Pen(brush, 7f))
                    {
                        g.DrawEllipse(pen, 9, 9, 46, 46);
                    }

                    using (var glow = new SolidBrush(Color.FromArgb(56, 255, 255, 255)))
                        g.FillEllipse(glow, 17, 17, 30, 30);

                    using (var core = new LinearGradientBrush(new Rectangle(20, 20, 24, 24),
                               Color.White, Color.FromArgb(180, 210, 255), 45f))
                        g.FillEllipse(core, 21, 21, 22, 22);
                }
                IntPtr h = bmp.GetHicon();
                using (var tmp = Icon.FromHandle(h))
                    return (Icon)tmp.Clone();
            }
        }

        public void Dispose()
        {
            try { _icon.Visible = false; _icon.Dispose(); } catch { }
            try { _appIcon.Dispose(); } catch { }
        }
    }
}
