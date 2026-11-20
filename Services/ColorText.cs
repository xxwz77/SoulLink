using System;
using System.Globalization;
using System.Windows.Media;

namespace SoulLink.Services
{
    /// <summary>界面里手填颜色字符串的解析/格式化（#RGB / #ARGB / #RRGGBB / #AARRGGBB）。</summary>
    public static class ColorText
    {
        /// <summary>解析颜色；失败返回 null。不带 # 也能认。</summary>
        public static Color? TryParse(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var s = text.Trim();
            if (s.StartsWith("#", StringComparison.Ordinal)) s = s.Substring(1);
            if (s.Length == 3) s = new string(new[] { s[0], s[0], s[1], s[1], s[2], s[2] });
            if (s.Length != 6 && s.Length != 8) return null;
            if (!uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var raw))
                return null;
            if (s.Length == 6)
                return Color.FromRgb((byte)(raw >> 16), (byte)(raw >> 8), (byte)raw);
            return Color.FromArgb((byte)(raw >> 24), (byte)(raw >> 16), (byte)(raw >> 8), (byte)raw);
        }

        /// <summary>按 #AARRGGBB 输出。</summary>
        public static string Format(Color c)
            => "#" + c.A.ToString("X2") + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");

        /// <summary>解析失败时用 fallback，绝不抛异常。</summary>
        public static Color ParseOrDefault(string? text, Color fallback)
            => TryParse(text) ?? fallback;
    }
}
