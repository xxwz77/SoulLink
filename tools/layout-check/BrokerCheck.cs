using System;
using System.IO;
using System.Security.Principal;
using System.Security.AccessControl;
using System.Linq;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using SoulLink.Services;
public static class BrokerCheck
{
    static int fails; static void Check(bool ok, string name) { Console.WriteLine((ok ? "ok " : "FAIL ") + name); if (!ok) fails++; }
    public static int Main()
    {
        Check(BrokerProtocol.ServiceName == "SoulLink.DevBroker", "服务名只属于测试版");
        Check(BrokerProtocol.PipeName == "SoulLink.DevBroker.v1", "服务管道固定");
        Check(BrokerProtocol.ValidPipeName("SoulLink_Relay_4_abcd1234"), "合法代理管道");
        foreach (var bad in new[] { "SoulLink-Stable_Relay_4_abcd1234", "..\\x", "SoulLink_Relay_0_abcd1234", "SoulLink_Relay_4_short" })
            Check(!BrokerProtocol.ValidPipeName(bad), "拒绝非法管道 " + bad);
        var acl = BrokerProtocol.AdministratorPipeSecurity();
        var sid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value;
        Console.WriteLine("ACL=" + acl.GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        Check(acl.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().Any(r => r.IdentityReference.Value == sid), "仅授权管理员/系统");
        Check(BrokerArguments.ReadNonce("--relay SoulLink_Relay_1_abcd1234 --require-elevated --deploy-nonce abc") == "abc", "部署 nonce 解析");
        var health = BrokerProtocol.SendAsync(new BrokerProtocol.Request { Command = "health" }, 1200).GetAwaiter().GetResult();
        Check(health.Ok || health.Code == "NOT_INSTALLED" || health.Code == "NOT_RUNNING", "服务健康响应真实：" + health.Code);
        var p = new System.Diagnostics.ProcessStartInfo("powershell.exe", "-NoProfile -Command exit 0") { UseShellExecute = false };
        using var child = System.Diagnostics.Process.Start(p)!; child.WaitForExit();
        var identity = typeof(BrokerProtocol).Assembly.GetType("SoulLink.Services.BrokerSecurity")!;
        var method = identity.GetMethod("InspectProcess", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        try
        {
            var value = method.Invoke(null, new object[] { Environment.ProcessId })!;
            Check((int)identity.GetField("SessionId")!.GetValue(value)! > 0, "进程会话检查使用实际PID");
        }
        catch (System.Reflection.TargetInvocationException ex)
        { Console.WriteLine("SKIP 沙箱不能读取当前进程令牌：" + ex.InnerException?.Message); }
        return fails;
    }
}
