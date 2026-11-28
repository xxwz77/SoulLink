using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SoulLink.Services;

namespace SoulLink.Views;

public partial class OverviewPage
{
    private sealed record MoraPartRow(string Label, string? Icon, string Tint, string Summary, string Share,
        double Progress, string EliteTiers);
    private sealed record MoraEntryRow(string Time, string Source, string Amount);

    private static readonly (string Label, string? Icon, string Tint)[] MoraPartStyles =
    [
        ("精英怪", "mask_red", "#D9A43A"),
        ("小怪", "slime_purple", "#9B7BD8"),
        ("宝箱", "chest", "#E8A23D"),
        ("罐子收益", null, "#C47A50"),
        ("其他收益", "book", "#6C8EBF")
    ];
    private string? _moraPopupAccount;
    private DailyMoraLedger.Composition? _moraPopupData;
    private Button? _moraCompositionAnchor;
    private Window? _moraCompositionOwner;
    private DispatcherOperation? _moraCompositionLayout;
    private bool IsMoraCompositionOpen => MoraCompositionOverlay.Visibility == Visibility.Visible;

    private void OpenMoraComposition_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: AccountRow row } button) return;
        _moraPopupAccount = row.Id;
        _moraCompositionAnchor = button;
        MoraCompositionOverlay.Visibility = Visibility.Visible;
        _moraCompositionOwner = Window.GetWindow(this);
        if (_moraCompositionOwner != null)
        {
            _moraCompositionOwner.PreviewMouseDown += MoraCompositionOwner_PreviewMouseDown;
            _moraCompositionOwner.Deactivated += MoraCompositionOwner_Deactivated;
        }
        RenderMoraComposition();
        PositionMoraComposition();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (IsMoraCompositionOpen) CloseMoraCompositionButton.Focus();
        }));
        if (_moraTotalsDay != DogfoodRecordReader.CurrentBusinessDay()) _ = RefreshMoraTotalsAsync();
    }

    private void RenderMoraComposition(bool readFinished = false)
    {
        var account = GetOverviewAccounts(App.Config).FirstOrDefault(item => item.Id == _moraPopupAccount);
        if (account == null)
        {
            CloseMoraComposition(restoreFocus: false);
            return;
        }

        var day = DogfoodRecordReader.CurrentBusinessDay();
        var composition = _moraTotalsDay == day ? _moraCompositions.GetValueOrDefault(MoraAccount(account)) : null;
        _moraPopupData = composition;
        MoraCompositionSubtitle.Text = $"{account.Caption} · 业务日 {day:yyyy-MM-dd}（04:00 换日）";
        if (composition == null)
        {
            MoraCompositionTotal.Text = _moraTotalsDay == day ? "今日暂无本地摩拉数据"
                : readFinished ? "本地摩拉数据读取失败" : "正在读取本地摩拉数据…";
            MoraCompositionParts.ItemsSource = null;
            MoraCompositionNote.Text = "";
            MoraCompositionDetails.Visibility = Visibility.Collapsed;
            MoraCompositionEntries.ItemsSource = null;
            return;
        }

        var total = composition.Total;
        MoraCompositionTotal.Text = total == null
            ? $"{composition.Entries.Count:N0} 条来源记录"
            : $"{total.Amount:N0} 摩拉{(total.IsPartial ? "（部分）" : "")} · {composition.Entries.Count:N0} 条来源记录";
        string eliteTiers = string.Join("  ", composition.EliteTiers.Select(tier => $"{tier.Value}×{tier.RecordCount}"));
        MoraCompositionParts.ItemsSource = composition.Parts
            .Where(part => part.Kind != DailyMoraLedger.IncomeKind.Unknown)
            .OrderBy(part => part.Kind switch
            {
                DailyMoraLedger.IncomeKind.Jar => DailyMoraLedger.IncomeKind.Chest,
                DailyMoraLedger.IncomeKind.Chest => DailyMoraLedger.IncomeKind.Jar,
                _ => part.Kind
            })
            .Select(part => FormatMoraPart(part, total?.Amount ?? 0,
                part.Kind == DailyMoraLedger.IncomeKind.Elite ? eliteTiers : "")).ToArray();

        var notes = new List<string>();
        var unknown = composition.Parts.Single(part => part.Kind == DailyMoraLedger.IncomeKind.Unknown);
        if (unknown.RecordCount > 0) notes.Add($"未分类 {unknown.Amount:N0} 摩拉 · {unknown.RecordCount:N0} 条");
        if (composition.MissingAmountCount > 0) notes.Add($"{composition.MissingAmountCount:N0} 条流水缺少金额");
        if (total is { IsPartial: false })
        {
            decimal gap = (decimal)total.Amount - composition.RecordedAmount;
            if (gap > 0) notes.Add($"札记汇总中有 {gap:N0} 摩拉缺少本地流水明细");
            if (gap < 0) notes.Add($"本地流水比札记汇总多 {-gap:N0} 摩拉");
        }
        MoraCompositionNote.Text = string.Join("。", notes);
        MoraCompositionDetails.Visibility = composition.Entries.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (MoraCompositionDetails.IsExpanded) PopulateMoraDetails();
    }

    private static MoraPartRow FormatMoraPart(DailyMoraLedger.IncomePart part, long total, string eliteTiers)
    {
        var (label, icon, tint) = MoraPartStyles[(int)part.Kind];
        double share = total > 0 ? 100.0 * part.Amount / total : 0;
        string count = part.MonsterDailyLimit is int limit
            ? $"{part.MonsterCount:N0} 只" + (part.MonsterCountInvalid ? $"（数据错误，日上限 {limit:N0} 只）" : "")
            : $"{part.RecordCount:N0} 条";
        return new(label, icon == null ? null : $"pack://application:,,,/Assets/Mora/{icon}.png", tint,
            $"{part.Amount:N0} 摩拉 · {count}",
            total > 0 ? $"{share:N2}%" : "—", Math.Clamp(share, 0, 100), eliteTiers);
    }

    private void MoraCompositionDetails_Expanded(object sender, RoutedEventArgs e) => PopulateMoraDetails();

    private void PopulateMoraDetails()
    {
        MoraCompositionEntries.ItemsSource = _moraPopupData?.Entries.Select(entry => new MoraEntryRow(
            DateTimeOffset.FromUnixTimeMilliseconds(entry.TimeMs).LocalDateTime.ToString("MM-dd HH:mm"),
            entry.RouteName != null ? $"罐子收益 · {entry.RouteName}" : entry.Action.Length == 0 ? "来源缺失" : entry.Action,
            entry.Amount is long amount ? $"{amount:N0} 摩拉" : "金额缺失")).ToArray();
    }

    internal static (double Left, double Top, double Width, double Height) PlaceMoraComposition(
        double viewportWidth, double viewportHeight, double anchorLeft, double anchorTop,
        double anchorBottom, double desiredHeight)
    {
        const double inset = 8;
        const double gap = 5;
        double minLeft = Math.Min(inset, viewportWidth / 4);
        double minTop = Math.Min(inset, viewportHeight / 4);
        double width = Math.Min(342, viewportWidth - 2 * minLeft);
        double height = Math.Min(desiredHeight, viewportHeight - 2 * minTop);
        double maxLeft = Math.Max(minLeft, viewportWidth - minLeft - width);
        double maxTop = Math.Max(minTop, viewportHeight - minTop - height);
        double below = anchorBottom + gap;
        double above = anchorTop - gap - height;
        double top = below <= maxTop ? below : above >= minTop ? above : below;
        return (Math.Clamp(anchorLeft, minLeft, maxLeft), Math.Clamp(top, minTop, maxTop), width, height);
    }

    private void PositionMoraComposition()
    {
        if (_moraCompositionAnchor is not { } anchor || !IsMoraCompositionOpen) return;
        if (!anchor.IsDescendantOf(OverviewRoot))
        {
            CloseMoraComposition(restoreFocus: false);
            return;
        }
        double width = OverviewRoot.ActualWidth;
        double height = OverviewRoot.ActualHeight;
        if (width <= 0 || height <= 0) return;
        var anchorTop = anchor.TranslatePoint(new Point(), OverviewRoot);
        var anchorBottom = anchor.TranslatePoint(new Point(0, anchor.ActualHeight), OverviewRoot);
        var limits = PlaceMoraComposition(width, height, anchorTop.X, anchorTop.Y, anchorBottom.Y, double.PositiveInfinity);
        MoraCompositionPanel.Width = limits.Width;
        MoraCompositionPanel.MaxHeight = limits.Height;
        MoraCompositionPanel.Measure(new Size(limits.Width, limits.Height));
        var placement = PlaceMoraComposition(width, height, anchorTop.X, anchorTop.Y,
            anchorBottom.Y, MoraCompositionPanel.DesiredSize.Height);
        Canvas.SetLeft(MoraCompositionPanel, placement.Left);
        Canvas.SetTop(MoraCompositionPanel, placement.Top);
    }

    private void MoraCompositionLayout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_moraCompositionAnchor == null || !IsMoraCompositionOpen || _moraCompositionLayout != null) return;
        _moraCompositionLayout = Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            _moraCompositionLayout = null;
            PositionMoraComposition();
        }));
    }

    private void CloseMoraComposition_Click(object sender, RoutedEventArgs e) => CloseMoraComposition(restoreFocus: true);

    private void MoraCompositionOutside_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        CloseMoraComposition(restoreFocus: true);
    }

    private void MoraCompositionOwner_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var position = e.GetPosition(MoraCompositionOverlay);
        if (position.X < 0 || position.Y < 0 || position.X >= MoraCompositionOverlay.ActualWidth ||
            position.Y >= MoraCompositionOverlay.ActualHeight)
            CloseMoraComposition(restoreFocus: false);
    }

    private void MoraCompositionOwner_Deactivated(object? sender, EventArgs e)
        => CloseMoraComposition(restoreFocus: false);

    private void MoraComposition_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        CloseMoraComposition(restoreFocus: true);
    }

    private void CloseMoraComposition(bool restoreFocus)
    {
        if (!IsMoraCompositionOpen) return;
        var anchor = _moraCompositionAnchor;
        if (_moraCompositionOwner != null)
        {
            _moraCompositionOwner.PreviewMouseDown -= MoraCompositionOwner_PreviewMouseDown;
            _moraCompositionOwner.Deactivated -= MoraCompositionOwner_Deactivated;
            _moraCompositionOwner = null;
        }
        _moraCompositionAnchor = null;
        _moraCompositionLayout?.Abort();
        _moraCompositionLayout = null;
        MoraCompositionOverlay.Visibility = Visibility.Collapsed;
        _moraPopupAccount = null;
        _moraPopupData = null;
        MoraCompositionDetails.IsExpanded = false;
        MoraCompositionParts.ItemsSource = null;
        MoraCompositionEntries.ItemsSource = null;
        MoraCompositionScroll.ScrollToTop();
        if (restoreFocus && anchor?.IsVisible == true) anchor.Focus();
    }
}
