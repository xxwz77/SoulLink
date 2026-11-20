using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SoulLink;
using SoulLink.Services;

/// <summary>
/// 复现并验证 ProxyHost 并发缺陷：
/// A2/A3 在同一轮会话监控里并行投送，会同时生成同一个 SoulLink.proxy.exe，
/// 旧实现抛 "being used by another process"，导致只投上一个通道。
///
/// 用法：ProxyRaceCheck.exe [--live]
///   默认：只并发调用 ProxyHost.Resolve()（快，不投送）
///   --live：并发做真实投送，验证两个账号都能拿到通道
/// </summary>
public static class ProxyRaceCheck
{
    private static int _fails;
    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + name);
        if (!ok) _fails++;
    }

    public static int Main(string[] args)
    {
        bool live = args.Any(a => a.Equals("--live", StringComparison.OrdinalIgnoreCase));

        // ── 1) 纯并发生成代理：这是崩溃点的最小复现 ──────────────
        // 先删掉缓存与磁盘副本，强制所有线程重新生成
        ProxyHost.Invalidate();
        string proxyDir = Path.Combine(AppIdentity.TempRoot, "proxy");
        try
        {
            if (Directory.Exists(proxyDir))
                foreach (var f in Directory.GetFiles(proxyDir, "*.proxy.exe"))
                { try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); } catch { } }
            Console.WriteLine("已清空旧代理副本，强制重建");
        }
        catch (Exception ex) { Console.WriteLine("清理副本失败（继续）：" + ex.Message); }

        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        var results = new System.Collections.Concurrent.ConcurrentBag<string>();
        int threads = 4;
        var barrier = new Barrier(threads);
        var tasks = Enumerable.Range(0, threads).Select(_ => Task.Run(() =>
        {
            barrier.SignalAndWait();          // 尽量让它们同时冲进 Resolve
            try { results.Add(ProxyHost.Resolve() ?? "(null)"); }
            catch (Exception ex) { errors.Add(ex.Message); }
        })).ToArray();
        Task.WaitAll(tasks);

        Console.WriteLine("并发 " + threads + " 次 Resolve：成功 " + results.Count + " / 异常 " + errors.Count);
        foreach (var e in errors) Console.WriteLine("  异常：" + e);
        Check(errors.IsEmpty, "并发生成代理不抛异常（原缺陷：" + errors.Count + " 个失败）");
        Check(results.Count == threads, "所有并发调用都拿到代理路径");
        Check(results.Distinct().Count() <= 1, "所有线程拿到同一个代理路径");
        Check(results.All(File.Exists), "返回的代理文件确实存在");

        if (!live)
        {
            Console.WriteLine("提示：加 --live 做真实并发投送。");
            Console.WriteLine(_fails == 0 ? "RESULT=OK 全部通过" : "RESULT=FAIL 失败 " + _fails + " 项");
            return _fails;
        }

        // ── 2) 真实并发投送：两个账号必须都拿到通道 ──────────────
        Console.WriteLine();
        Console.WriteLine("=== 并发投送 A2 + A3 ===");
        var sessions = SessionService.QueryAsync().GetAwaiter().GetResult();
        var targets = new[] { "A2", "A3" }
            .Select(u => new { User = u, Session = SessionService.Find(sessions, u) })
            .Where(x => x.Session != null && x.Session.Id > 0)
            .ToList();
        if (targets.Count == 0) { Console.WriteLine("FAIL 没有可用会话"); return 2; }

        var prev = App.Config.Settings.UseBrokerChannel;
        App.Config.Settings.UseBrokerChannel = true;
        var relays = new System.Collections.Concurrent.ConcurrentDictionary<string, RelayClient>();
        var deployBarrier = new Barrier(targets.Count);
        var deployResults = new System.Collections.Concurrent.ConcurrentDictionary<string, (bool ok, string msg)>();

        var deployTasks = targets.Select(t => Task.Run(async () =>
        {
            var relay = new RelayClient(t.User, t.Session!.Id);
            relays[t.User] = relay;
            deployBarrier.SignalAndWait();    // 模拟同一轮扫描里同时发起
            try
            {
                var (ok, msg) = await relay.DeployAsync(
                    new SoulLink.Models.Account { UserName = t.User }, 30000);
                deployResults[t.User] = (ok, msg);
            }
            catch (Exception ex) { deployResults[t.User] = (false, "异常：" + ex.Message); }
        })).ToArray();
        Task.WaitAll(deployTasks);

        foreach (var t in targets)
        {
            (bool ok, string msg) r = deployResults.TryGetValue(t.User, out var v) ? v : (false, "无结果");
            Console.WriteLine(t.User + " 会话 " + t.Session!.Id + "：" + (r.ok ? "成功" : "失败") + " · " + r.msg);
        }
        Check(targets.All(t =>
              {
                  if (!deployResults.TryGetValue(t.User, out var v)) return false;
                  return v.ok;
              }),
              "并发投送时每个账号都拿到通道（原缺陷：只成功一个）");

        var liveSet = BgiLogWatcher.DiscoverLiveAgentSessions();
        Console.WriteLine("live 通道会话 = {" + string.Join(",", liveSet.OrderBy(x => x)) + "}");
        foreach (var t in targets)
            Check(liveSet.Contains(t.Session!.Id), t.User + " 的通道被界面识别");

        foreach (var r in relays.Values) { try { r.Dispose(); } catch { } }
        App.Config.Settings.UseBrokerChannel = prev;
        Thread.Sleep(1000);

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "RESULT=OK 全部通过" : "RESULT=FAIL 失败 " + _fails + " 项");
        return _fails;
    }
}
