using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SoulLink.Services;

namespace SoulLink.Controls
{
    /// <summary>
    /// 热键录制框：点「录制」后，用户按什么组合就记什么。
    /// 支持键盘组合和鼠标按键（中键 / 侧键）。
    ///
    /// 为什么不用 TextBox：让用户手敲 "Ctrl+Alt+Q" 既难记又容易写错，
    /// 而且鼠标键根本没法打字输入。
    /// </summary>
    public sealed class HotkeyRecorder : Button
    {
        public HotkeySpec? Spec { get; private set; }
        private bool _recording;
        private bool _ignoreInitialLeft;
        private int _ignoreLeftUntil;

        /// <summary>录制完成（或取消）时触发。</summary>
        public event Action<HotkeySpec?>? Recorded;

        public HotkeyRecorder()
        {
            Click += (_, __) => ToggleRecording();
            UpdateText();

            // 键盘：捕获所有按键
            PreviewKeyDown += OnPreviewKeyDown;
            PreviewKeyUp += OnPreviewKeyUp;

            // 失焦就取消录制，避免卡住
            LostKeyboardFocus += (_, __) => StopRecording();
            // 预览事件发生在 Button.Click 之前，避免点“停止”时把左键录成热键。
            PreviewMouseDown += OnRecMouseDown;
            PreviewMouseUp += (_, e) =>
            {
                if (_recording && e.ChangedButton == MouseButton.Left)
                    _ignoreInitialLeft = false;
            };
            IsVisibleChanged += (_, e) => { if (e.NewValue is false) StopRecording(); };
            Unloaded += (_, __) => StopRecording();
            LostMouseCapture += (_, __) => { };
        }

        private void ToggleRecording()
        {
            if (_recording) { StopRecording(); return; }
            _recording = true;
            // WPF CaptureMouse 会为当前激活按钮补发一次逻辑 LeftDown；
            // 这不是用户要录制的按键，必须等真正松开左键后再开始录制。
            _ignoreInitialLeft = Mouse.LeftButton == MouseButtonState.Pressed;
            _ignoreLeftUntil = Environment.TickCount + 300;
            NativeHotkey.BeginRecording();
            Focusable = true;
            Focus();
            Keyboard.Focus(this);
            CaptureMouse();

            // 记录鼠标键
            MouseDown += OnRecMouseDown;

            Content = "请按组合键…（Esc 取消）";
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0x33, 0x7C, 0x5C, 0xFF));
        }

        private void StopRecording()
        {
            if (!_recording) return;
            _recording = false;
            NativeHotkey.EndRecording();
            MouseDown -= OnRecMouseDown;
            try { ReleaseMouseCapture(); } catch { }
            UpdateText();
        }

        private void OnRecMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!_recording) return;
            e.Handled = true;
            if (e.ChangedButton == MouseButton.Left && _ignoreInitialLeft)
            {
                if (Mouse.LeftButton == MouseButtonState.Pressed || Environment.TickCount < _ignoreLeftUntil)
                    return;
                _ignoreInitialLeft = false;
            }

            string? btn = e.ChangedButton switch
            {
                MouseButton.Left => "Left",
                MouseButton.Right => "Right",
                MouseButton.Middle => "Middle",
                MouseButton.XButton1 => "X1",
                MouseButton.XButton2 => "X2",
                _ => null
            };
            if (btn == null) return;
            if (btn == "Left" && _ignoreInitialLeft)
            {
                _ignoreInitialLeft = false;
                return;
            }

            var spec = new HotkeySpec
            {
                Ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0,
                Alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0,
                Shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0,
                Win = (Keyboard.Modifiers & ModifierKeys.Windows) != 0,
                MouseButton = btn
            };

            Finish(spec);
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!_recording) return;
            e.Handled = true;

            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
            {
                StopRecording();
                return;
            }

            // 只按修饰键不算完成，继续等
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
                return;

            uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            if (vk == 0) return;

            var spec = new HotkeySpec
            {
                Ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0,
                Alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0,
                Shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0,
                Win = (Keyboard.Modifiers & ModifierKeys.Windows) != 0,
                Key = vk
            };

            Finish(spec);
        }

        private void OnPreviewKeyUp(object sender, KeyEventArgs e)
        {
            if (!_recording) return;
            e.Handled = true;
        }

        private void Finish(HotkeySpec spec)
        {
            StopRecording();
            Spec = spec;
            UpdateText();
            Recorded?.Invoke(spec);
        }

        /// <summary>外部设置当前值（初始化用）。</summary>
        public void SetSpec(HotkeySpec? spec, bool notify = false)
        {
            Spec = spec;
            UpdateText();
            if (notify) Recorded?.Invoke(spec);
        }

        private void UpdateText()
        {
            Content = Spec == null ? "点击录制" : Spec.ToString();
        }
    }
}
