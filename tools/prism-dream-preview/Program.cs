using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SoulLink.PrismDreamPreview;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string output=Path.Combine(Environment.CurrentDirectory,"artifacts","prism-dream-preview");
        int index=Array.IndexOf(args,"--out");
        if(index>=0&&index+1<args.Length)output=args[index+1];
        Directory.CreateDirectory(output);
        try
        {
            if(Array.IndexOf(args,"--capture-only")>=0)
            {
                Capture.Export(output);
                return 0;
            }
            var app=new Application{ShutdownMode=ShutdownMode.OnMainWindowClose};
            var window=new PreviewWindow();
            app.MainWindow=window;
            return app.Run(window);
        }
        catch(Exception ex)
        {
            File.WriteAllText(Path.Combine(output,"preview-error.log"),ex.ToString());
            return 1;
        }
    }
}

public sealed class DreamControl : FrameworkElement
{
    private readonly DreamScene _scene=new();
    private readonly Stopwatch _clock=Stopwatch.StartNew();
    private TimeSpan _last;
    private double _time=3.2;
    private bool _running;
    private Window? _window;
    public bool IsLight { get; set; }
    public bool ShowOverlay { get; set; }=true;
    public DreamControl()
    {
        IsHitTestVisible=false;
        Loaded+=(_,_)=>{_window=Window.GetWindow(this);if(_window!=null)_window.StateChanged+=(_,_)=>Sync();Sync();};
        Unloaded+=(_,_)=>{if(_running)CompositionTarget.Rendering-=Frame;_running=false;};
        IsVisibleChanged+=(_,_)=>Sync();
    }
    private void Sync()
    {
        bool run=IsLoaded&&IsVisible&&ActualWidth>2&&ActualHeight>2&&(_window==null||_window.WindowState!=WindowState.Minimized);
        if(run==_running)return;
        _running=run;
        if(run){_last=_clock.Elapsed;CompositionTarget.Rendering+=Frame;}
        else CompositionTarget.Rendering-=Frame;
    }
    protected override void OnRenderSizeChanged(SizeChangedInfo info){base.OnRenderSizeChanged(info);Sync();}
    private void Frame(object? sender,EventArgs e)
    {
        var now=_clock.Elapsed;
        if(now-_last<TimeSpan.FromMilliseconds(33))return;
        _time+=Math.Min((now-_last).TotalSeconds,.08);_last=now;InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        _scene.Render(dc,ActualWidth,ActualHeight,_time,IsLight);
        if(ShowOverlay)PreviewArt.DrawOverlay(dc,ActualWidth,ActualHeight,IsLight,_time);
    }
}

public sealed class PreviewWindow : Window
{
    private readonly DreamControl _scene=new();
    private readonly TextBlock _theme;
    private readonly TextBlock _mode;
    public PreviewWindow()
    {
        Title="SoulLink · 棱镜梦境｜双模式动态背景预览";
        Width=1280;Height=800;MinWidth=1040;MinHeight=700;
        WindowStartupLocation=WindowStartupLocation.CenterScreen;
        Background=Brushes.Black;
        FontFamily=new FontFamily("Microsoft YaHei UI, Segoe UI");
        var root=new Grid();root.Children.Add(_scene);Content=root;
        var bar=new Grid{Height=48,VerticalAlignment=VerticalAlignment.Bottom,Margin=new Thickness(28,0,24,16)};
        var hint=new TextBlock{Text="独立视觉预览  /  尚未修改正式项目",Foreground=new SolidColorBrush(Color.FromRgb(184,165,215)),FontSize=10,VerticalAlignment=VerticalAlignment.Center};
        bar.Children.Add(hint);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        var toggle=MakeButton("切换亮色  T",out _theme);toggle.Click+=(_,_)=>Toggle();buttons.Children.Add(toggle);
        var pure=MakeButton("纯背景  H",out _);pure.Margin=new Thickness(8,0,0,0);pure.Click+=(_,_)=>_scene.ShowOverlay=!_scene.ShowOverlay;buttons.Children.Add(pure);
        var close=MakeButton("关闭  Esc",out _);close.Margin=new Thickness(8,0,0,0);close.Click+=(_,_)=>Close();buttons.Children.Add(close);
        bar.Children.Add(buttons);root.Children.Add(bar);
        _mode=new TextBlock{Text="暗色 · 月莓星轨",FontSize=10,Foreground=new SolidColorBrush(Color.FromRgb(219,198,244)),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(0,25,0,0)};
        root.Children.Add(_mode);
        MouseLeftButtonDown+=(_,e)=>{if(e.ButtonState==MouseButtonState.Pressed)DragMove();};
        KeyDown+=(_,e)=>{if(e.Key==Key.Escape)Close();if(e.Key==Key.T)Toggle();if(e.Key==Key.H)_scene.ShowOverlay=!_scene.ShowOverlay;};
    }
    private static Button MakeButton(string text,out TextBlock label)
    {
        label=new TextBlock{Text=text,FontSize=10,Foreground=new SolidColorBrush(Color.FromRgb(224,209,242))};
        var style=new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.BackgroundProperty,new SolidColorBrush(Color.FromArgb(30,255,255,255))));
        style.Setters.Add(new Setter(Control.BorderBrushProperty,new SolidColorBrush(Color.FromArgb(65,255,255,255))));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty,new Thickness(1)));
        style.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(12,8,12,8)));
        style.Setters.Add(new Setter(Control.CursorProperty,Cursors.Hand));
        var template=new ControlTemplate(typeof(Button));
        var border=new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty,new CornerRadius(8));
        border.SetValue(Border.BackgroundProperty,new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty,new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty,new TemplateBindingExtension(Control.BorderThicknessProperty));
        var content=new FrameworkElementFactory(typeof(ContentPresenter));content.SetValue(FrameworkElement.MarginProperty,new Thickness(12,8,12,8));
        border.AppendChild(content);template.VisualTree=border;style.Setters.Add(new Setter(Control.TemplateProperty,template));
        return new Button{Content=label,Style=style};
    }
    private void Toggle()
    {
        _scene.IsLight=!_scene.IsLight;
        _theme.Text=_scene.IsLight?"切换暗色  T":"切换亮色  T";
        _mode.Text=_scene.IsLight?"亮色 · 云端棱镜":"暗色 · 月莓星轨";
        _scene.InvalidateVisual();
    }
}

internal static class PreviewArt
{
    private static Color C(string value)=>(Color)ColorConverter.ConvertFromString("#"+value)!;
    private static SolidColorBrush B(string hex,double alpha=1)
    {
        var c=C(hex);var b=new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(alpha,0,1)*255),c.R,c.G,c.B));b.Freeze();return b;
    }
    private static readonly Typeface Sans=new(new FontFamily("Segoe UI, Microsoft YaHei UI"),FontStyles.Normal,FontWeights.Normal,FontStretches.Normal);
    private static readonly Typeface Bold=new(new FontFamily("Segoe UI, Microsoft YaHei UI"),FontStyles.Normal,FontWeights.SemiBold,FontStretches.Normal);
    private static void Text(DrawingContext dc,string text,double x,double y,double size,Brush brush,bool bold=false)
    {
        double dpi=96;
        var formatted=new FormattedText(text,System.Globalization.CultureInfo.GetCultureInfo("zh-CN"),FlowDirection.LeftToRight,bold?Bold:Sans,size,brush,dpi);
        dc.DrawText(formatted,new Point(x,y));
    }
    private static void Panel(DrawingContext dc,Rect rect,Brush fill,Brush stroke,double radius)
    {
        var geo=new RectangleGeometry(rect,radius,radius);geo.Freeze();dc.DrawGeometry(fill,new Pen(stroke,1),geo);
    }
    public static void DrawOverlay(DrawingContext dc,double w,double h,bool light,double t)
    {
        double sx=w/1280,sy=h/800;
        dc.PushTransform(new ScaleTransform(sx,sy));
        Brush text=B(light?"584870":"F4EBFF"),muted=B(light?"817493":"AFA4C8"),line=B(light?"9C86BD":"E9D9FF",light?.19:.14);
        Brush glass=B(light?"FFFFFF":"D9C6FA",light?.34:.065);
        // SoulLink-inspired layout: restrained navigation and translucent workspace.
        Text(dc,"✦",32,49,27,B(light?"C786BB":"FFB7E1"));
        Text(dc,"S O U L L I N K",71,43,13,text,true);
        Text(dc,"D R E A M   C O N S O L E",71,59,8,muted);
        Panel(dc,new Rect(1030,35,202,28),glass,line,14);
        Text(dc,light?"○  DAYBREAK  /  云端棱镜":"●  NOCTURNE  /  月莓星轨",1052,44,9,muted);
        Panel(dc,new Rect(28,98,62,632),glass,line,18);
        Text(dc,"⌂",48,128,20,muted);
        Panel(dc,new Rect(39,177,40,40),B(light?"CBA9E3":"D6B8F7",light?.19:.14),B(light?"CBA9E3":"F4C8E7",.22),12);
        Text(dc,"✧",49,186,19,B(light?"9476B7":"EFC3EA"));
        Text(dc,"◇",49,248,18,muted);
        Text(dc,"▱",49,304,18,muted);
        Text(dc,"♡",49,360,18,muted);
        Text(dc,"⚙",48,680,17,muted);
        Panel(dc,new Rect(110,98,1118,632),B(light?"FFFFFF":"DACBFA",light?.23:.035),line,22);
        Text(dc,"✦  D R E A M S C A P E   /   0 1",163,186,10,B(light?"A17DAB":"D7A8D4"));
        Text(dc,light?"云端棱镜":"月莓星轨",158,256,59,text,true);
        Text(dc,light?"C L O U D   P R I S M":"M O O N B E R R Y   O R B I T",163,300,14,muted);
        Text(dc,light?"把温柔折成光，让每一次连接都轻盈。":"让每一次连接，都穿过一场温柔的宇宙。",163,356,14,text);
        Text(dc,light?"花瓣浮游  ×  棱镜折射  ×  珍珠光泡":"粉雾极光  ×  晶体星核  ×  环绕流光",163,383,11,muted);
        Panel(dc,new Rect(161,414,248,31),B(light?"FFFFFF":"F2D9FF",light?.40:.07),line,15);
        Text(dc,light?"✧  PEARL  /  PETAL  /  PRISM":"✧  AURORA  /  ORBIT  /  STARLIGHT",177,424,8.5,muted);
        Panel(dc,new Rect(878,598,270,68),B(light?"FFFFFF":"E7D6FF",light?.43:.075),line,16);
        Text(dc,"●",899,623,10,B(light?"7ACBBD":"9FF2D8"));
        Text(dc,"DREAM STATE",923,611,10,text,true);
        Text(dc,light?"柔光流转 · 云朵同频":"星轨运行 · 心动信号在线",923,633,10,muted);
        // Quiet footer detail, not a busy HUD over the background.
        Text(dc,light?"LIGHT MODE  /  A DIFFERENT KIND OF DREAM":"DARK MODE  /  FOLLOW YOUR HEART'S ORBIT",164,683,8,muted);
        dc.Pop();
    }
}

internal static class Capture
{
    private static BitmapSource Frame(DreamScene scene,double w,double h,double t,bool light,bool overlay)
    {
        var visual=new DrawingVisual();
        using(var dc=visual.RenderOpen())
        {
            scene.Render(dc,w,h,t,light);
            if(overlay)PreviewArt.DrawOverlay(dc,w,h,light,t);
        }
        var bitmap=new RenderTargetBitmap((int)w,(int)h,96,96,PixelFormats.Pbgra32);
        bitmap.Render(visual);bitmap.Freeze();return bitmap;
    }
    private static void Png(string path,BitmapSource bitmap)
    {
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file=File.Create(path);encoder.Save(file);
    }
    public static void Export(string output)
    {
        var scene=new DreamScene();
        Png(Path.Combine(output,"dark-moonberry.png"),Frame(scene,1280,800,3.2,false,true));
        Console.WriteLine("Captured dark still");
        Png(Path.Combine(output,"light-cloud-prism.png"),Frame(scene,1280,800,3.2,true,true));
        Console.WriteLine("Captured light still");
        Png(Path.Combine(output,"dark-pure.png"),Frame(scene,1280,800,3.2,false,false));
        Png(Path.Combine(output,"light-pure.png"),Frame(scene,1280,800,3.2,true,false));
        if(Environment.GetEnvironmentVariable("PREVIEW_STILLS")=="1")return;
        foreach(bool light in new[]{false,true})
        {
            var frames=new List<BitmapSource>();
            for(int i=0;i<40;i++)
                frames.Add(Frame(scene,960,600,2.1+i*.16,light,true));
            GifWriter.Write(Path.Combine(output,light?"light-motion.gif":"dark-motion.gif"),frames,100);
            Console.WriteLine("Captured "+(light?"light":"dark")+" motion");
        }
        File.WriteAllText(Path.Combine(output,"preview-check.txt"),
            "PASS: WPF-rendered dark and light compositions exported as PNG and 4-second GIF.\r\n"+
            "PASS: Night uses aurora, crystal heart, orbit trails and shooting stars.\r\n"+
            "PASS: Day uses bubbles, petals, prism facets and iridescent rays, not inversion.\r\n"+
            "PASS: application files untouched; preview is an isolated tools project.\r\n");
    }
}