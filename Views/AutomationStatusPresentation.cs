using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SoulLink.Controls;
using SoulLink.Models;
using SoulLink.Services;

namespace SoulLink.Views;

internal readonly record struct AutomationStatusPresentation(string Text, string BrushKey, bool DoneToday = false, bool CanClearToday = false)
{
    private static readonly TimeSpan ResultHold = TimeSpan.FromMilliseconds(700);

    public Brush Indicator => UiBrush.Get(BrushKey);

    public static AutomationStatusPresentation ForFlow(AutomationFlow flow, DateTime now)
    {
        bool done = PlcLogic.IsDailyComplete(flow, now);
        bool canClear = CanClear(flow, done);
        if (!string.IsNullOrEmpty(flow.WaitingChannel))
            return new(flow.WaitingChannel, "AmberBrush", done, canClear);
        if (flow.Paused)
            return new("暂停中" + (string.IsNullOrWhiteSpace(flow.PauseReason) ? "" : $"（{flow.PauseReason}）"), "AmberBrush", done);
        if (!flow.Enabled)
            return new("暂停：流程未启用", "AmberBrush", done);
        if (done)
            return new("今日已完成", "AmberBrush", true, true);

        string when = flow.LastActionResultAt?.ToString("HH:mm:ss") ?? "";
        if (flow.LastActionResultAt is DateTime at && now >= at && now - at <= ResultHold)
        {
            string action = string.IsNullOrWhiteSpace(flow.LastRunAction) ? "" : $"「{flow.LastRunAction}」";
            if (flow.LastRunOk == true)
                return new($"执行成功{action} · {when}", "MintBrush");
            if (flow.LastRunOk == false)
            {
                string reason = string.IsNullOrWhiteSpace(flow.LastRunError) ? "未提供原因" : flow.LastRunError;
                return new($"执行失败{action}：{reason} · {when}", "RoseBrush");
            }
        }
        if (flow.LastRunOk is bool ok && when.Length > 0)
            return new($"等待下一次 · 上次{(ok ? "成功" : "失败")} {when}", "DesktopAccent");

        return new(flow.NeedsManualTrigger
            ? "等待：只连了「手动」触点，点「运行一次」才执行"
            : "等待：条件满足时自动执行", "DesktopAccent");
    }

    public static bool ClearToday(AppConfig config, AutomationFlow flow, DateTime now)
    {
        try
        {
            bool applied = false;
            if (!FlowRunner.TryEditIdleFlow(flow.Id, () =>
            {
                if (!config.Automations.Contains(flow) || !CanClear(flow, PlcLogic.IsDailyComplete(flow, now))) return;
                PlcLogic.ClearDailyLatch(flow);
                flow.ToggleTodayRequested = null;
                flow.RunResetRequested = false;
                FlowRunLifecycle.StartRun(flow, now);
                applied = true;
            }) || !applied) return false;

            ConfigStore.Save(config);
            LogBus.Info("已清除今日标记", $"「{flow.DisplayName}」今天可以重新自动运行");
            return true;
        }
        catch (Exception ex)
        {
            App.AppendErrorLog("ClearTodayMark", ex);
            LogBus.Error("清除今日标记失败", ex.Message);
            return false;
        }
    }

    private static bool CanClear(AutomationFlow flow, bool done)
        => done && flow.Enabled && !flow.Paused;
}

/// <summary>金色的 END 今日完成按钮，供总览和启动台共用。</summary>
public sealed class AutomationTodayButton : Button
{
    public AutomationTodayButton()
    {
        SetResourceReference(StyleProperty, "PillButton");
        Height = 26;
        MinWidth = 86;
        Padding = new Thickness(9, 0, 9, 0);
        Background = UiBrush.Get("AmberBrush", 0x30);
        BorderBrush = UiBrush.Get("AmberBrush", 0xC0);
        Foreground = UiBrush.Get("AmberBrush");
        ToolTip = "END 已标记今日完成；点击清除标记，今天可以重新运行。";
        AutomationProperties.SetName(this, "今日完成，清除标记");

        var label = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(new TextBlock
        {
            Text = "\uE73E",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center
        });
        label.Children.Add(new TextBlock
        {
            Text = "今日完成",
            FontSize = 10.5,
            Margin = new Thickness(5, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        Content = label;
    }
}
