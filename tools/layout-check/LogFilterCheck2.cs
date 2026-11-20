using System;
using System.Linq;
using System.Reflection;
using SoulLink;

public static class LogFilterCheck2
{
    public static int Main()
    {
        var type = typeof(SoulLink.Controls.RdpConsole);
        var m = type.GetMethod("IsImportant", BindingFlags.NonPublic | BindingFlags.Static);
        if (m == null) { Console.WriteLine("FAIL 找不到 IsImportant"); return 2; }
        bool Imp(string s) => (bool)m.Invoke(null, new object[] { s })!;

        int fails = 0;
        void T(string s) { bool ok = Imp(s); Console.WriteLine((ok ? "ok   " : "FAIL ") + "落盘: " + s); if (!ok) fails++; }

        T("收到目标会话热键事件 · UI线程=True connected=True input=True captured=False");
        T("丢弃热键事件 · 原因=RDP 画面没有键盘焦点 · visible=True armed=True connected=True axFocus=False");
        T("忽略目标会话热键回声（本机刚处理过）");
        T("执行热键切换 · guard=0 UI线程=True");
        T("热键触发: target=FreeLook IsConnected=True _captured=False");
        T("热键切换结果 ok=True message=已进入自由视角 captured=True mode=FreeLook");
        T("事件管道收到热键 · 会话 6 · toggle · 订阅者=1");
        T("手动代理已挂接 · 会话 6 · 屏幕 1920x1080");
        T("进入自由视角（手动切换）中心=1337,768 焦点=True");

        Console.WriteLine();
        Console.WriteLine(fails == 0 ? "RESULT=OK 全部落盘" : "RESULT=FAIL " + fails + " 条被丢弃");
        return fails;
    }
}
