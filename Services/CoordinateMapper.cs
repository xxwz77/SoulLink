using System;

namespace SoulLink.Services
{
    /// <summary>
    /// 坐标换算：RDP 画面里的鼠标位置 → 远端会话的桌面坐标。
    ///
    /// 为什么要换算：RDP 控件按 DesktopWidth/Height 与自身控件尺寸的比例缩放画面，
    /// 而控件尺寸又随窗口大小变化（还有 DPI 缩放）。直接把 UI 像素当远端像素会偏。
    /// </summary>
    public sealed class CoordinateMapper
    {
        /// <summary>远端桌面尺寸（RDP 的 DesktopWidth/Height）</summary>
        public int RemoteWidth { get; set; }
        public int RemoteHeight { get; set; }

        /// <summary>控件在屏幕上的可用区域（不含黑边）</summary>
        public double ViewWidth { get; set; }
        public double ViewHeight { get; set; }

        /// <summary>画面在控件里的偏移（居中留黑边时使用）</summary>
        public double OffsetX { get; set; }
        public double OffsetY { get; set; }

        /// <summary>是否保持宽高比（控件的 SmartSizing）</summary>
        public bool KeepAspect { get; set; } = true;

        public bool IsValid => RemoteWidth > 0 && RemoteHeight > 0 && ViewWidth > 0 && ViewHeight > 0;

        /// <summary>
        /// 重新计算缩放与偏移。控件尺寸变化、分辨率变化时调用。
        /// </summary>
        public void Update(int remoteWidth, int remoteHeight, double viewWidth, double viewHeight)
        {
            RemoteWidth = remoteWidth;
            RemoteHeight = remoteHeight;
            ViewWidth = viewWidth;
            ViewHeight = viewHeight;

            if (!KeepAspect || remoteWidth <= 0 || remoteHeight <= 0)
            {
                OffsetX = 0;
                OffsetY = 0;
                return;
            }

            double scaleX = viewWidth / remoteWidth;
            double scaleY = viewHeight / remoteHeight;
            double scale = Math.Min(scaleX, scaleY);      // 取小的，保证整幅画面都在

            double drawnW = remoteWidth * scale;
            double drawnH = remoteHeight * scale;
            OffsetX = (viewWidth - drawnW) / 2.0;
            OffsetY = (viewHeight - drawnH) / 2.0;
        }

        /// <summary>UI 坐标（控件内相对像素）→ 远端桌面坐标。超出画面时夹紧。</summary>
        public (int x, int y) ToRemote(double uiX, double uiY)
        {
            if (!IsValid) return (0, 0);

            double localX = uiX - OffsetX;
            double localY = uiY - OffsetY;

            double scaleX, scaleY;
            if (KeepAspect)
            {
                double scale = Math.Min(ViewWidth / RemoteWidth, ViewHeight / RemoteHeight);
                scaleX = scaleY = scale;
            }
            else
            {
                scaleX = ViewWidth / RemoteWidth;
                scaleY = ViewHeight / RemoteHeight;
            }

            if (scaleX <= 0 || scaleY <= 0) return (0, 0);

            int rx = (int)Math.Round(localX / scaleX);
            int ry = (int)Math.Round(localY / scaleY);

            rx = Math.Clamp(rx, 0, RemoteWidth - 1);
            ry = Math.Clamp(ry, 0, RemoteHeight - 1);
            return (rx, ry);
        }

        /// <summary>远端桌面坐标 → UI 坐标（画光标标记用）。</summary>
        public (double x, double y) ToUi(int remoteX, int remoteY)
        {
            if (!IsValid) return (0, 0);

            double scaleX, scaleY;
            if (KeepAspect)
            {
                double scale = Math.Min(ViewWidth / RemoteWidth, ViewHeight / RemoteHeight);
                scaleX = scaleY = scale;
            }
            else
            {
                scaleX = ViewWidth / RemoteWidth;
                scaleY = ViewHeight / RemoteHeight;
            }

            return (OffsetX + remoteX * scaleX, OffsetY + remoteY * scaleY);
        }

        /// <summary>
        /// 相对位移换算：转视角时鼠标位移是**物理像素量级**的，
        /// 不该按缩放比例缩小，否则窗口一小转得就慢。这里只做轻度补偿。
        /// </summary>
        public (int dx, int dy) ScaleRelative(double deltaUiX, double deltaUiY, double sensitivity = 1.0)
        {
            if (!IsValid) return ((int)Math.Round(deltaUiX), (int)Math.Round(deltaUiY));

            // 画面被拉伸时，同样的手部位移在远端应该对应成比例的位移
            double scale = KeepAspect
                ? Math.Min(ViewWidth / RemoteWidth, ViewHeight / RemoteHeight)
                : (ViewWidth + ViewHeight) / (RemoteWidth + RemoteHeight);

            if (scale <= 0) scale = 1.0;
            double factor = sensitivity / scale;

            // 上限保护，避免拖动一下光标飞出屏幕
            int dx = (int)Math.Round(deltaUiX * factor);
            int dy = (int)Math.Round(deltaUiY * factor);
            dx = Math.Clamp(dx, -2000, 2000);
            dy = Math.Clamp(dy, -2000, 2000);
            return (dx, dy);
        }

        public string Describe()
        {
            if (!IsValid) return "未就绪";
            double sx = ViewWidth / RemoteWidth, sy = ViewHeight / RemoteHeight;
            return string.Format("远端 {0}x{1} · 视口 {2:F0}x{3:F0} · 缩放 {4:F3}x{5:F3} · 偏移 {6:F0},{7:F0}",
                RemoteWidth, RemoteHeight, ViewWidth, ViewHeight, sx, sy, OffsetX, OffsetY);
        }
    }
}
