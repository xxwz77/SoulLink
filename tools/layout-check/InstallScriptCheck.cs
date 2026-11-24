using System;
using System.IO;

// ★ 2026-11-24 更新（原断言前提已失效，不是产品缺陷）：
//   原断言要求 dist\tools\broker-install.ps1 里出现 S-1-5-18 / S-1-5-32-544 / S-1-5-32-545 三个字面量。
//   但 ACL 逻辑后来搬进了 tools/broker-setup/Program.cs（编译成 SoulLink.BrokerSetup.exe），
//   ps1 只剩「校验管理员 + 调 exe」，SID 字面量不再出现在 ps1 里。
//   实测：这三个 SID 现在位于 tools/broker-setup/Program.cs:210-213。
//   所以断言改为「ps1 不再出现账户别名」+「SID 逻辑在 BrokerSetup 源码里齐全」，
//   覆盖的是同一个安全要求，只是跟着实现搬了位置。
public static class InstallScriptCheck
{
 public static int Main()
 {
  int fails = 0;
  void Check(bool ok, string text) { Console.WriteLine((ok ? "ok   " : "FAIL ") + text); if (!ok) fails++; }

  // 1) 发布脚本仍只允许测试版 exe、且不再使用账户别名
  string script = Path.Combine("dist", "tools", "broker-install.ps1");
  if (!File.Exists(script)) { Console.Error.WriteLine("发布脚本不存在：" + script); return 1; }
  string s = File.ReadAllText(script);
  Check(!s.Contains("@('SY','BA')") && !s.Contains("'BU'"), "发布脚本不含账户别名");
  Check(s.Contains("SoulLink.exe"), "发布脚本仍限测试版 exe");

  // 2) 固定 SID 逻辑现在在 BrokerSetup 源码里（ps1 只是外壳）
  string setup = Path.Combine("tools", "broker-setup", "Program.cs");
  if (!File.Exists(setup)) { Console.Error.WriteLine("BrokerSetup 源码不存在：" + setup); return 4; }
  string b = File.ReadAllText(setup);
  Check(b.Contains("S-1-5-18"), "BrokerSetup 使用 SYSTEM SID (S-1-5-18)");
  Check(b.Contains("S-1-5-32-544"), "BrokerSetup 使用 Administrators SID (S-1-5-32-544)");
  Check(b.Contains("S-1-5-32-545"), "BrokerSetup 使用 Users 只读 SID (S-1-5-32-545)");

  Console.WriteLine(fails == 0 ? "ok 发布安装脚本仅使用固定SID" : "FAILURES=" + fails);
  return fails;
 }
}