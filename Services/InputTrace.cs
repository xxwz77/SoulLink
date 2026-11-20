using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace SoulLink.Services
{
    /// <summary>
    /// 注入通道时序诊断（**默认关闭**）。
    ///
    /// ★ 历史：这一套埋点是 2.17.16 定案「代理建连约 2 秒后被宿主断开」时加的，
    ///   结论已落定（根因是 BeginInputCore 里按 InputAllowed 主动 StopInputAsync），
    ///   埋点随之改为默认关闭 —— 正常使用不写任何诊断日志。
    ///
    /// 需要重新取证时：启动加 --input-trace，或设环境变量 SOULLINK_INPUT_TRACE=1。
    /// 日志位置：%LOCALAPPDATA%\SoulLink\input-trace-yyyyMMdd.log
    ///
    /// ★ 约束（务必遵守）：本类只**记录**，不参与控制流；
    ///   且传入的字符串参数必须是纯值拼接，**不得**在参数里读 UI 属性
    ///   （IsVisible / Window.IsActive 等只在 UI 线程可读，后台线程读会抛异常，
    ///     曾导致一次成功的投送被误判为失败）。
    /// </summary>
    public static class InputTrace
    {
        private const string Prefix = "INPUTTRACE";

        /// <summary>是否启用诊断（默认关闭）。</summary>
        public static bool Enabled { get; } = Detect();

        private static bool Detect()
        {
            try
            {
                if (Environment.GetEnvironmentVariable("SOULLINK_INPUT_TRACE") == "1") return true;
                foreach (var a in Environment.GetCommandLineArgs())
                    if (a != null && a.Equals("--input-trace", StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }
            return false;
        }

        public static void Write(string tag, string message)
        {
            // ★ 默认关闭：不做任何字符串拼接之外的额外开销，也不写文件。
            if (!Enabled) return;
            try
            {
                AsyncLog.Append(
                    ConfigStore.DailyLogPath("input-trace"),
                    DateTime.Now.ToString("HH:mm:ss.fff")
                        + "  [" + Environment.CurrentManagedThreadId.ToString("D3") + "]"
                        + "  " + Prefix + " " + (tag ?? "?") + " | " + message
                        + Environment.NewLine);
            }
            catch { }
        }

        /// <summary>
        /// 抓调用栈，用来回答「到底是谁调用了 StopInputAsync」。
        /// 跳过自身与 async 状态机帧（MoveNext 会把真实调用方埋掉）。
        /// </summary>
        public static string Caller(int skip = 2, int take = 7)
        {
            // ★ 关着就直接返回：抓栈（StackTrace）在热路径上并不便宜。
            if (!Enabled) return "-";
            try
            {
                var frames = new StackTrace(skip, false).GetFrames();
                var names = new List<string>();
                if (frames != null)
                {
                    foreach (var f in frames)
                    {
                        string n = f.GetMethod()?.Name ?? "?";
                        if (n == "Caller" || n == "MoveNext" || n == "Start") continue;
                        names.Add(n);
                        if (names.Count >= take) break;
                    }
                }
                return names.Count == 0 ? "?" : string.Join(" < ", names);
            }
            catch { return "?"; }
        }
    }
}
