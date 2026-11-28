using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using SoulLink.Controls;
using SoulLink.Models;
using SoulLink.Services;

namespace SoulLink.Views;

public partial class FlowEditorWindow
{
    private string _savedDefinition = "";
    private bool _hasSavedFlow;
    private bool _closeWithoutPrompt;
    private bool _showFlowGuide;
    private bool _initialShowFlowGuide;
    private (string Owner, string End, bool Before, bool Want)? _todayEdit;
    private readonly Dictionary<string, string> _draftBgiPaths = new(StringComparer.OrdinalIgnoreCase);

    private void BeginFlowDraft(AutomationFlow source, bool isNew)
    {
        _hasSavedFlow = !isNew;
        _showFlowGuide = _initialShowFlowGuide = _cfg.Settings.ShowFlowGuide;
        _savedDefinition = FlowEdits.DefinitionOf(_flow);
        FlowRunner.TryEditIdleFlow(_flow.Id, () => FlowEdits.ReadRuntime(_flow, _hasSavedFlow ? source : null, DateTime.Now, null));
    }

    private AutomationFlow? CurrentSavedFlow() => _cfg.Automations.Find(flow => flow.Id == _flow.Id);

    private bool? PendingToday => _todayEdit is { } change && change.Owner == _flow.AccountId
        && change.End == _flow.EndNode?.Id ? change.Want : null;

    private bool HasFlowEdits => PendingToday.HasValue || _draftBgiPaths.Count != 0
        || FlowEdits.DefinitionOf(_flow) != _savedDefinition;

    private void StageToday(FlowNode end, bool want, DateTime now)
    {
        bool before = PendingToday.HasValue && _todayEdit is { } previous
            ? previous.Before : !PlcLogic.IsDailyComplete(_flow, now);
        _todayEdit = want == before ? null : (_flow.AccountId, end.Id, before, want);
        SyncDraftRuntime();
        // The editor shows the pending choice even while an active scan owns the saved flow.
        PlcLogic.SetTodayRun(_flow, now, want);
    }

    private void SyncDraftRuntime()
    {
        var now = DateTime.Now;
        var live = CurrentSavedFlow();
        if (live == null || !FlowEdits.SameOwner(_flow, live))
        {
            FlowEdits.ReadRuntime(_flow, live, now, PendingToday);
            return;
        }
        FlowRunner.TryEditIdleFlow(_flow.Id, () => FlowEdits.ReadRuntime(_flow, CurrentSavedFlow(), now, PendingToday));
    }

    private bool ApplyGuidePreference()
    {
        if (_showFlowGuide == _initialShowFlowGuide) return false;
        _cfg.Settings.ShowFlowGuide = _initialShowFlowGuide = _showFlowGuide;
        return true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _closeWithoutPrompt) return;
        if (!HasFlowEdits)
        {
            if (ApplyGuidePreference()) ConfigStore.Save(_cfg);
            return;
        }

        var choice = ThemedMessage.YesNoCancel(this, "保存", "有未保存的修改，要保存吗？");
        e.Cancel = choice == MessageBoxResult.Cancel || (choice == MessageBoxResult.Yes && !SaveFlow(showMessage: false));
    }

    private FlowValidator.Report ValidateDraft()
        => FlowValidator.Validate(_flow, _cfg, user => _draftBgiPaths.GetValueOrDefault(user));

    private bool SaveFlow(bool showMessage = true)
    {
        try
        {
            _flow.Name = _flow.DisplayName.Trim();
            _flow.Enabled = EnabledCheck.IsChecked == true;
            var report = ValidateDraft();
            if (report.HasError)
            {
                ThemedMessage.Info(this, "流程不完整", report.ToText());
                return false;
            }

            var saved = CurrentSavedFlow();
            bool changed = !_hasSavedFlow || HasFlowEdits ||
                (_flow.AutoName == true && saved?.Name != _flow.Name);
            if (changed && !FlowRunner.TryEditIdleFlow(_flow.Id, CommitFlowDraft))
            {
                ThemedMessage.Info(this, "暂时无法保存", "流程正在执行，完成后可以保存。");
                return false;
            }
            if (!changed && CurrentSavedFlow() == null) throw new InvalidOperationException("流程已删除。");

            bool guideChanged = ApplyGuidePreference();
            if (changed || guideChanged) ConfigStore.Save(_cfg);
            SyncDraftRuntime();
            RefreshDailyState();
            if (showMessage) ThemedMessage.Info(this, "提示", "流程已保存。"
                + (report.Issues.Count > 0 ? "\n" + report.ToText() : ""));
            return true;
        }
        catch (Exception ex)
        {
            ThemedMessage.Info(this, "保存失败", ex.Message);
            return false;
        }
    }

    private void CommitFlowDraft()
    {
        int index = _cfg.Automations.FindIndex(flow => flow.Id == _flow.Id);
        if (index < 0 && _hasSavedFlow) throw new InvalidOperationException("流程已删除。");
        var live = index < 0 ? null : _cfg.Automations[index];
        var saved = FlowEdits.Commit(_flow, live, DateTime.Now, PendingToday);
        foreach (var path in _draftBgiPaths) BgiDataCenter.SetPath(path.Key, path.Value, save: false);
        if (index < 0) _cfg.Automations.Add(saved);
        else _cfg.Automations[index] = saved;
        _hasSavedFlow = true;
        _savedDefinition = FlowEdits.DefinitionOf(_flow);
        _todayEdit = null;
        _draftBgiPaths.Clear();
    }

    private string DraftBgiPath(string user) => _draftBgiPaths.GetValueOrDefault(user) ?? BgiPathResolver.GetPath(user);

    private bool IsDraftBgiBound(string user) => _draftBgiPaths.ContainsKey(user) || BgiPathResolver.IsPinned(user);

    private BgiAccountData DraftBgiData(string user) => _draftBgiPaths.TryGetValue(user, out var path)
        ? BgiDataCenter.ReadFiles(user, path) : BgiDataCenter.Get(user);

    private void StageBgiPath(string user, string path)
    {
        string normalized = BgiPathResolver.Normalize(path);
        if (BgiPathResolver.IsPinned(user)
            && string.Equals(BgiPathResolver.GetPath(user), normalized, StringComparison.OrdinalIgnoreCase))
            _draftBgiPaths.Remove(user);
        else _draftBgiPaths[user] = normalized;
    }
}
