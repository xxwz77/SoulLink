using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SoulLink.Models;
using SoulLink.Services;
using SoulLink.Views;

public static class Program
{
    private static readonly string Dir="artifacts/dream-ui-check";
    private static Application app=null!;
    private static int errors;
    [STAThread] public static int Main()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Path.Combine(Dir,"final-check.log"),"FINAL CHECK\n");
        app=new Application{ShutdownMode=ShutdownMode.OnMainWindowClose};
        app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/SoulLink;component/Theme/Styles.xaml")});
        SoulLink.App.Config.Settings.ThemeMode=2;ThemeManager.Apply();
        var main=new MainWindow{Width=1240,Height=820};app.MainWindow=main;
        main.Loaded+=async(_,_)=>await Run(main);
        app.Run(main);return errors;
    }
    private static async Task Run(MainWindow main)
    {
        try
        {
            main.Activate();await Frames();Shot(main,"main-dark");
            SoulLink.App.Config.Settings.ThemeMode=1;ThemeManager.Apply();await Frames();Shot(main,"main-light");
            var navToggle=Field<Button>(main,"BtnHamburger");
            bool collapsed=(bool)typeof(MainWindow).GetField("_navCollapsed",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(main)!;
            if(collapsed)navToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Task.Delay(800);await Frames();
            var nav=Field<Border>(main,"NavRail");
            Log("NAV background="+nav.Background+" foreground="+nav.BorderBrush+" width="+nav.ActualWidth);
            Shot(nav,"navigation-rail-light");
            if(nav.ActualWidth<150)Fail("Navigation did not expand");
            navToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));await Task.Delay(500);await Frames();
            if(nav.ActualWidth>65)Fail("Navigation did not collapse");
            await Dialog(new InputDialogWindow("连接名称","输入框、边框与文字跟随亮色主题。","云端 · A2"),"input-light");
            await Dialog(new AppDialog("连接已准备好","确认框、按钮、圆角和背景采用同一主题。",true),"dialog-light");
            await ComboMenu(main);
            SoulLink.App.Config.Settings.ThemeMode=2;ThemeManager.Apply();await Frames();
            await Dialog(new AppDialog("已切回暗色","主题反复切换后，文字与背景恢复正确。"),"dialog-restored");
            var cfg=new AppConfig();cfg.Settings.ShowFlowGuide=false;
            var account=new Account{Id="visual",UserName="tester"};cfg.Accounts.Add(account);
            var editor=new FlowEditorWindow(new AutomationFlow{AccountId=account.Id,Name="主题输入检查"},account,cfg,true){Owner=main,Width=1180,Height=820};
            editor.Show();editor.Activate();await Frames();Shot(editor,"editor-content-light");
            var input=Field<TextBox>(editor,"NameBox");input.Focus();input.SelectAll();await Frames();
            if(!input.IsKeyboardFocusWithin)Fail("Editor name input did not focus");
            editor.Close();main.Hide();await Frames();main.Close();
        }
        catch(Exception ex){Fail(ex.ToString());main.Close();}
    }
    private static async Task ComboMenu(Window owner)
    {
        var combo=new ComboBox{Width=240,Height=40,ItemsSource=new[]{"月莓星轨","云端棱镜","跟随系统"},SelectedIndex=1};
        var menu=new ContextMenu();menu.Items.Add(new MenuItem{Header="编辑动作",IsChecked=true});menu.Items.Add(new MenuItem{Header="复制连接命令"});
        var button=new Button{Content="右键菜单",Width=180,Height=40,Style=(Style)app.FindResource("GhostButton"),ContextMenu=menu};
        var host=new Window{Owner=owner,Width=620,Height=260,Title="菜单验收",WindowStartupLocation=WindowStartupLocation.CenterOwner,Content=new StackPanel{Margin=new Thickness(30),Children={new StackPanel{Orientation=Orientation.Horizontal,Children={combo,button}}}}};
        host.Show();host.Activate();await Frames();combo.Focus();await Frames();
        combo.ApplyTemplate();var toggle=combo.Template.FindName("Toggle",combo) as ToggleButton;
        if(toggle==null)Fail("ComboBox template toggle missing");else toggle.IsChecked=true;
        await Frames();Log("COMBO="+combo.IsDropDownOpen);
        if(!combo.IsDropDownOpen)Fail("ComboBox did not open from its toggle");
        Screen("controls-popup-light");combo.IsDropDownOpen=false;await Frames();
        host.Activate();button.Focus();menu.PlacementTarget=button;menu.Placement=PlacementMode.Bottom;menu.IsOpen=true;await Frames();
        Log("MENU="+menu.IsOpen);if(!menu.IsOpen)Fail("Context menu did not open");
        Screen("context-menu-light");menu.IsOpen=false;host.Close();await Frames();
    }
    private static async Task Dialog(Window window,string name)
    {window.WindowStartupLocation=WindowStartupLocation.CenterScreen;window.Show();await Frames();Shot(window,name);window.Close();await Frames();}
    private static T Field<T>(object owner,string name) where T:class => (T)owner.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(owner)!;
    private static async Task Frames(){for(int i=0;i<6;i++){await Task.Delay(50);await Dispatcher.Yield(DispatcherPriority.Render);}}
    private static void Shot(FrameworkElement element,string name)
    {
        element.UpdateLayout();double w=element.ActualWidth,h=element.ActualHeight;
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(w),(int)Math.Ceiling(h),96,96,PixelFormats.Pbgra32);bitmap.Render(element);Save(name,bitmap);
    }
    private static void Screen(string name)
    {
        var bounds=System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        using var bitmap=new System.Drawing.Bitmap(bounds.Width,bounds.Height);
        using(var graphics=System.Drawing.Graphics.FromImage(bitmap))graphics.CopyFromScreen(bounds.Location,System.Drawing.Point.Empty,bounds.Size);
        var source=System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(bitmap.GetHbitmap(),IntPtr.Zero,Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions());Save(name,source);
    }
    private static void Save(string name,BitmapSource bitmap){var e=new PngBitmapEncoder();e.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(Dir,name+".png"));e.Save(file);Log("SHOT "+name);}
    private static void Fail(string text){errors++;Log("FAIL "+text);}
    private static void Log(string text){Console.WriteLine(text);File.AppendAllText(Path.Combine(Dir,"final-check.log"),text+"\n");}
}