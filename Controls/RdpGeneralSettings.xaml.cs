using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SoulLink.Models;
using SoulLink.Services;

namespace SoulLink.Controls
{
 public partial class RdpGeneralSettings : UserControl
 {
  /// <summary>
  /// ★ 必须初始为 true！
  ///
  /// InitializeComponent() 建立模板时，Slider 会先按 XAML 里的 Minimum="1" 发出一次
  /// ValueChanged（old=0 → new=1）。那一刻 LoadSettings 还没跑、_loading 还是 false，
  /// 于是 "1" 被当成用户操作写进配置 —— 用户保存的灵敏度每次启动都被冲回 1。
  /// 实测日志：Sensitivity_Changed old=0.00 new=1.00 loading=False
  /// </summary>
  private bool _loading = true;

  public RdpGeneralSettings()
  {
   InitializeComponent();
   Loaded += (_, __) => LoadSettings();
  }
  private void LoadSettings()
  {
   _loading = true;
   CaptureClickCheck.IsChecked = App.Config.Settings.RdpCaptureOnClick;

   // 保护层边框：空配置表示"跟主题默认色"，输入框也保持为空，
   // 不要把默认色写成具体值 —— 否则用户切主题后就不跟随了。
   ShieldColorBox.Text = App.Config.Settings.ShieldBorderColor ?? "";
   UpdateShieldColorPreview();
   ShieldThicknessSlider.Value = Math.Max(0, Math.Min(8, App.Config.Settings.ShieldBorderThickness));
   ShieldThicknessText.Text = ((int)ShieldThicknessSlider.Value) + " px";

   SensitivitySlider.Value = App.Config.Settings.LookSensitivity;
   SensitivityText.Text = App.Config.Settings.LookSensitivity.ToString("0.0") + "x";
   GlobalHotkey.SetSpec(HotkeySpec.Parse(App.Config.Settings.LockHotkey));
   GlobalHotkey.Recorded -= OnHotkey;
   GlobalHotkey.Recorded += OnHotkey;

   // ★ 3.2.4：全屏热键（可自定义，避免 F11 被 RDP/游戏吞掉后进得去出不来）
   FullScreenHotkeyRecorder.SetSpec(HotkeySpec.Parse(App.Config.Settings.FullScreenHotkey));
   FullScreenHotkeyRecorder.Recorded -= OnFullScreenHotkey;
   FullScreenHotkeyRecorder.Recorded += OnFullScreenHotkey;

   ExitFullScreenHotkeyRecorder.SetSpec(HotkeySpec.Parse(App.Config.Settings.ExitFullScreenHotkey));
   ExitFullScreenHotkeyRecorder.Recorded -= OnExitFullScreenHotkey;
   ExitFullScreenHotkeyRecorder.Recorded += OnExitFullScreenHotkey;
   CaptureClickCheck.Checked -= Capture_Changed;
   CaptureClickCheck.Unchecked -= Capture_Changed;
   CaptureClickCheck.Checked += Capture_Changed;
   CaptureClickCheck.Unchecked += Capture_Changed;

   // 先同步一次文本，避免 Slider 的值被 clamp 后和文字不一致
   SensitivityText.Text = SliderValueToText(SensitivitySlider.Value);
   _loading = false;
  }

  private static string SliderValueToText(double v) => Math.Round(v, 1).ToString("0.0") + "x";
  private void Capture_Changed(object sender, RoutedEventArgs e)
  {
   if (_loading) return;
   App.Config.Settings.RdpCaptureOnClick = CaptureClickCheck.IsChecked == true;
   Save();
  }
  private void Sensitivity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
  {
   if (_loading) return;
   App.Config.Settings.LookSensitivity = Math.Round(e.NewValue, 1);
   SensitivityText.Text = App.Config.Settings.LookSensitivity.ToString("0.0") + "x";
   Save();
  }
  /// <summary>预览块跟着输入实时变色；解析失败显示默认色 + 红框提示。</summary>
  private void UpdateShieldColorPreview()
  {
   if (ShieldColorPreview == null) return;
   var text = (ShieldColorBox.Text ?? "").Trim();
   if (text.Length == 0)
   {
    ShieldColorPreview.Background = new SolidColorBrush(Color.FromArgb(0xCC, 0xF0, 0xC0, 0x41));
    ShieldColorPreview.BorderBrush = new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF));
    ShieldColorPreview.ToolTip = "留空 = 主题默认色";
    return;
   }
   var c = SoulLink.Services.ColorText.TryParse(text);
   if (c == null)
   {
    // 解析失败：预览退回默认色并标红，但**不写配置** —— 别把非法值存进去。
    ShieldColorPreview.Background = new SolidColorBrush(Color.FromArgb(0xCC, 0xF0, 0xC0, 0x41));
    ShieldColorPreview.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x8B));
    ShieldColorPreview.ToolTip = "颜色格式无法识别";
    return;
   }
   ShieldColorPreview.Background = new SolidColorBrush(c.Value);
   ShieldColorPreview.BorderBrush = new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF));
   ShieldColorPreview.ToolTip = SoulLink.Services.ColorText.Format(c.Value);
  }

  private void ShieldColor_TextChanged(object sender, TextChangedEventArgs e)
  {
   if (_loading) return;
   var text = (ShieldColorBox.Text ?? "").Trim();
   // 只有能解析（或留空 = 用默认）才落盘，避免半截输入把配置写坏。
   if (text.Length > 0 && SoulLink.Services.ColorText.TryParse(text) == null)
   {
    UpdateShieldColorPreview();
    return;
   }
   UpdateShieldColorPreview();
   App.Config.Settings.ShieldBorderColor = text;
   Save();
  }

  private void ShieldColorReset_Click(object sender, RoutedEventArgs e)
  {
   ShieldColorBox.Text = "";
   UpdateShieldColorPreview();
   App.Config.Settings.ShieldBorderColor = "";
   Save();
  }

  private void ShieldThickness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
  {
   if (ShieldThicknessText != null)
    ShieldThicknessText.Text = ((int)Math.Round(e.NewValue)) + " px";

   // ★ 双重防护：
   //   ① _loading 期间（LoadSettings 正在把配置写进控件）绝不回写；
   //   ② 值没变就不写盘 —— InitializeComponent 与 LoadSettings 都会触发一次
   //      ValueChanged，直接落盘会把用户调好的粗细冲成默认值（实测：5 → 2）。
   if (_loading) return;
   var rounded = Math.Round(e.NewValue);
   if (Math.Abs(rounded - App.Config.Settings.ShieldBorderThickness) < 0.001) return;
   App.Config.Settings.ShieldBorderThickness = rounded;
   Save();
  }

  private void OnHotkey(HotkeySpec? spec)
  {
   if (_loading || spec == null) return;
   App.Config.Settings.LockHotkey = spec.ToString();
   Save();
  }

  /// <summary>进/出全屏热键。改完立刻让所有已打开的画面重新装钩子。</summary>
  private void OnFullScreenHotkey(HotkeySpec? spec)
  {
   if (_loading || spec == null) return;
   App.Config.Settings.FullScreenHotkey = spec.ToString();
   Save();
   SoulLink.Views.ConsolePage.NotifyFullScreenHotkeysChanged();
  }

  /// <summary>退出全屏备用热键（全屏下的逃生通道）。</summary>
  private void OnExitFullScreenHotkey(HotkeySpec? spec)
  {
   if (_loading || spec == null) return;
   App.Config.Settings.ExitFullScreenHotkey = spec.ToString();
   Save();
   SoulLink.Views.ConsolePage.NotifyFullScreenHotkeysChanged();
  }
  private static System.Collections.Generic.IEnumerable<RdpConsole> FindConsoles(DependencyObject root)
  {
   if (root is RdpConsole console) yield return console;
   for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
    foreach (var nested in FindConsoles(System.Windows.Media.VisualTreeHelper.GetChild(root, i))) yield return nested;
  }

  private void Save()
  {
   ConfigStore.Save(App.Config);
   SoulLink.Views.ConsolePage.NotifyGlobalSettingsChanged();
   foreach (Window window in Application.Current.Windows)
    foreach (var console in FindConsoles(window)) console.ApplyGlobalSettings();
   if (SavedText != null) SavedText.Text = "已保存 · 所有用户共用 · " + DateTime.Now.ToString("HH:mm:ss");
  }
 }
}