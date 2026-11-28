using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
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

   // ★ 2026-11-22：状态轮询（显示「实际在用的抓屏后端」）
   //   为什么需要：配置里选了 WGC，也可能因显存不足/身份不对而静默降级到 BitLlt，
   //   光看下拉框看不出来。这里每 5 秒问一次代理，把真实后端显示出来。
   //   ★ 用 TickHub 而不是新建 DispatcherTimer（AGENTS.md 性能约束）。
   //   ★ Unloaded 必须退订，否则页面来回切换会挂 N 个订阅者。
   Loaded += (_, __) => StartStatusPolling();
   Unloaded += (_, __) =>
   {
    App.SettingsChanged -= OnSettingsChanged;
    StopStatusPolling();
   };
  }

  private IDisposable? _statusTick;

  private void StartStatusPolling()
  {
   StopStatusPolling();
   try
   {
    _statusTick = TickHub.Register("RdpGeneralSettings.recordstatus", 5, () => _ = RefreshBackendStatusAsync());
    _ = RefreshBackendStatusAsync();   // 进来先查一次，不用等 5 秒
   }
   catch { }
  }

  private void StopStatusPolling()
  {
   try { _statusTick?.Dispose(); } catch { }
   _statusTick = null;
  }

  /// <summary>
  /// 问代理「你现在实际用哪个抓屏后端」，只对**开了录制的账号**有意义
  ///（没开录制时代理根本不抓屏，问也是 idle）。
  /// </summary>
  private async Task RefreshBackendStatusAsync()
  {
   try
   {
    if (BackendStatusText == null) return;
    var s = App.Config?.Settings;
    if (s == null) return;

    var recIds = s.RecordAccountIds ?? new List<string>();
    if (recIds.Count == 0)
    {
     bool hasRemoteControl = App.Config.Accounts.Any(a => s.IsRemoteControlEnabledFor(a.Id));
     BackendStatusText.Text = hasRemoteControl ? "当前没有账号开启录像。观看远控时才会启动采集。" : "录像和网页远控均已关闭。";
     return;
    }

    // 只查第一个开录制的账号（避免一次打多个代理）
    string accId = recIds[0];
    var acc = App.Config.Accounts?.FirstOrDefault(a => a.Id == accId);
    string label = acc?.UserName ?? accId;

    // ★ 代理按会话注册，所以要先把账号 ID 换成会话号再取 relay。
    //   没有会话 = 该账号当前没连上，直接告诉用户，不去打不存在的代理。
    int sid = 0;
    try
    {
     var st = AccountStateCenter.Get(accId);
     sid = st?.SessionId ?? 0;
    }
    catch { }
    if (sid <= 0)
    {
     BackendStatusText.Text = label + "：当前没有活动会话（账号未连接）";
     return;
    }

    var relay = RelayClient.FindBySession(sid);
    if (relay == null)
    {
     BackendStatusText.Text = label + "：会话 " + sid + " 的代理通道未建立";
     return;
    }
    var resp = await relay.RecordStatusAsync().ConfigureAwait(true);
    string msg = resp?.Message ?? "";
    if (msg.Length == 0) { BackendStatusText.Text = label + "：代理无响应"; return; }

    // 解析 |capture=WGC 这种键值
    string backend = "?";
    foreach (var part in msg.Split('|'))
    {
     int eq = part.IndexOf('=');
     if (eq > 0 && part.Substring(0, eq).Trim().Equals("capture", StringComparison.OrdinalIgnoreCase))
     { backend = part.Substring(eq + 1).Trim(); break; }
    }
    string status = msg.Split('|')[0];

    string human = backend switch
    {
     "WGC" => "GPU 抓屏（WGC）✅",
     "BitBlt" => "兼容模式（BitLlt）⚠ 如果你的设置是 GPU 抓屏，说明降级了",
     _ => backend
    };
    BackendStatusText.Text = "实际在跑：" + label + " · " + human + " · 状态 " + status;
   }
   catch { try { if (BackendStatusText != null) BackendStatusText.Text = ""; } catch { } }
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
   GlobalHotkey.SetSpec(HotkeySpec.ParseRdpHotkey(App.Config.Settings.LockHotkey, "Ctrl+Alt+Q"));
   GlobalHotkey.Recorded -= OnHotkey;
   GlobalHotkey.Recorded += OnHotkey;

   // ★ 3.2.4：全屏热键（可自定义，避免 F11 被 RDP/游戏吞掉后进得去出不来）
   FullScreenHotkeyRecorder.SetSpec(HotkeySpec.ParseRdpHotkey(App.Config.Settings.FullScreenHotkey, "F11"));
   FullScreenHotkeyRecorder.Recorded -= OnFullScreenHotkey;
   FullScreenHotkeyRecorder.Recorded += OnFullScreenHotkey;

   ExitFullScreenHotkeyRecorder.SetSpec(HotkeySpec.ParseRdpHotkey(App.Config.Settings.ExitFullScreenHotkey));
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

  /// <summary>读取录像与直播参数，以及每个账号各自的两个开关。</summary>
  private void LoadRecordSettings()
  {
   try
   {
    var settings = App.Config.Settings;
    // 帧率（录像帧率仍是固定档，直播帧率改滑动条）
    SelectComboByTag(RecordFpsCombo, settings.RecordFps);
    StreamFpsSlider.Value = Math.Clamp(settings.StreamFps, 15, 120);
    StreamFpsText.Text = settings.StreamFps + " 帧/秒";
    SelectComboByTag(RecordQualityCombo, settings.RecordQuality);
    RecordKeepBox.Text = settings.RecordKeepSeconds.ToString();
    // ★ 2026-11-22：抓屏方式（之前只有配置字段、无界面入口）
    SelectComboByTag(CaptureModeCombo, settings.CaptureMode);
    // ★ 2026-11-23：码率改滑动条（1~50 Mbps），NVENC 预设 + 关键帧缓冲上调
    BitrateSlider.Value = Math.Clamp(settings.VideoBitrateKbps, 1000, 500000) / 1000.0;
    BitrateText.Text = (settings.VideoBitrateKbps / 1000.0).ToString("0") + " Mbps";
    SelectComboByTag(NvencPresetCombo, settings.NvencPreset);
    VbvIncreaseSlider.Value = Math.Clamp(settings.VbvBufferIncreasePercent, 0, 200);
    VbvIncreaseText.Text = settings.VbvBufferIncreasePercent + "%";
    UpdateVideoHintText();

    // 账号多选
    // ★★ 3.2.8：**排除主账号** ★★
    //   用户明确指出：「不应该有主账号，主账号都不投送」。
    //   录制依赖目标会话里的注入代理，而代理**从不投送到主账号**（那是操作者自己的桌面），
    //   所以主账号出现在这里只会让用户勾了却没效果 —— 直接不显示。
    //   判定用 AccountScope.IsPrimaryUser（与"仅管理挂机账号"同一套口径）。
    CaptureAccountPanel.Children.Clear();
    var accounts = App.Config.Accounts
        .Where(a => !string.IsNullOrWhiteSpace(a.Id) && !string.IsNullOrWhiteSpace(a.UserName))
        .Where(a => !AccountScope.IsPrimaryUser(a.UserName))
        .ToList();
    CaptureAccountsEmpty.Visibility = accounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    foreach (var acc in accounts)
    {
     var row = new Grid { MinHeight = 26 };
     row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
     row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "RecordOption" });
     row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "RemoteOption" });
     string label = string.IsNullOrWhiteSpace(acc.DisplayName) ? acc.UserName : acc.DisplayName + "（" + acc.UserName + "）";
     row.Children.Add(new TextBlock
     {
      Text = label,
      Style = (Style)FindResource("Caption"),
      VerticalAlignment = VerticalAlignment.Center,
      TextTrimming = TextTrimming.CharacterEllipsis,
      ToolTip = label
     });
     AddAccountOption(row, 1, acc.Id, label + " · 屏幕录制",
        settings.RecordAccountIds.Contains(acc.Id), RecordAccount_Changed);
     AddAccountOption(row, 2, acc.Id, label + " · 网页远控",
        settings.IsRemoteControlEnabledFor(acc.Id), RemoteControl_Changed);
     CaptureAccountPanel.Children.Add(row);
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

  private void AddAccountOption(Grid row, int column, string id, string name, bool isChecked, RoutedEventHandler changed)
  {
   var option = new CheckBox
   {
    Style = (Style)FindResource("GlassCheckBox"),
    IsChecked = isChecked,
    Tag = id,
    ToolTip = name,
    HorizontalAlignment = HorizontalAlignment.Center,
    VerticalAlignment = VerticalAlignment.Center
   };
   AutomationProperties.SetName(option, name);
   option.Checked += changed;
   option.Unchecked += changed;
   Grid.SetColumn(option, column);
   row.Children.Add(option);
  }

  private void RemoteControl_Changed(object sender, RoutedEventArgs e)
  {
   if (_loading || sender is not CheckBox option || option.Tag is not string id) return;

   var settings = App.Config.Settings;
   var ids = settings.RemoteControlAccountIds?.ToList() ?? new List<string>();
   if (option.IsChecked == true)
   {
    if (!ids.Contains(id)) ids.Add(id);
   }
   else ids.RemoveAll(accountId => accountId == id);
   settings.RemoteControlAccountIds = ids;
   ConfigStore.Save(App.Config);
   RelayClient.PushRemoteControlToConnectedAgents(id);
   ShowSaved();
   _ = RefreshBackendStatusAsync();
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

  /// <summary>
  /// 抓屏方式切换（对应 AppSettings.CaptureMode）。
  /// ★ 值语义与 Relay/SessionRecorder.cs 的 CreateCapture() 一致：
  ///   0=自动(DDA→WGC→BitBlt) 1=DXGI-DDA 2=WGC 3=BitBlt。
  /// ★ 通过 captureParams 热切；worker 重建抓屏和编码器，viewer 不断线。
  /// </summary>
  private void CaptureMode_Changed(object sender, SelectionChangedEventArgs e)
  {
   try
   {
    if (CaptureModeCombo?.SelectedItem is ComboBoxItem item && item.Tag is string tag
        && int.TryParse(tag, out int mode))
    {
     App.Config.Settings.CaptureMode = Math.Clamp(mode, 0, 3);
     RelayClient.PushCaptureModeToConnectedAgents(mode);
     ConfigStore.Save(App.Config);
     UpdateVideoHintText();
    }
   }
   catch { }
  }

  /// <summary>视频码率切换（对应 AppSettings.VideoBitrateKbps）。</summary>
  private void BitrateSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
  {
   if (BitrateText != null)
    BitrateText.Text = ((int)Math.Round(e.NewValue)) + " Mbps";
   if (_loading) return;
   int kbps = (int)Math.Round(e.NewValue) * 1000;
   if (kbps == App.Config.Settings.VideoBitrateKbps) return;
   App.Config.Settings.VideoBitrateKbps = kbps;
   Save();
   RelayClient.PushVideoParametersToConnectedAgents();
   UpdateVideoHintText();
  }

  private void NvencPreset_Changed(object sender, SelectionChangedEventArgs e)
  {
   if (_loading) return;
   if (NvencPresetCombo?.SelectedItem is ComboBoxItem item && item.Tag is string tag
       && int.TryParse(tag, out int preset))
   {
    App.Config.Settings.NvencPreset = preset;
    Save();
    RelayClient.PushVideoParametersToConnectedAgents();
    UpdateVideoHintText();
   }
  }

  private void VbvIncreaseSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
  {
   if (VbvIncreaseText != null)
    VbvIncreaseText.Text = ((int)Math.Round(e.NewValue)) + "%";
   if (_loading) return;
   int pct = (int)Math.Round(e.NewValue);
   if (pct == App.Config.Settings.VbvBufferIncreasePercent) return;
   App.Config.Settings.VbvBufferIncreasePercent = pct;
   Save();
   RelayClient.PushVideoParametersToConnectedAgents();
   UpdateVideoHintText();
  }

  /// <summary>
  /// 显示「当前设置意味着什么」——用具体数字讲，不堆术语。
  /// ★ 用户诉求：「明确告知用户它们是干嘛的，让人好判断」。
  /// </summary>
  private void UpdateVideoHintText()
  {
   try
   {
    if (VideoHintText == null) return;
    var s = App.Config?.Settings;
    if (s == null) return;

    string capText = s.CaptureMode switch
    {
        1 => "DXGI Desktop Duplication（显式 DDA）",
        2 => "Windows Graphics Capture（WGC）",
        3 => "兼容模式（单帧 17~34ms，一定能用但慢）",
        _ => "自动（优先 GPU，失败降级到兼容模式）"
    };

    double hourGb = s.VideoBitrateKbps / 8.0 * 3600 / 1024.0;
    string brText = (s.VideoBitrateKbps / 1000.0).ToString("0") + " Mbps（约 "
        + hourGb.ToString("0.0") + " GB/小时）";

    string presetName = s.NvencPreset switch
    {
        1 => "P1（最快）", 2 => "P2", 4 => "P4", 5 => "P5", 6 => "P6", 7 => "P7（最慢）", _ => "P3（均衡）"
    };
    string vbvText = s.VbvBufferIncreasePercent > 0
        ? "关键帧缓冲 +" + s.VbvBufferIncreasePercent + "%（关键帧画质更好，首屏稍慢）"
        : "关键帧缓冲 0%（关键帧压缩最强，首屏最快）";

    VideoHintText.Text = "抓屏：" + capText + "\n码率：" + brText
        + " · 直播帧率 " + s.StreamFps + "fps"
        + "\nNVENC 预设：" + presetName + " · " + vbvText
        + "\n（码率/预设/缓冲只在 H.264 硬编启用时生效；JPEG 模式忽略）"
        + "\n\n★ 码率/预设/缓冲/帧率会实时下发，正在观看的直播连接不断开；抓屏方式也会热切，不重投代理。";
   }
   catch { }
  }

  private void RecordFps_Changed(object sender, SelectionChangedEventArgs e)
  {
   if (_loading) return;
   if (RecordFpsCombo?.SelectedItem is ComboBoxItem item && item.Tag is string tag
       && int.TryParse(tag, out int fps))
   {
    App.Config.Settings.RecordFps = fps;
    Save();
    RelayClient.PushVideoParametersToConnectedAgents();
    UpdateRecordHint();
   }
  }

  private void StreamFpsSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
  {
   if (StreamFpsText != null)
    StreamFpsText.Text = ((int)Math.Round(e.NewValue)) + " 帧/秒";
   if (_loading) return;
   int fps = (int)Math.Round(e.NewValue);
   if (fps == App.Config.Settings.StreamFps) return;
   App.Config.Settings.StreamFps = fps;
   Save();
   RelayClient.PushVideoParametersToConnectedAgents();
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
    ShieldColorPreview.BorderBrush = UiBrush.Get("StrokeMid");
    ShieldColorPreview.ToolTip = "留空 = 主题默认色";
    return;
   }
   var c = SoulLink.Services.ColorText.TryParse(text);
   if (c == null)
   {
    // 解析失败：预览退回默认色并标红，但**不写配置** —— 别把非法值存进去。
    ShieldColorPreview.Background = new SolidColorBrush(Color.FromArgb(0xCC, 0xF0, 0xC0, 0x41));
    ShieldColorPreview.BorderBrush = UiBrush.Get("RoseBrush");
    ShieldColorPreview.ToolTip = "颜色格式无法识别";
    return;
   }
   ShieldColorPreview.Background = new SolidColorBrush(c.Value);
   ShieldColorPreview.BorderBrush = UiBrush.Get("StrokeMid");
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
   if (_loading || spec == null || spec.IsBareEscape) return;
   App.Config.Settings.LockHotkey = spec.ToString();
   Save();
  }

  /// <summary>进/出全屏热键。改完立刻让所有已打开的画面重新装钩子。</summary>
  private void OnFullScreenHotkey(HotkeySpec? spec)
  {
   if (_loading || spec == null || spec.IsBareEscape) return;
   App.Config.Settings.FullScreenHotkey = spec.ToString();
   Save();
   SoulLink.Views.ConsolePage.NotifyFullScreenHotkeysChanged();
  }

  /// <summary>退出全屏备用热键（可选）。spec 为 null = 用户清空 → 不钩任何键。</summary>
  private void OnExitFullScreenHotkey(HotkeySpec? spec)
  {
   if (_loading || spec?.IsBareEscape == true) return;
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
   ShowSaved();
  }

  private void ShowSaved()
  {
   if (SavedText != null) SavedText.Text = "已保存 · " + DateTime.Now.ToString("HH:mm:ss");
  }
 }
}
