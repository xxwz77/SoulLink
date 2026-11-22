using System;
using System.Windows;
using System.Windows.Input;
using SoulLink.Services;

namespace SoulLink.Views
{
    /// <summary>
    /// 「会话瘦身说明」弹窗：详细、清晰、不拥挤。
    ///
    /// ★ 为什么独立成一个页面而不是塞进 ProcessPage：
    ///   ProcessPage 是操作页（选会话 → 清理 → 看进程），信息密度高；
    ///   把「这是什么 / 关掉会怎样 / 不影响什么 / 怎么恢复」这些说明放进去会挤成一团。
    ///   单独弹窗用分区卡片 + 留白承载说明，用户需要时点开、看完关掉，互不干扰。
    ///
    /// ★ 两个页面统一（2026-11-22）：
    ///   本页**只读展示**三个开关的当前状态，不在这里直接改配置；
    ///   真正操作开关的位置是 ProcessPage（操作页）。本页提供「去设置」跳转。
    ///   三个状态与 Models.AppSettings 字段一一对应：
    ///   · KillToggle           → SessionSlimKillEnabled（分账号杀进程，默认 true）
    ///   · UserPolicyToggle     → SessionSlimUserPolicyEnabled（Edge 预启动，默认 false）
    ///   · MachinePolicyToggle  → SessionSlimMachinePolicyEnabled（机器级 HKLM，默认 false）
    /// </summary>
    public partial class SessionsSlimPage : Window
    {
        public SessionsSlimPage()
        {
            InitializeComponent();
            LoadSettings();
        }

        public static void ShowFor(Window? owner)
        {
            var w = new SessionsSlimPage();
            if (owner != null && owner.IsVisible) w.Owner = owner;
            w.ShowDialog();
        }

        /// <summary>只读地把三个开关的当前状态显示到界面上（不写配置）。</summary>
        private void LoadSettings()
        {
            try
            {
                var s = App.Config?.Settings;
                if (s == null) return;
                // ★ 只读展示：ToggleButton 已 IsEnabled=false，这里只同步显示状态。
                KillToggle.IsChecked = s.SessionSlimKillEnabled;
                UserPolicyToggle.IsChecked = s.SessionSlimUserPolicyEnabled;
                MachinePolicyToggle.IsChecked = s.SessionSlimMachinePolicyEnabled;
            }
            catch { }
        }

        /// <summary>跳转到操作页（会话瘦身 / ProcessPage），关闭本说明弹窗。</summary>
        private void JumpToSettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Close();
                if (Application.Current?.MainWindow is MainWindow main)
                    main.NavigateTo("procs");
            }
            catch (Exception ex)
            {
                LogBus.Error("跳转到会话瘦身页失败", ex.Message);
            }
        }

        private void DragArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try { DragMove(); } catch { }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
