using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace SoulLink.Controls
{
    /// <summary>
    /// 会自己解释「为什么点不动」的主按钮。
    ///
    /// ★ 用户实测反馈（3.1.10 原文）：
    ///   「创建用户按钮无法点击，不能创建用户」
    ///   「没有点击动作，不能点的时候没有变灰色。交互性太差。」
    ///
    /// 实测到的两个根因：
    ///   ① 表单没填完时 RemoteAdminPage 把 IsEnabled 置 False，而 WPF 的**禁用控件
    ///      完全不接收鼠标输入** —— 点下去既没有按下效果，也没有任何提示，
    ///      用户只能看到一枚（看上去还能点的）彩色按钮却毫无反应；
    ///   ② PrimaryButton 的禁用态原本只把渐变降到 0.35，看上去仍是彩色按钮。
    ///
    /// 本控件的做法：**从不把自己设成 IsEnabled=False**，而是用
    /// <see cref="IsBlocked"/> 表示「现在按不进去」：
    ///   · 外观：中性灰胶囊 + 次级色文字 + 箭头光标（HintPrimaryButton 模板的触发器切换）；
    ///   · 交互：点下去照样有完整的按下反馈，松手时把 <see cref="BlockedHint"/> 里的原因
    ///     交给 <see cref="BlockedFeedback"/>，而不是让点击静默消失。
    ///
    /// ★ 为什么重写 OnClick 而不是拦预览事件：
    ///   试过 AddHandler(PreviewMouseLeftButtonUp, handledEventsToo) + e.Handled=true ——
    ///   ButtonBase 的按下状态是在 OnMouseLeftButtonDown 里置的，正常在 OnMouseLeftButtonUp
    ///   里复位；把 preview 事件吞掉会让它**永远停在按下态**（按钮卡成凹陷、鼠标捕获不释放）。
    ///   OnClick 是 ButtonBase 在所有状态都复位之后才调用的，在这里拦截最安全，
    ///   而且鼠标、空格/回车、Automation 三种入口全部覆盖。
    /// </summary>
    public class HintButton : Button
    {
        /// <summary>true = 当前按不进去（外观置灰，点击转为提示）。与 IsEnabled 相互独立。</summary>
        public static readonly DependencyProperty IsBlockedProperty =
            DependencyProperty.Register(nameof(IsBlocked), typeof(bool), typeof(HintButton),
                new PropertyMetadata(false, OnStateChanged));

        /// <summary>被挡住时告诉用户的原因，例如「还差：Windows 用户名、密码」。</summary>
        public static readonly DependencyProperty BlockedHintProperty =
            DependencyProperty.Register(nameof(BlockedHint), typeof(string), typeof(HintButton),
                new PropertyMetadata(string.Empty, OnStateChanged));

        /// <summary>被挡住时点了按钮的回调（参数是 BlockedHint）。</summary>
        public static readonly DependencyProperty BlockedFeedbackProperty =
            DependencyProperty.Register(nameof(BlockedFeedback), typeof(Action<string>), typeof(HintButton),
                new PropertyMetadata(null));

        public bool IsBlocked
        {
            get => (bool)GetValue(IsBlockedProperty);
            set => SetValue(IsBlockedProperty, value);
        }

        public string BlockedHint
        {
            get => (string)GetValue(BlockedHintProperty);
            set => SetValue(BlockedHintProperty, value);
        }

        public Action<string>? BlockedFeedback
        {
            get => (Action<string>?)GetValue(BlockedFeedbackProperty);
            set => SetValue(BlockedFeedbackProperty, value);
        }

        public HintButton() => Sync();

        private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((HintButton)d).Sync();

        private string HintText => string.IsNullOrWhiteSpace(BlockedHint) ? "按钮当前不可用" : BlockedHint;

        /// <summary>当前是否真的按不进去：自身被挡住，或父级把整块禁用了。</summary>
        private bool Blocked => IsBlocked || !IsEnabled;

        private void Sync()
        {
            bool blocked = Blocked;
            Cursor = blocked ? Cursors.Arrow : Cursors.Hand;
            ToolTip = blocked ? HintText : null;
            try { AutomationProperties.SetHelpText(this, blocked ? HintText : string.Empty); } catch { }
        }

        /// <summary>
        /// 被挡住时**不**触发 Click（业务不会跑），改为回报原因；
        /// 此时 ButtonBase 已经把 IsPressed / 鼠标捕获全部复位，所以不会卡在按下态。
        /// </summary>
        protected override void OnClick()
        {
            if (Blocked)
            {
                try { BlockedFeedback?.Invoke(HintText); } catch { }
                return;
            }
            base.OnClick();
        }
    }
}
