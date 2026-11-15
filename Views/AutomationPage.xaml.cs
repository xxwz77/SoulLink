using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SoulLink.Models;
using SoulLink.Services;

namespace SoulLink.Views
{
 public partial class AutomationPage : UserControl
 {
  private AutomationProfile? _current;
  private bool _busy;
  public AutomationPage() { InitializeComponent(); }
  public void Activate()
  {
   ProfileList.ItemsSource = App.Config.AutomationProfiles.Select(p => new { p.Id, p.Name, Summary = (p.Enabled ? "每天 " + p.DailyTime : "手动") + " · " + p.AccountIds.Count + " 用户 · " + p.ActionIds.Count + " 步骤" }).ToList();
   if (_current != null) ProfileList.SelectedItem = ProfileList.Items.Cast<object>().FirstOrDefault(x => (string)x.GetType().GetProperty("Id")!.GetValue(x)! == _current.Id);
  }
  private void New_Click(object sender, RoutedEventArgs e) { _current = new AutomationProfile(); Load(_current); }
  private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (ProfileList.SelectedItem == null) return; string id = (string)ProfileList.SelectedItem.GetType().GetProperty("Id")!.GetValue(ProfileList.SelectedItem)!; _current = App.Config.AutomationProfiles.FirstOrDefault(p => p.Id == id); if (_current != null) Load(_current); }
  private void Load(AutomationProfile profile)
  {
   ProfileName.Text = profile.Name; ScheduleEnabled.IsChecked = profile.Enabled; ScheduleTime.Text = profile.DailyTime;
   AccountChoices.Items.Clear(); foreach (var account in App.Config.Accounts.OrderBy(a => a.SortOrder)) { var check = new CheckBox { Content = account.Caption + " · " + account.UserName, IsChecked = profile.AccountIds.Contains(account.Id), Tag = account.Id, Style = (Style)FindResource("GlassCheckBox"), Margin = new Thickness(0,4,0,4) }; AccountChoices.Items.Add(check); }
   ActionChoices.Items.Clear(); foreach (var action in App.Config.Actions) { var check = new CheckBox { Content = action.Name, IsChecked = profile.ActionIds.Contains(action.Id), Tag = action.Id, Style = (Style)FindResource("GlassCheckBox"), Margin = new Thickness(0,4,0,4) }; ActionChoices.Items.Add(check); }
   StatusText.Text = "配置仅保存于 SoulLink，不依赖 autoBGI。默认不启用定时任务。";
  }
  private void Save_Click(object sender, RoutedEventArgs e) => Save();
  private bool Save()
  {
   if (_current == null) _current = new AutomationProfile();
   string name = ProfileName.Text.Trim(); if (name.Length == 0) { StatusText.Text = "请填写配置组名称。"; return false; }
   if (!TimeSpan.TryParseExact(ScheduleTime.Text.Trim(), @"hh\:mm", null, out _)) { StatusText.Text = "定时格式应为 HH:mm，例如 08:00。"; return false; }
   _current.Name = name; _current.Enabled = ScheduleEnabled.IsChecked == true; _current.DailyTime = ScheduleTime.Text.Trim();
   _current.AccountIds = AccountChoices.Items.Cast<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();
   _current.ActionIds = ActionChoices.Items.Cast<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();
   if (!App.Config.AutomationProfiles.Contains(_current)) App.Config.AutomationProfiles.Add(_current);
   ConfigStore.Save(App.Config); Activate(); StatusText.Text = "已保存「" + name + "」。"; return true;
  }
  private async void Run_Click(object sender, RoutedEventArgs e)
  {
   if (_busy || !Save()) return;
   if (!Dialogs.Confirm("将按列表顺序执行已选用户的动作，确认启动这条一条龙吗？", "启动一条龙")) return;
   _busy = true; BtnRunProfile.IsEnabled = false;
   try { await AutomationRunner.RunAsync(App.Config, _current!, StatusText); }
   finally { _busy = false; BtnRunProfile.IsEnabled = true; }
  }
  private void Delete_Click(object sender, RoutedEventArgs e)
  {
   if (_current == null || !App.Config.AutomationProfiles.Contains(_current)) return;
   if (!Dialogs.Confirm("确定删除配置组「" + _current.Name + "」吗？只删除配置，不会操作用户程序。", "删除配置组", true)) return;
   App.Config.AutomationProfiles.Remove(_current); _current = null; ConfigStore.Save(App.Config); Activate(); ProfileName.Clear(); StatusText.Text = "已删除配置组。";
  }
 }
}