using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SoulLink.Views;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try
        {
            var app = new SoulLink.App();
            app.InitializeComponent();
            SoulLink.Services.ThemeManager.Apply();
            var window = new MainWindow();
            Console.WriteLine("MainWindow constructed");
            window.SourceInitialized += (_, _) => Console.WriteLine("MainWindow source initialized");
            window.Loaded += (_, _) => Console.WriteLine("MainWindow loaded");
            window.Show();
            window.UpdateLayout();
            app.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            var glyph = window.FindName("NavGlyphLaunch") as SoulLink.Controls.NavigationOrnament;
            Console.WriteLine("Glyph: " + (glyph == null ? "missing" : "found; selected=" + glyph.IsSelected));
            string output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "design", "navigation-icons", "actual-main-window.png"));
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            using var stream = File.Create(output);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Save(stream);
            Console.WriteLine("PASS: actual main window rendered without starting the application automation loop.");
            window.Close();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }
}
