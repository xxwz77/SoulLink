using System;
using System.Collections.ObjectModel;
using SoulLink.Models;

namespace SoulLink.Services
{
    public enum LogLevel { Info, Ok, Warn, Error, Run }

    public class LogEntry
    {
        public DateTime Time { get; set; } = DateTime.Now;
        public LogLevel Level { get; set; } = LogLevel.Info;
        public string Text { get; set; } = "";
        public string Detail { get; set; } = "";

        public string TimeText => Time.ToString("HH:mm:ss");
        public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

        [System.Text.Json.Serialization.JsonIgnore]
        public System.Windows.Media.Color DotColor
        {
            get
            {
                switch (Level)
                {
                    case LogLevel.Ok: return System.Windows.Media.Color.FromRgb(0x41, 0xF0, 0xC0);
                    case LogLevel.Warn: return System.Windows.Media.Color.FromRgb(0xFF, 0xC4, 0x6B);
                    case LogLevel.Error: return System.Windows.Media.Color.FromRgb(0xFF, 0x6B, 0x8B);
                    case LogLevel.Run: return System.Windows.Media.Color.FromRgb(0x4F, 0xE3, 0xFF);
                    default: return System.Windows.Media.Color.FromRgb(0x8B, 0x9C, 0xC8);
                }
            }
        }

        /// <summary>
        /// ★ 2.17.51（.NET 11 迁移）：原来每次求值都 new 一个画刷。
        ///   WPF 绑定会对同一属性反复求值，日志列表越长重复分配越多。
        ///   颜色是常量，改成冻结的静态实例，像素结果完全一致。
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public System.Windows.Media.Brush TextColor
        {
            get
            {
                switch (Level)
                {
                    case LogLevel.Ok: return OkBrush;
                    case LogLevel.Warn: return WarnBrush;
                    case LogLevel.Error: return ErrorBrush;
                    case LogLevel.Run: return RunBrush;
                    default: return DefaultBrush;
                }
            }
        }

        private static readonly System.Windows.Media.SolidColorBrush OkBrush = MakeFrozen(0x9D, 0xFF, 0xE0);
        private static readonly System.Windows.Media.SolidColorBrush WarnBrush = MakeFrozen(0xFF, 0xDC, 0x9E);
        private static readonly System.Windows.Media.SolidColorBrush ErrorBrush = MakeFrozen(0xFF, 0xB4, 0xC4);
        private static readonly System.Windows.Media.SolidColorBrush RunBrush = MakeFrozen(0xBF, 0xE9, 0xFF);
        private static readonly System.Windows.Media.SolidColorBrush DefaultBrush = MakeFrozen(0xC8, 0xD6, 0xF0);

        private static System.Windows.Media.SolidColorBrush MakeFrozen(byte r, byte g, byte b)
        {
            var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public System.Windows.Visibility DetailVisibility =>
            HasDetail ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }

    /// <summary>全局日志总线，UI 和托盘都订阅它。</summary>
    public static class LogBus
    {
        public static ObservableCollection<LogEntry> Entries { get; } = new ObservableCollection<LogEntry>();
        private const int MaxEntries = 600;

        public static event Action<LogEntry>? Published;

        public static void Write(LogLevel level, string text, string detail = "")
        {
            var e = new LogEntry { Level = level, Text = text, Detail = detail ?? "" };
            App.Current?.Dispatcher?.InvokeAsync(() =>
            {
                Entries.Insert(0, e);
                while (Entries.Count > MaxEntries) Entries.RemoveAt(Entries.Count - 1);
                Published?.Invoke(e);
            });
        }

        public static void Info(string t, string d = "") => Write(LogLevel.Info, t, d);
        public static void Ok(string t, string d = "") => Write(LogLevel.Ok, t, d);
        public static void Warn(string t, string d = "") => Write(LogLevel.Warn, t, d);
        public static void Error(string t, string d = "") => Write(LogLevel.Error, t, d);
    }
}