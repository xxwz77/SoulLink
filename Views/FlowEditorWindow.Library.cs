using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SoulLink.Controls;
using SoulLink.Models;

namespace SoulLink.Views;

public partial class FlowEditorWindow
{
    private sealed record ModuleGroup(string Title, FlowNodeType[] Types);
    private sealed record LibraryEntry(string Group, Button Button);

    private static readonly ModuleGroup[] ConditionGroups =
    [
        new("时间和触发", [FlowNodeType.TrigSchedule, FlowNodeType.TrigInterval, FlowNodeType.CondTimeRange, FlowNodeType.TrigManual]),
        new("会话和程序", [FlowNodeType.TrigSessionOnline, FlowNodeType.CondSessionState, FlowNodeType.CondProcessExists,
            FlowNodeType.CondGenshinRunning, FlowNodeType.CondWindowForeground]),
        new("BetterGI 状态", [FlowNodeType.CondBgiRunning, FlowNodeType.TrigBgiIdle, FlowNodeType.CondBgiLiveState,
            FlowNodeType.CondBgiActive, FlowNodeType.CondScriptGroupState, FlowNodeType.CondDogfoodRoute]),
        new("日志", [FlowNodeType.TrigLogMatch, FlowNodeType.CondLogContains, FlowNodeType.CondLogMissing, FlowNodeType.CondLogHasError]),
        new("流程逻辑", [FlowNodeType.CondLastResult, FlowNodeType.CondCompare, FlowNodeType.CondChoice])
    ];

    private static readonly ModuleGroup[] ActionGroups =
    [
        new("BetterGI 操作", [FlowNodeType.ActStartBgi, FlowNodeType.ActStopBgi, FlowNodeType.ActStartOneDragon, FlowNodeType.ActStartGroups]),
        new("会话和程序", [FlowNodeType.ActSessionLogin, FlowNodeType.ActSessionReconnect, FlowNodeType.ActSessionLogoff,
            FlowNodeType.ActLaunchProcess, FlowNodeType.ActKillProcess, FlowNodeType.ActCloseGenshin]),
        new("键盘和鼠标", [FlowNodeType.ActKeyPress, FlowNodeType.ActMouseClick, FlowNodeType.ActMouseMove, FlowNodeType.ActMouseWheel]),
        new("截图和录像", [FlowNodeType.ActScreenshot, FlowNodeType.ActSaveRecording]),
        new("快捷动作和流程", [FlowNodeType.ActRunAction, FlowNodeType.ActJoinRoom, FlowNodeType.ActDelay])
    ];

    private const double LibraryColumnWidth = 270;
    private const double LibraryMinColumnWidth = 210;
    private const double LibraryColumnGap = 12;
    private const double LibraryHeadingHeight = 22;
    private const double LibraryScrollWidth = 8;
    private FrameworkElement? _libraryAnchor;
    private LibraryEntry[] _libraryEntries = [];
    private int _librarySplit = -1;
    private bool _libraryLayoutQueued;
    private DispatcherTimer? _closeLibraryTimer;

    private void BuildLibrary()
    {
        AddLibraryCategory(FlowNodeKind.Condition, "条件", "满足时继续", ConditionGroups);
        AddLibraryCategory(FlowNodeKind.Action, "动作", "条件满足后执行", ActionGroups);
        AddLibraryCategory(FlowNodeKind.Terminate, "当天完成", "到达后停止当天扫描", []);

        PreviewMouseDown += CloseLibraryOnOutsideClick;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || _libraryAnchor == null) return;
            if (KindMenuCard.IsKeyboardFocusWithin) _libraryAnchor.Focus();
            CloseLibraryMenu();
            e.Handled = true;
        };
        LocationChanged += (_, _) => QueueLibraryLayout();
        DpiChanged += (_, _) => QueueLibraryLayout();
        MenuLayer.SizeChanged += (_, _) => QueueLibraryLayout();
        Closed += (_, _) => CloseLibraryMenu();
    }

    private void AddLibraryCategory(FlowNodeKind kind, string title, string description, ModuleGroup[] groups)
    {
        var definitions = FlowNodeCatalog.ByKind(kind);
        if (definitions.Count == 0) return;
        string accent = FlowCanvas.AccentResource(kind);
        var card = LibraryButton(title, description, accent, isCategory: true);
        LibPanel.Children.Add(card);
        if (definitions.Count == 1)
        {
            card.MouseEnter += (_, _) => CloseLibraryMenu();
            BindLibraryItem(card, definitions[0]);
            return;
        }

        card.MouseEnter += (_, _) => OpenLibraryMenu(card, definitions, groups, accent);
        card.MouseLeave += (_, _) => ScheduleLibraryClose();
        card.Click += (_, _) => OpenLibraryMenu(card, definitions, groups, accent);
        card.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Space or Key.Down)) return;
            OpenLibraryMenu(card, definitions, groups, accent);
            _libraryEntries.FirstOrDefault()?.Button.Focus();
            e.Handled = true;
        };
    }

    private Button LibraryButton(string title, string description, string accent, bool isCategory, string? glyph = null)
    {
        var labels = new StackPanel();
        var name = new TextBlock { Text = title, FontSize = isCategory ? 14 : 12, FontWeight = FontWeights.SemiBold };
        name.SetResourceReference(TextBlock.ForegroundProperty, isCategory ? accent : "TextPrimary");
        labels.Children.Add(name);
        var detail = new TextBlock { Text = description, FontSize = 10.5, TextWrapping = TextWrapping.Wrap };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        labels.Children.Add(detail);

        var contents = new DockPanel();
        if (glyph != null)
        {
            var icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12, Width = 19, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
            icon.SetResourceReference(TextBlock.ForegroundProperty, accent);
            contents.Children.Add(icon);
        }
        contents.Children.Add(labels);
        var button = new Button
        {
            Style = (Style)FindResource(isCategory ? "DesktopButton" : "DesktopLink"),
            Content = contents,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = isCategory ? new Thickness(11, 8, 11, 8) : new Thickness(7, 4, 7, 4),
            Margin = new Thickness(0, 0, 0, isCategory ? 8 : 2)
        };
        AutomationProperties.SetName(button, title);
        AutomationProperties.SetHelpText(button, description);
        if (isCategory) button.SetResourceReference(Control.BorderBrushProperty, "StrokeMid");
        return button;
    }

    private void BindLibraryItem(Button button, FlowNodeDef definition)
    {
        Point pressedAt = default;
        button.PreviewMouseLeftButtonDown += (_, e) => pressedAt = e.GetPosition(button);
        button.PreviewMouseMove += (_, e) =>
        {
            if (!button.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
            var delta = e.GetPosition(button) - pressedAt;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            BeginDragFromLibrary(definition);
            e.Handled = true;
        };
        button.Click += (_, _) =>
        {
            CloseLibraryMenu();
            var center = new Point(
                Math.Max(20, CanvasScroll.ActualWidth / 2 - 89 + CanvasScroll.HorizontalOffset),
                Math.Max(20, CanvasScroll.ActualHeight / 2 - 31 + CanvasScroll.VerticalOffset));
            AddNode(definition, center);
        };
    }

    private void OpenLibraryMenu(FrameworkElement anchor, List<FlowNodeDef> definitions, ModuleGroup[] groups, string accent)
    {
        _closeLibraryTimer?.Stop();
        if (_libraryAnchor == anchor) return;

        var remaining = definitions.ToDictionary(definition => definition.Type);
        var entries = new List<LibraryEntry>(remaining.Count);
        foreach (var group in groups)
            foreach (var type in group.Types)
                if (remaining.Remove(type, out var definition)) AddEntry(group.Title, definition);
        foreach (var definition in remaining.Values) AddEntry("其他模块", definition);

        _libraryAnchor = anchor;
        _libraryEntries = entries.ToArray();
        _librarySplit = -1;
        MenuLayer.Visibility = Visibility.Visible;
        LayoutLibraryMenu();
        KindMenuScroll.ScrollToTop();

        void AddEntry(string group, FlowNodeDef definition)
        {
            var button = LibraryButton(definition.Title, definition.Desc, accent, isCategory: false, definition.Glyph);
            BindLibraryItem(button, definition);
            entries.Add(new LibraryEntry(group, button));
        }
    }

    private void QueueLibraryLayout()
    {
        if (_libraryAnchor == null || _libraryLayoutQueued) return;
        _libraryLayoutQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _libraryLayoutQueued = false;
            LayoutLibraryMenu();
        }));
    }

    private Rect LibraryBounds(FrameworkElement root, FrameworkElement anchor)
    {
        var bounds = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
        if (PresentationSource.FromVisual(root) != null)
        {
            var screenPoint = anchor.PointToScreen(new Point(anchor.ActualWidth / 2, anchor.ActualHeight / 2));
            var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)screenPoint.X, (int)screenPoint.Y));
            var area = screen.WorkingArea;
            var workArea = new Rect(root.PointFromScreen(new Point(area.Left, area.Top)), root.PointFromScreen(new Point(area.Right, area.Bottom)));
            bounds.Intersect(workArea);
        }
        var canvasTop = CanvasScroll.TranslatePoint(new Point(), root).Y;
        bounds.Intersect(new Rect(0, canvasTop, root.ActualWidth, CanvasScroll.ActualHeight));
        if (bounds.Width > 8 && bounds.Height > 8) bounds.Inflate(-4, -4);
        return bounds;
    }

    private void LayoutLibraryMenu()
    {
        if (_libraryAnchor is not { } anchor || MenuLayer.Parent is not FrameworkElement root) return;
        var bounds = LibraryBounds(root, anchor);
        if (bounds.IsEmpty || bounds.Width < 1 || bounds.Height < 1) return;

        double preferredLeft = anchor.TranslatePoint(new Point(anchor.ActualWidth + 6, 0), root).X;
        double chromeWidth = KindMenuCard.Padding.Left + KindMenuCard.Padding.Right + KindMenuCard.BorderThickness.Left + KindMenuCard.BorderThickness.Right;
        double chromeHeight = KindMenuCard.Padding.Top + KindMenuCard.Padding.Bottom + KindMenuCard.BorderThickness.Top + KindMenuCard.BorderThickness.Bottom;
        double height = Math.Max(1, bounds.Height - chromeHeight);
        double availableRight = bounds.Right - Math.Max(bounds.Left, preferredLeft);
        double singleMinimum = chromeWidth + LibraryScrollWidth + LibraryMinColumnWidth;
        double singleBudget = availableRight >= singleMinimum ? availableRight : bounds.Width;
        double columnWidth = Math.Clamp(Math.Floor(singleBudget - chromeWidth - LibraryScrollWidth), 1, LibraryColumnWidth);
        int split = _libraryEntries.Length;
        double neededHeight = MeasureLibraryRange(MeasureLibraryItems(columnWidth), 0, split);

        double twoMinimum = singleMinimum + LibraryMinColumnWidth + LibraryColumnGap;
        if (neededHeight > height && bounds.Width >= twoMinimum && split > 1)
        {
            double twoBudget = availableRight >= twoMinimum ? availableRight : bounds.Width;
            columnWidth = Math.Min(LibraryColumnWidth, Math.Floor((twoBudget - chromeWidth - LibraryScrollWidth - LibraryColumnGap) / 2));
            var itemHeights = MeasureLibraryItems(columnWidth);
            (split, neededHeight) = ChooseLibrarySplit(itemHeights, height);
        }

        bool twoColumns = split < _libraryEntries.Length;
        bool scroll = neededHeight > height;
        double width = chromeWidth + columnWidth + (twoColumns ? LibraryColumnGap + columnWidth : 0) + (scroll ? LibraryScrollWidth : 0);
        KindMenuLeft.Width = columnWidth;
        KindMenuRight.Width = columnWidth;
        KindMenuRight.Visibility = twoColumns ? Visibility.Visible : Visibility.Collapsed;
        KindMenu.ColumnDefinitions[1].Width = new GridLength(twoColumns ? LibraryColumnGap : 0);
        KindMenuCard.Width = width;
        KindMenuScroll.MaxHeight = height;
        KindMenuScroll.VerticalScrollBarVisibility = scroll ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        System.Windows.Controls.Canvas.SetLeft(KindMenuCard, Math.Clamp(preferredLeft, bounds.Left, Math.Max(bounds.Left, bounds.Right - width)));
        System.Windows.Controls.Canvas.SetTop(KindMenuCard, bounds.Top);

        if (split == _librarySplit) return;
        _librarySplit = split;
        KindMenuLeft.Children.Clear();
        KindMenuRight.Children.Clear();
        PopulateLibraryColumn(KindMenuLeft, 0, split);
        PopulateLibraryColumn(KindMenuRight, split, _libraryEntries.Length);
    }

    private double[] MeasureLibraryItems(double width)
    {
        var heights = new double[_libraryEntries.Length];
        for (int i = 0; i < _libraryEntries.Length; i++)
        {
            var button = _libraryEntries[i].Button;
            button.Measure(new Size(width, double.PositiveInfinity));
            heights[i] = button.DesiredSize.Height;
        }
        return heights;
    }

    private bool StartsLibraryGroup(int index) => index == 0 || _libraryEntries[index].Group != _libraryEntries[index - 1].Group;

    private double MeasureLibraryRange(double[] heights, int first, int end)
    {
        double total = 0;
        for (int i = first; i < end; i++)
            total += heights[i] + (i == first || StartsLibraryGroup(i) ? LibraryHeadingHeight : 0);
        return total;
    }

    private (int Split, double Height) ChooseLibrarySplit(double[] heights, double availableHeight)
    {
        int split = 1;
        var best = (Fits: 1, GroupBreak: 1, Height: double.PositiveInfinity);
        for (int i = 1; i < heights.Length; i++)
        {
            double height = Math.Max(MeasureLibraryRange(heights, 0, i), MeasureLibraryRange(heights, i, heights.Length));
            bool fits = height <= availableHeight;
            var rank = (Fits: fits ? 0 : 1, GroupBreak: fits && !StartsLibraryGroup(i) ? 1 : 0, Height: height);
            if (rank.CompareTo(best) >= 0) continue;
            best = rank;
            split = i;
        }
        return (split, best.Height);
    }

    private void PopulateLibraryColumn(StackPanel column, int first, int end)
    {
        for (int i = first; i < end; i++)
        {
            var entry = _libraryEntries[i];
            if (i == first || StartsLibraryGroup(i))
            {
                var heading = new TextBlock { Text = entry.Group, Height = LibraryHeadingHeight, FontSize = 11,
                    FontWeight = FontWeights.SemiBold, Padding = new Thickness(7, 4, 0, 0) };
                heading.SetResourceReference(TextBlock.ForegroundProperty, "DesktopAccent");
                AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
                column.Children.Add(heading);
            }
            column.Children.Add(entry.Button);
        }
    }

    private void KindMenuCard_MouseEnter(object sender, MouseEventArgs e) => _closeLibraryTimer?.Stop();
    private void KindMenuCard_MouseLeave(object sender, MouseEventArgs e) => ScheduleLibraryClose();

    private void ScheduleLibraryClose()
    {
        if (_libraryAnchor == null) return;
        if (_closeLibraryTimer == null)
        {
            _closeLibraryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(325) };
            _closeLibraryTimer.Tick += (_, _) =>
            {
                _closeLibraryTimer.Stop();
                if (_libraryAnchor?.IsMouseOver == true || KindMenuCard.IsMouseOver || KindMenuCard.IsKeyboardFocusWithin) return;
                CloseLibraryMenu();
            };
        }
        _closeLibraryTimer.Stop();
        _closeLibraryTimer.Start();
    }

    private void CloseLibraryOnOutsideClick(object sender, MouseButtonEventArgs e)
    {
        if (_libraryAnchor is not { } anchor) return;
        if (e.OriginalSource is Visual source && (ReferenceEquals(source, anchor) || anchor.IsAncestorOf(source)
            || ReferenceEquals(source, KindMenuCard) || KindMenuCard.IsAncestorOf(source))) return;
        CloseLibraryMenu();
    }

    private void CloseLibraryMenu()
    {
        _closeLibraryTimer?.Stop();
        _libraryAnchor = null;
        MenuLayer.Visibility = Visibility.Collapsed;
    }
}
