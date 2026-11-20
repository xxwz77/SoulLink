using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SoulLink.Controls;
using SoulLink.Models;
using SoulLink.Views;

public static class FlowGuideCheck
{
    private static int _failures;
    private static void Check(bool ok, string text)
    { Console.WriteLine((ok ? "ok   " : "FAIL ") + text); if (!ok) _failures++; }

    [STAThread]
    public static int Main()
    {
        var app = new Application();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/SoulLink;component/Theme/Styles.xaml", UriKind.Absolute) });
        var cfg = new AppConfig();
        var account = new Account { Id = "guide-test", UserName = "tester" };
        cfg.Accounts.Add(account);
        var flow = new AutomationFlow { AccountId = account.Id, Name = "引导隔离测试" };
        flow.Nodes.Add(new FlowNode { Id = "c", Kind = FlowNodeKind.Condition, Type = FlowNodeType.CondTimeRange });
        var editor = new FlowEditorWindow(flow, account, cfg);
        editor.Closed += (_, _) => typeof(FlowEditorWindow).GetMethod("EndGuide", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, null);
        editor.ContentRendered += async (_, _) => await Verify(editor, flow, cfg);
        editor.Show(); app.Run(editor);
        Console.WriteLine(_failures == 0 ? "ALL PASSED" : "FAILURES=" + _failures);
        return _failures;
    }

    private static async Task Verify(FlowEditorWindow editor, AutomationFlow flow, AppConfig cfg)
    {
        try
        {
            await Frames();
            var host = (Canvas)typeof(FlowEditorWindow).GetField("GuideLayer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
            var guide = (FlowGuideOverlay)typeof(FlowEditorWindow).GetField("_guide", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
            Check(guide != null && guide.CurrentIndex == 0, "真实编辑器首次显示六步引导");
            Check(host.Visibility == Visibility.Visible, "透明宿主位于真实编辑器视觉树中");
            int nodeCount = flow.Nodes.Count, edgeCount = flow.Edges.Count, configCount = cfg.Automations.Count;
            foreach (double width in new[] { 1180d, 1080d })
            {
                editor.Width = width; editor.Height = width < 1180 ? 740 : 820;
                editor.UpdateLayout(); await Frames(); guide.SyncHostSize();
            for (int i = 0; i < guide.StepCount; i++)
            {
                guide.ShowFrame(i, 3.5); editor.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Render);
                var panel = Descendants(guide).OfType<Border>().FirstOrDefault(b => Equals(b.Tag, "GuidePanel"));
                Check(panel != null && panel.IsVisible && panel.ActualWidth > 300 && panel.ActualHeight > 200,
                    "第 " + (i + 1) + " 步说明卡实际可见");
                Check(panel != null && Inside(panel, guide), "第 " + (i + 1) + " 步说明卡未越界");
                var nodes = Descendants(guide).OfType<Border>().Where(b => Equals(b.Tag, "DemoNode")).ToList();
                Check(nodes.Count > 0 && nodes.All(n => Inside(n, guide)), "第 " + (i + 1) + " 步示例全部在窗口内");
                Check(panel != null && nodes.All(n => !Overlap(panel, n)), "第 " + (i + 1) + " 步说明卡不遮挡示例");
                Check(Descendants(guide).OfType<StackPanel>().Any(p => Equals(p.Tag, "GuideNavigation")),
                    "第 " + (i + 1) + " 步存在右下角导航");
                if(width == 1180)
                {
                    Directory.CreateDirectory("artifacts/guide-demo/main");
                    var bitmap = new RenderTargetBitmap((int)editor.ActualWidth, (int)editor.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(editor);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var stream = System.IO.File.Create("artifacts/guide-demo/main/step-" + (i + 1) + ".png")) encoder.Save(stream);
                    Check(bitmap.PixelWidth > 1000 && bitmap.PixelHeight > 700, "第 " + (i + 1) + " 步生成真实编辑器截图");
                }
            }
            }
            guide.Previous(); Check(guide.CurrentIndex == 4, "上一步可以回看");
            guide.Next(); Check(guide.CurrentIndex == 5, "下一步可以前进");
            typeof(FlowEditorWindow).GetMethod("CloseGuide", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, null);
            Check(host.Visibility == Visibility.Collapsed && host.Children.Count == 0, "关闭后清空遮罩并释放控件");
            Check(flow.Nodes.Count == nodeCount && flow.Edges.Count == edgeCount && cfg.Automations.Count == configCount,
                "引导没有添加节点、连线或保存流程");
        }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex); _failures++; }
        finally { editor.Close(); }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static Rect Bounds(FrameworkElement e, FrameworkElement root)
        => e.TransformToVisual(root).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight));
    private static bool Inside(FrameworkElement e, FrameworkElement root)
    {
        var r = Bounds(e, root);
        return r.X >= -1 && r.Y >= -1 && r.Right <= root.ActualWidth + 1 && r.Bottom <= root.ActualHeight + 1;
    }
    private static bool Overlap(FrameworkElement a, FrameworkElement b)
    {
        var root = (FrameworkElement)a;
        while (VisualTreeHelper.GetParent(root) is DependencyObject parent && parent is FrameworkElement parentElement) root = parentElement;
        var x = Bounds(a, root); var y = Bounds(b, root); x.Intersect(y);
        return !x.IsEmpty && x.Width > 2 && x.Height > 2;
    }
    private static async Task Frames()
    { for (int i = 0; i < 5; i++) { await Task.Delay(40); await Dispatcher.Yield(DispatcherPriority.Render); } }
}
