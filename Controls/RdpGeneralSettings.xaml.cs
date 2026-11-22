using System;
using System.Linq;
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

   // 订阅全局配置变更：网页改设置后立刻刷新本面板（不用切页）。
   // ★ 在构造函数里订阅、Unloaded 时退订：
   //   如果放在 Loaded 里订阅，页面来回切换 N 次就会挂 N 个处理器。
   //   退订用 -= 是安全的（未订阅时移除不报错），所以不会被重复加成问题。
   App.SettingsChanged += OnSettingsChanged;
   Unloaded += (_, __) => App.SettingsChanged -= OnSettingsChanged;
  }
  /// <summary>
  /// 外部（设置页）切换到本控件时调用，重新读一遍配置。
  ///
  /// ★★ 为什么需要它（2026-11-22）：★★
  ///   网页可以改录制参数（/api/settings），直接写进 App.Config.Settings。
  ///   而本控件只在 Loaded 时读一次 —— 页面切换不会重新触发 Loaded，
  ///   于是控件里显示的还是打开时的旧值（实测：网页改成 5fps，
  ///   应用里仍显示 30fps）。更糟的是此时只要动任意一项，
  ///   就会把旧值回写，覆盖掉网页刚改的配置。
  ///
  ///   所以：每次切到本页都重新载入一次。
  /// </summary>
  public void Reload()
  {
   try { LoadSettings(); } catch { }
  }

  /// <summary>配置被外部改了（网页端）→ 重读配置。</summary>
  private void OnSettingsChanged()
  {
   try { LoadSettings(); } catch { }
  }

  private void LoadSettings()
  {
   // ★ 用 try/finally 保证 _loading 一定被复位。
   //   原来没有保护：中途抛异常（比如某个控件还没初始化）就会永久停在 true，
   //   之后用户改任何一项都会被 `if (_loading) return;` 静默忽略 ——
   //   表现为"设置改了不生效"，而且完全无声。
   _loading = true;
   try { LoadSettingsCore(); }
   finally { _loading = false; }
  }

  private void LoadSettingsCore()
  {
   CaptureClickCheck.IsChecked = App.Config.Settings.RdpCaptureOnClick;

   // 保护层边框：空配置表示"跟主题默认色"，输入框也保持为空，
   // 不要把默认色写成具体值 —— 否则用户切主题后就不跟随了。
   ShieldColorBox.Text = App.Config.Settings.ShieldBorderColor ?? "";
   UpdateShieldColorPreview();
   ShieldThicknessSlider.Value = Math.Max(0, Math.Min(8, App.Config.Settings.ShieldBorderThickness));
   ShieldThicknessText.Text = ((int)ShieldThicknessSlider.Value) + " px";

   LoadRecordSettings();

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
  }

  // ══════════ 屏幕录制（3.2.8）══════════
  //
  //  用户需求：「在 RDP 通用配置里增加一个设置栏目，对目标用户（RDP 配置里有的
  //            用户，可多选）开启录像功能，然后用户可以设置录像时长」。

  /// <summary>把账号列表渲染成一排复选框，勾选状态来自配置。</summary>
  private void LoadRecordSettings()
  {
   try
   {
    var settings = App.Config.Settings;
    // 帧率
    SelectComboByTag(RecordFpsCombo, settings.RecordFps);
    SelectComboByTag(RecordQualityCombo, settings.RecordQuality);
    RecordKeepBox.Text = settings.RecordKeepSeconds.ToString();

    // 账号多选
    // ★★ 3.2.8：**排除主账号** ★★
    //   用户明确指出：「不应该有主账号，主账号都不投送」。
    //   录制依赖目标会话里的注入代理，而代理**从不投送到主账号**（那是操作者自己的桌面），
    //   所以主账号出现在这里只会让用户勾了却没效果 —— 直接不显示。
    //   判定用 AccountScope.IsPrimaryUser（与"仅管理挂机账号"同一套口径）。
    RecordAccountPanel.Children.Clear();
    var accounts = App.Config.Accounts
        .Where(a => !string.IsNullOrWhiteSpace(a.UserName))
        .Where(a => !AccountScope.IsPrimaryUser(a.UserName))
        .ToList();
    if (accounts.Count == 0)
    {
     RecordAccountPanel.Children.Add(new TextBlock
     {
      Text = "没有可录制的账号（主账号不投送代理，无法录制）",
      Style = (Style)FindResource("Caption"),
      TextWrapping = TextWrapping.Wrap
     });
     return;
    }

    foreach (var acc in accounts)
    {
     var cb = new CheckBox
     {
      Style = (Style)FindResource("GlassCheckBox"),
      Content = string.IsNullOrWhiteSpace(acc.DisplayName) ? acc.UserName : acc.DisplayName + "（" + acc.UserName + "）",
      IsChecked = settings.RecordAccountIds.Contains(acc.Id),
      Tag = acc.Id,
      FontSize = 10,
      Margin = new Thickness(0, 2, 0, 2)
     };
     cb.Checked += RecordAccount_Changed;
     cb.Unchecked += RecordAccount_Changed;
     RecordAccountPanel.Children.Add(cb);
    }

    // ★ 顺手清理历史配置里可能残留的主账号勾选（旧版本允许勾选它）
    try
    {
     int removed = settings.RecordAccountIds.RemoveAll(id =>
     {
      var acc = App.Config.Accounts.FirstOrDefault(a => a.Id == id);
      return acc != null && AccountScope.IsPrimaryUser(acc.UserName);
     });
     if (removed > 0) Save();
    }
    catch { }

    UpdateRecordHint();
   }
   catch (Exception ex) { App.AppendErrorLog("LoadRecordSettings", ex); }
  }

  private static void SelectComboByTag(ComboBox? combo, int value)
  {
   if (combo == null) return;
   foreach (var obj in combo.Items)
    if (obj is ComboBoxItem item && item.Tag is string tag && tag == value.ToString())
    { combo.SelectedItem = item; return; }
   if (combo.Items.Count > 0) combo.SelectedIndex = 0;
  }

  private void RecordAccount_Changed(object sender, RoutedEventArgs e)
  {
   if (_loading) return;
   if (sender is not CheckBox cb || cb.Tag is not string id) return;

   var list = App.Config.Settings.RecordAccountIds;
   if (cb.IsChecked == true) { if (!list.Contains(id)) list.Add(id); }
   else list.Remove(id);

   Save();
   UpdateRecordHint();
  }

  private void RecordFps_Changed(object sender, SelectionChangedEventArgs e)
  {
   if (_loading) return;
   if (RecordFpsCombo?.SelectedItem is ComboBoxItem item && item.Tag is string tag
       && int.TryParse(tag, out int fps))
   {
    App.Config.Settings.RecordFps = fps;
    Save();
    UpdateRecordHint();
   }
  }

  private void RecordQuality_Changed(object sender, SelectionChangedEventArgs e)
  {
   if (_loading) return;
   if (RecordQualityCombo?.SelectedItem is ComboBoxItem item && item.Tag is string tag
       && int.TryParse(tag, out int q))
   {
    App.Config.Settings.RecordQuality = q;
    Save();
    UpdateRecordHint();
   }
  }

  private void RecordKeep_Changed(object sender, TextChangedEventArgs e)
  {
   if (_loading) return;
   if (int.TryParse((RecordKeepBox.Text ?? "").Trim(), out int sec) && sec >= 5 && sec <= 600)
   {
    App.Config.Settings.RecordKeepSeconds = sec;
    Save();
    UpdateRecordHint();
   }
  }

  /// <summary>把内存占用估算显示给用户，避免开了很多账号才发现吃内存。</summary>
  private void UpdateRecordHint()
  {
   try
   {
    var s = App.Config.Settings;
    int count = s.RecordAccountIds.Count;
    if (count == 0)
    {
     RecordHintText.Text = "未选择任何账号 —— 不录制。勾选上面的账号后，代理会在该账号的会话内开始录制。";
     return;
    }
    // 1920x1080 质量60≈126KB、75≈159KB、90≈206KB /帧
    double perFrameKb = s.RecordQuality switch { >= 85 => 206, >= 70 => 159, _ => 126 };
    double totalMb = count * perFrameKb * s.RecordFps * s.RecordKeepSeconds / 1024.0;
    RecordHintText.Text = "已选 " + count + " 个账号 · " + s.RecordFps + "fps · 质量 " + s.RecordQuality
        + " · 保留 " + s.RecordKeepSeconds + " 秒，预计占用内存约 " + totalMb.ToString("0") + " MB。"
        + "（只在内存里循环，点「保存」才写盘）";
   }
   catch { }
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

  /// <summary>退出全屏备用热键（可选）。spec 为 null = 用户清空 → 不钩任何键。</summary>
  private void OnExitFullScreenHotkey(HotkeySpec? spec)
  {
   if (_loading) return;
   App.Config.Settings.ExitFullScreenHotkey = spec?.ToString() ?? "";
   Save();
   SoulLink.Views.ConsolePage.NotifyFullScreenHotkeysChanged();
  }

  /// <summary>清空备用热键：不钩任何键（默认就是空的）。</summary>
  private void ExitFullScreenHotkeyClear_Click(object sender, RoutedEventArgs e)
  {
   ExitFullScreenHotkeyRecorder.SetSpec(null);
   App.Config.Settings.ExitFullScreenHotkey = "";
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
   // 3.2.8：录制选项改了立刻同步到已投送的账号（勾上的开始录，取消的停）
   try { SoulLink.Views.ConsolePage.NotifyRecordingSettingsChanged(); } catch { }
   foreach (Window window in Application.Current.Windows)
    foreach (var console in FindConsoles(window)) console.ApplyGlobalSettings();
   if (SavedText != null) SavedText.Text = "已保存 · 所有用户共用 · " + DateTime.Now.ToString("HH:mm:ss");
  }
 }
}