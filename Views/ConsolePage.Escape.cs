using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using SoulLink.Controls;
using SoulLink.Services;

namespace SoulLink.Views;

public partial class ConsolePage
{
    private const uint EscapeKey = 0x1B;
    private readonly RdpEscapeGesture _escapeGesture = new();
    private LowLevelKeyboardHook? _rdpEscapeHook;
    private EscapePress? _escapePress;
    private CancellationTokenSource? _escapeHold;
    private int _escapeContext;

    private sealed record EscapePress(RdpConsole Console, int Generation, int Context, bool FullScreen, bool Looking);

    private void InstallRdpEscapeHook()
    {
        if (_rdpEscapeHook?.IsHooked == true)
        {
            // Put this hook back at the front of the chain without losing the physical key-up.
            if (_rdpEscapeHook.Rearm()) return;
        }

        if (LowLevelKeyboardHook.IsPhysicalKeyDown((int)EscapeKey))
            _escapeGesture.Down(false, Environment.TickCount64, out _);
        else if (!_escapeGesture.IsIdle)
        {
            _escapeGesture.Up(Environment.TickCount64, out _);
            ClearEscapePress();
        }
        _rdpEscapeHook ??= new LowLevelKeyboardHook { HandlePhysicalKey = HandleRdpEscapeKey };
        _rdpEscapeHook.Install();
    }

    private bool HandleRdpEscapeKey(uint key, bool down, uint modifiers)
    {
        if (key != EscapeKey) return false;
        long now = Environment.TickCount64;
        if (down)
        {
            // At Esc-down the system already knows the state of other keys, including both sides of a modifier.
            var console = _escapeGesture.IsIdle && modifiers == 0 && LowLevelKeyboardHook.ReadPhysicalModifiers() == 0
                ? GetRdpEscapeTarget() : null;
            bool swallow = _escapeGesture.Down(console != null, now, out bool started);
            if (started && console != null)
            {
                var press = new EscapePress(console, console.ConnectionGeneration, _escapeContext, _isFullScreen, console.IsLooking);
                _escapePress = press;
                _escapeHold = new CancellationTokenSource();
                _ = FinishEscapeHoldAsync(press, _escapeHold.Token);
            }
            return swallow;
        }

        bool handled = _escapeGesture.Up(now, out var action);
        var completed = _escapePress;
        ClearEscapePress();
        if (completed != null && action != RdpEscapeAction.None)
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => ApplyEscapeAction(completed, action)));
        return handled;
    }

    private async Task FinishEscapeHoldAsync(EscapePress press, CancellationToken ct)
    {
        try
        {
            await Task.Delay(RdpEscapeGesture.HoldMilliseconds, ct).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() =>
            {
                if (!ReferenceEquals(_escapePress, press) || !_escapeGesture.Hold()) return;
                ClearEscapePress();
                ApplyEscapeAction(press, RdpEscapeAction.ExitLocal);
            }, DispatcherPriority.Input, ct);
        }
        catch (OperationCanceledException) { }
    }

    private void CancelRdpEscape()
    {
        _escapeContext++;
        _escapeGesture.Cancel();
        ClearEscapePress();
    }

    private void ClearEscapePress()
    {
        _escapePress = null;
        var hold = _escapeHold;
        _escapeHold = null;
        hold?.Cancel();
        hold?.Dispose();
    }

    private RdpConsole? GetRdpEscapeTarget()
    {
        if (NativeHotkey.HotkeysSuppressed || !IsLoaded || !IsVisible ||
            ViewPage?.Visibility != Visibility.Visible) return null;
        var console = _viewing?.Console;
        if (console == null || (!_isFullScreen && !console.IsLooking)) return null;
        var window = Window.GetWindow(this);
        if (window == null || window.WindowState == WindowState.Minimized) return null;
        IntPtr owner = new WindowInteropHelper(window).Handle;
        return owner != IntPtr.Zero && owner == GetEscapeForegroundWindow() ? console : null;
    }

    private void ApplyEscapeAction(EscapePress press, RdpEscapeAction action)
    {
        if (press.Context != _escapeContext || !ReferenceEquals(GetRdpEscapeTarget(), press.Console) ||
            press.Generation != press.Console.ConnectionGeneration ||
            press.FullScreen != _isFullScreen || press.Looking != press.Console.IsLooking) return;

        if (action == RdpEscapeAction.SendRemote)
        {
            if (!press.Console.TrySendRemoteEscape()) RdpConsole.Debug("远端 Esc 未发送：RDP 输入不可用");
            return;
        }

        if (press.Looking) press.Console.SetInputMode(ConsoleInputMode.Pointer);
        if (press.FullScreen) ExitFullScreen("EscapeHold");
    }

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow", ExactSpelling = true)]
    private static extern IntPtr GetEscapeForegroundWindow();
}
