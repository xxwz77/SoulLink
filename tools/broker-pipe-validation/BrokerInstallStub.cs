namespace SoulLink.Services
{
    internal static class SystemLauncher
    {
        public static string ResolveSelfExe() => System.Environment.ProcessPath ?? "";
    }

    internal static class BrokerInstall
    {
        public static string ServiceExe => "";
    }
}
