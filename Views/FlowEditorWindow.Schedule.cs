using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SoulLink.Controls;
using SoulLink.Models;

namespace SoulLink.Views;

public partial class FlowEditorWindow
{
    private (FlowNode Node, TextBlock Text)? _scheduleProgress;

    private static bool ShouldShowScheduleParameter(FlowNode node, string key)
    {
        if (node.Type != FlowNodeType.TrigSchedule) return true;
        string recurrence = PlcSchedule.Recurrence(node);
        return key switch
        {
            "weekDays" => recurrence == "weekly",
            "month" => recurrence == "yearly",
            "dayOfMonth" => recurrence is "monthly" or "yearly",
            "startDate" => recurrence is "intervalDays" or "once",
            "intervalDays" => recurrence == "intervalDays",
            "stepSeconds" or "resetAfter" => recurrence != "once",
            "catchUpHours" => string.IsNullOrWhiteSpace(node.Get("catchUpSeconds")),
            "catchUpSeconds" => !string.IsNullOrWhiteSpace(node.Get("catchUpSeconds")),
            _ => true
        };
    }

    private void OnScheduleRecurrenceChanged(FlowNode node, ComboBox selector)
    {
        if (PlcSchedule.Recurrence(node) is "intervalDays" or "once" && string.IsNullOrWhiteSpace(node.Get("startDate")))
            node.Set("startDate", DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        bool focus = selector.IsKeyboardFocusWithin;
        Dispatcher.InvokeAsync(() =>
        {
            if (Canvas.SelectedNode != node) return;
            ShowPropPanel(node);
            if (focus && _fieldTargets.TryGetValue("recurrence", out var target)) target.Focus();
        });
    }

    private WrapPanel BuildWeekDaysEditor(FlowNode node, string key)
    {
        var container = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
        var labels = new[] { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };
        int[] order = [1, 2, 3, 4, 5, 6, 0];
        PlcSchedule.TryReadWeekDays(node.Get(key), out int selected);
        foreach (int day in order)
        {
            var box = new CheckBox
            {
                Content = labels[day],
                IsChecked = (selected & (1 << day)) != 0,
                Style = (Style)FindResource("GlassCheckBox"),
                Margin = new Thickness(0, 2, 9, 2)
            };
            box.Checked += (_, _) => SetDay(day, true);
            box.Unchecked += (_, _) => SetDay(day, false);
            container.Children.Add(box);
            _propUi.Add(box);
        }
        RegisterField(key, (CheckBox)container.Children[0]);
        return container;

        void SetDay(int day, bool enabled)
        {
            selected = enabled ? selected | (1 << day) : selected & ~(1 << day);
            node.Set(key, string.Join(",", order.Where(d => (selected & (1 << d)) != 0)));
            MarkDirty();
            Canvas.Redraw();
        }
    }

    private void AddScheduleProgressHint(FlowNode node)
    {
        var text = new TextBlock
        {
            FontSize = 10.5,
            Margin = new Thickness(0, 10, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = UiBrush.Get("MintBrush")
        };
        _scheduleProgress = (node, text);
        PropPanel.Children.Add(text);
        RefreshScheduleProgress();
    }

    private void RefreshScheduleProgress()
    {
        if (_scheduleProgress is not { } progress) return;
        var (node, text) = progress;
        var now = DateTime.Now;
        var next = PlcSchedule.Next(_flow, node, now);
        if (_flow.SatisfiedContacts.Contains(node.Id)) text.Text = "本次定时信号已触发，等待流程继续";
        else if (next is { } occurrence)
        {
            text.Text = occurrence.At < now ? $"本次 {occurrence.At:MM-dd HH:mm:ss} · 补跑窗口内"
                : occurrence.At == now ? $"本次 {occurrence.At:MM-dd HH:mm:ss} · 到达计划时刻"
                : $"下次 {occurrence.At:yyyy-MM-dd HH:mm:ss}";
            if (node.GetInt("stepSeconds", 0) > 0 && PlcSchedule.Recurrence(node) != "once")
                text.Text += $" · 本轮第 {(long)occurrence.StepIndex + 1} 次";
        }
        else text.Text = PlcSchedule.Validate(node) == null ? "没有后续计划" : "";
        text.Visibility = text.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}
