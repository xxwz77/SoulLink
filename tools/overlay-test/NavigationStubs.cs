using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using SoulLink.Models;

namespace SoulLink
{
    public partial class App : Application
    {
        public static AppConfig Config { get; private set; } = new AppConfig();
        public static string StartupPage => "";
        public static void Breadcrumb(string text)
        {
            if (text.StartsWith("侧栏")) Console.WriteLine("TEST EVENT " + text);
        }
        public static void AppendErrorLog(string source, Exception? ex)
            => Console.Error.WriteLine(source + ": " + ex);
    }
}

namespace SoulLink.Core { public static class JsonDefaults { } }

namespace SoulLink.Models
{
    public enum LogLevel { Info, Error }
}

namespace SoulLink.Services
{
    public static class LogBus
    {
        public static void Info(string a, string b = "") { }
        public static void Ok(string a) { }
        public static void Error(string a, string b = "") { }
    }
    public static class PointerAcceleration { public static void ForceRestore() { } }
    public static class SessionService
    {
        public static int CurrentSessionId => 0;
        public static string CurrentUser => "UI-TEST";
        public static void EnablePrivileges() { }
    }
    public static class SystemLauncher
    {
        public static bool IsElevated => false;
        public static void EnablePrivileges() { }
    }
    public static class SecretBox
    {
        public static string Protect(string value) => "";
        public static string Unprotect(string value) => "";
    }
    public sealed class AutomationScheduler : IDisposable { public void Dispose() { } }
}

namespace SoulLink.Controls
{
    public static class RdpConsole { public static bool VerboseLog => false; }
    public sealed class GlowDot : Shape
    {
        public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
            nameof(Color), typeof(Color), typeof(GlowDot), new FrameworkPropertyMetadata(Colors.Green));
        public Color Color { get => (Color)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
        protected override Geometry DefiningGeometry => new EllipseGeometry(new Rect(RenderSize));
    }
    public sealed class AuroraBackdrop : Control { }
}

namespace SoulLink.Views
{
    public class PlaceholderPage : UserControl
    {
        public event Action<bool, string>? StatusChanged;
        public MainWindow? MainWindowRef { get; set; }
        public PlaceholderPage() { Content = new Border { Background = Brushes.Transparent }; }
        public void Activate() { }
        public void RefreshSessions() { }
        public void ReloadActions() { }
        public void Reload() { }
        public void Refresh() { }
        public void DisposeConsoles() { }
        public void FocusSession(SoulLink.Models.SessionInfo session) { }
        public System.Threading.Tasks.Task AddSessionAsync(SoulLink.Models.SessionInfo session) => System.Threading.Tasks.Task.CompletedTask;
    }
    public sealed class LaunchPage : PlaceholderPage { }
    public sealed class AutomationPage : PlaceholderPage { }
    public sealed class ConsolePage : PlaceholderPage
    {
        public MainWindow? MainWindowRef { get; set; }
        public event Action? AddRequested;
    }
    public sealed class BgiUpdatePage : PlaceholderPage { }
    public sealed class LobbyPage : PlaceholderPage { }
    public sealed class ProcessPage : PlaceholderPage { }
    public sealed class CliPage : PlaceholderPage { }
    public sealed class SettingsPage : PlaceholderPage { }
    public sealed class ActionEditorWindow : Window
    {
        public ActionItem Result { get; set; } = new ActionItem();
        public ActionEditorWindow(object? item, object? config) { }
        public void PresetAccount(SoulLink.Models.SessionInfo session) { }
        public void PresetAccount(string userName) { }
    }
}
