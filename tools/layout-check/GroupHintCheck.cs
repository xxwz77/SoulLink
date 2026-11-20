using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SoulLink;
using SoulLink.Models;
using SoulLink.Services;
using SoulLink.Views;

public static class GroupHintCheck
{
    static int fails;
    static void Check(bool ok, string s) { Console.WriteLine((ok ? "ok   " : "FAIL ") + s); if (!ok) fails++; }

    [STAThread]
    public static int Main()
    {
        var app = new Application();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/SoulLink;component/Theme/Styles.xaml", UriKind.Absolute) });
        var cfg = ConfigStore.Load();
        typeof(App).GetProperty("Config", BindingFlags.Public | BindingFlags.Static)!.SetValue(null, cfg);
        AccountStateCenter.Start(cfg, 1000);
        Thread.Sleep(2500);

        // ① 未绑定账号（lummy）作为目标用户 → 必须给出可读原因
        var acc = cfg.Accounts.First(a => a.UserName == "lummy");
        var flow = new AutomationFlow { AccountId = acc.Id };
        var node = new FlowNode { Id = "g", Kind = FlowNodeKind.Condition, Type = FlowNodeType.CondScriptGroupState };
        foreach (var d in FlowNodeCatalog.Get(FlowNodeType.CondScriptGroupState)!.Params) node.Set(d.Key, d.Default);
        flow.Nodes.Add(node);
        var win = new FlowEditorWindow(flow, acc, cfg);
        var show = typeof(FlowEditorWindow).GetMethod("ShowPropPanel", BindingFlags.Instance | BindingFlags.NonPublic)!;
        node.Set("user", "lummy");
        show.Invoke(win, new object[] { node });
        var panel = (StackPanel)typeof(FlowEditorWindow).GetField("PropPanel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(win)!;
        Console.WriteLine("---- texts for lummy ----");
        foreach (var t in Descendants(panel).OfType<TextBlock>().Select(t => t.Text ?? "")) if (t.Length > 0) Console.WriteLine("  [" + t + "]");
        Console.WriteLine("node.user=" + node.Get("user") + " data=" + BgiDataCenter.Get(node.Get("user")).Error + " exe='" + BgiDataCenter.Get(node.Get("user")).ExePath + "'");
        var hints = Descendants(panel).OfType<TextBlock>().Select(t => t.Text ?? "").Where(t => t.Contains("未能从数据中心")).ToList();
        Check(hints.Count == 1, "未绑定 BGI 路径时给出明确原因");
        Check(hints.Count == 1 && hints[0].Contains("BetterGI 路径"), "原因里指出是路径未绑定");
        if (hints.Count > 0) Console.WriteLine("     hint=" + hints[0]);

        // ② 绑定账号（A2）→ 不显示提示，且选项齐全
        node.Set("user", "A2");
        show.Invoke(win, new object[] { node });
        panel = (StackPanel)typeof(FlowEditorWindow).GetField("PropPanel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(win)!;
        var hints2 = Descendants(panel).OfType<TextBlock>().Select(t => t.Text ?? "").Where(t => t.Contains("未能从数据中心")).ToList();
        Check(hints2.Count == 0, "已绑定账号时不显示告警");
        var groupCombo = Descendants(panel).OfType<ComboBox>()
            .FirstOrDefault(c => c.Items.OfType<ComboBoxItem>().Any(i => (i.Content as string) == "（未选择）"));
        Check(groupCombo != null && groupCombo.Items.Count >= 2, "已绑定账号时列出配置组（" + (groupCombo?.Items.Count ?? 0) + " 项）");

        // ③ 新建节点默认把「目标用户」写进参数（界面显示与实际生效一致）
        var node2 = new FlowNode { Id = "h", Kind = FlowNodeKind.Condition, Type = FlowNodeType.CondScriptGroupState };
        foreach (var d in FlowNodeCatalog.Get(FlowNodeType.CondScriptGroupState)!.Params) node2.Set(d.Key, d.Default);
        flow.Nodes.Add(node2);
        show.Invoke(win, new object[] { node2 });
        Check(node2.Get("user") == "A2", "默认选中的目标用户写回节点参数（user=" + node2.Get("user") + "）");

        win.Close();
        AccountStateCenter.Stop();
        Console.WriteLine(fails == 0 ? "ALL PASSED" : "FAILURES=" + fails);
        return fails;
    }

    static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }
}