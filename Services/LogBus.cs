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

        [System.Text.Json.Serialization.JsonIgnore]
        public System.Windows.Media.Brush TextColor
        {
            get
            {
                switch (Level)
                {
                    case LogLevel.Ok: return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9D, 0xFF, 0xE0));
                    case LogLevel.Warn: return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xDC, 0x9E));
                    case LogLevel.Error: return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xB4, 0xC4));
                    case LogLevel.Run: return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xBF, 0xE9, 0xFF));
                    default: return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC8, 0xD6, 0xF0));
                }
            }
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