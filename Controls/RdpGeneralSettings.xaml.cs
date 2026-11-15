using System;
using System.Windows;
using System.Windows.Controls;
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
   SensitivitySlider.Value = App.Config.Settings.LookSensitivity;
   SensitivityText.Text = App.Config.Settings.LookSensitivity.ToString("0.0") + "x";
   GlobalHotkey.SetSpec(HotkeySpec.Parse(App.Config.Settings.LockHotkey));
   GlobalHotkey.Recorded -= OnHotkey;
   GlobalHotkey.Recorded += OnHotkey;
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
  private void OnHotkey(HotkeySpec? spec)
  {
   if (_loading || spec == null) return;
   App.Config.Settings.LockHotkey = spec.ToString();
   Save();
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
   foreach (Window window in Application.Current.Windows)
    foreach (var console in FindConsoles(window)) console.ApplyGlobalSettings();
   if (SavedText != null) SavedText.Text = "已保存 · 所有用户共用 · " + DateTime.Now.ToString("HH:mm:ss");
  }
 }
}