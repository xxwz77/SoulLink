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
            ApplyMenuTheme(menu);
            menu.Opening += (_, _) => ApplyMenuTheme(menu);
            menu.Items.Add(new ToolStripMenuItem("打开 SoulLink", null, (_, __) => ShowRequested?.Invoke())
            { Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold) });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("执行上次的动作", null, (_, __) => QuickOpenRequested?.Invoke()));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("退出", null, (_, __) => ExitRequested?.Invoke()));
            return menu;
        }

        private static void ApplyMenuTheme(ContextMenuStrip menu)
        {
            bool dark=ThemeManager.IsDark;
            menu.BackColor=dark?Color.FromArgb(23,22,46):Color.FromArgb(255,251,255);
            menu.ForeColor=dark?Color.FromArgb(242,235,255):Color.FromArgb(60,44,92);
            menu.Renderer=new ToolStripProfessionalRenderer(new DreamMenuColors(dark));
        }

        private sealed class DreamMenuColors : ProfessionalColorTable
        {
            private readonly bool _dark;
            public DreamMenuColors(bool dark) => _dark=dark;
            public override Color MenuBorder => _dark?Color.FromArgb(84,75,114):Color.FromArgb(166,147,194);
            public override Color MenuItemSelected => _dark?Color.FromArgb(61,52,90):Color.FromArgb(236,224,247);
            public override Color MenuItemSelectedGradientBegin => MenuItemSelected;
            public override Color MenuItemSelectedGradientEnd => MenuItemSelected;
            public override Color MenuItemBorder => _dark?Color.FromArgb(160,126,195):Color.FromArgb(174,137,204);
            public override Color MenuItemPressedGradientBegin => MenuItemSelected;
            public override Color MenuItemPressedGradientEnd => MenuItemSelected;
            public override Color ToolStripDropDownBackground => _dark?Color.FromArgb(23,22,46):Color.FromArgb(255,251,255);
            public override Color ImageMarginGradientBegin => ToolStripDropDownBackground;
            public override Color ImageMarginGradientMiddle => ToolStripDropDownBackground;
            public override Color ImageMarginGradientEnd => ToolStripDropDownBackground;
            public override Color SeparatorDark => _dark?Color.FromArgb(60,53,84):Color.FromArgb(214,201,229);
            public override Color SeparatorLight => Color.Transparent;
        }

        public void SetTip(string text) => _icon.Text = Trim(text, 63);

        public void Balloon(string title, string text, ToolTipIcon icon = ToolTipIcon.Info)
        {
            try { _icon.ShowBalloonTip(3000, title, Trim(text, 250), icon); } catch { }
        }

        private static string Trim(string s, int n) => string.IsNullOrEmpty(s) || s.Length <= n ? s ?? "" : s.Substring(0, n);

        /// <summary>
        /// 托盘图标：优先用 Assetssoullink.ico（与应用图标一致）。
        ///
        /// ★ 以前这里是"代码画一个紫青圆环"，和 exe 图标不是一个东西，
        ///   任务栏/托盘/桌面看到三种图案。现在统一到同一个 ico 资源。
        ///   资源读不到时才回退到旧的手绘图案，保证不因缺资源而崩。
        /// </summary>
        public static Icon BuildIcon()
        {
            Icon? fromResource = LoadFromResource();
            if (fromResource != null) return fromResource;
            return BuildFallbackIcon();
        }

        /// <summary>从打包资源加载 Assets/soullink.ico（按托盘所需尺寸取帧）。</summary>
        private static Icon? LoadFromResource()
        {
            try
            {
                var uri = new Uri("pack://application:,,,/Assets/soullink.ico", UriKind.Absolute);
                var info = System.Windows.Application.GetResourceStream(uri);
                if (info == null) return null;
                using var stream = info.Stream;
                return new Icon(stream, SystemInformation.SmallIconSize);
            }
            catch { return null; }
        }

        /// <summary>回退：旧的手绘紫青圆环（仅当 ico 资源不可用时使用）。</summary>
        private static Icon BuildFallbackIcon()
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
