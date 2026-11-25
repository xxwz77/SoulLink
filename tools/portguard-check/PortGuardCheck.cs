using System;
using System.Net;
using System.Net.NetworkInformation;
using SoulLink.Services;

/// <summary>
/// 端口冲突回归测试（t41）—— 守门「内部端口只试绑回环」的静默复发。
///
/// 背景（t36 修复的真实 bug，因果链）：
///   主程序内部回环端口起点曾是 WebPort+1；WebDirectToAgent=true 时代理按
///   WebPort+账号序号+1 绑定，序号 0 恰好也是 WebPort+1。Windows 允许
///   0.0.0.0:19081 与 127.0.0.1:19081 共存 → 主程序绑成功但请求全被代理接走，
///   表现是「输任何口令都 401」。表现（401）与根因（端口冲突）毫无直觉关联，
///   一旦判定被改回「只试绑回环」，bug 会静默复发且难以联想 —— 必须有测试守门。
///
/// 修复两道（见 Services/WebServer.cs 第 49-90 行，本测试不改它、只验证它）：
///   ① 起点越过代理保留段 WebPort+62（保留跨度 WebDirectReservedSpan=61）；
///   ② 候选判定用「整机无任何监听者（IPv4+IPv6）」+「能绑回环」，
///      而不是只试绑回环。
/// </summary>
public static class PortGuardCheck
{
    static int fails;

    static void Check(bool ok, string s)
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + s);
        if (!ok) fails++;
    }

    static HttpListener OccupyWildcard(int port)
    {
        var l = new HttpListener();
        l.Prefixes.Add("http://+:" + port + "/");
        l.Start();
        return l;
    }

    public static int Main()
    {
        Console.WriteLine("=== PortGuardCheck：内部端口选择不得与代理保留段重叠 ===");

        const int WebPort = 19080;
        const int ReservedLo = WebPort + 1;      // 19081
        const int ReservedHi = WebPort + 61;     // 19141

        // ── 断言1：选出的内部端口不在代理保留段 [WebPort+1, WebPort+61] ──
        int r1 = WebServer.ResolveInternalPort(WebPort);
        Check(r1 != 0, "断言1a: ResolveInternalPort(" + WebPort + ") 找到可用端口（=" + r1 + "，0=找不到）");
        Check(r1 < ReservedLo || r1 > ReservedHi,
              "断言1b: 选中端口 " + r1 + " 不在代理保留段 [" + ReservedLo + ", " + ReservedHi + "]");

        // ── 断言4：连续两次选择返回同一端口（幂等/稳定）──
        int r2 = WebServer.ResolveInternalPort(WebPort);
        Check(r2 == r1, "断言4: 连续两次选择返回同一端口（" + r1 + " vs " + r2 + "）");

        // ── 断言3（本 bug 的核心）：通配地址被占但回环可绑 → 必须跳过 ──
        // 用真实 HttpListener 绑 http://+:port/ 复现代理占用（0.0.0.0 通配）。
        // ★ 只有 HttpListener 通配 + HttpListener 回环才会「共存」（Windows 特性）；
        //   若用 TcpListener 占 0.0.0.0，回环 HttpListener 反而绑不上 —— 不构成该 bug。
        if (r1 != 0 && r1 <= 65535)
        {
            HttpListener wildcard = null;
            try
            {
                wildcard = OccupyWildcard(r1);
                bool loopbackStillBindable = WebServer.CanBindLoopback(r1);
                bool anywhereListening = WebServer.IsAnywhereListening(r1);
                bool freeEverywhere = WebServer.IsPortFreeEverywhere(r1);

                // 铁证：回环仍可绑（说明「只试绑回环」会被骗过），但整机判定正确拒绝。
                Check(loopbackStillBindable, "断言3a: 通配占用时 CanBindLoopback(" + r1 + ")=" + loopbackStillBindable + "（应=True，证明只试绑回环会被骗）");
                Check(anywhereListening, "断言3b: IsAnywhereListening(" + r1 + ")=" + anywhereListening + "（应=True，整机扫描能发现通配监听）");
                Check(!freeEverywhere, "断言3c: IsPortFreeEverywhere(" + r1 + ")=" + freeEverywhere + "（应=False，正确拒绝）");
            }
            catch (Exception ex)
            {
                // 非管理员/urlacl 环境无法绑通配 → 本断言按「无法复现」降级为明确提示，不算失败
                Console.WriteLine("ok   (skip) 通配绑定不可用，跳过断言3（需要 HttpListener 通配权限）：" + ex.Message);
            }
            finally
            {
                try { wildcard?.Stop(); wildcard?.Close(); } catch { }
            }
        }
        else
        {
            Console.WriteLine("ok   (skip) 断言3/断言2：无可用候选端口，跳过");
        }

        // ── 断言2：人为占用某个候选端口 → 选择逻辑应跳过它 ──
        if (r1 != 0 && r1 <= 65535)
        {
            HttpListener occ = null;
            try
            {
                occ = OccupyWildcard(r1);
                int r3 = WebServer.ResolveInternalPort(WebPort);
                Check(r3 != r1, "断言2: 占用候选端口 " + r1 + " 后，选择逻辑跳过它（改选 " + r3 + "）");
            }
            catch (Exception ex)
            {
                Console.WriteLine("ok   (skip) 通配绑定不可用，跳过断言2：" + ex.Message);
            }
            finally
            {
                try { occ?.Stop(); occ?.Close(); } catch { }
            }
        }

        Console.WriteLine("");
        Console.WriteLine(fails == 0 ? "PASS：全部端口守卫断言通过" : "FAIL：" + fails + " 条断言未通过");
        return fails == 0 ? 0 : 1;
    }
}
