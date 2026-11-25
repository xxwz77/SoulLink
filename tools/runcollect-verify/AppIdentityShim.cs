
// AppIdentity 垫片：验证工程只用到 DataDir 一个属性（UseIsolatedForTest 已重定向 DB）。
namespace SoulLink.Services
{
    internal static class AppIdentity
    {
        internal static string DataDir => ".";
    }
}
