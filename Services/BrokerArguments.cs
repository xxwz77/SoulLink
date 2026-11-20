using System;
namespace SoulLink.Services
{
    public static class BrokerArguments
    {
        public static string AddElevated(string arguments, string nonce)
            => arguments + " --require-elevated --deploy-nonce " + nonce;
        public static string ReadNonce(string? arguments)
        {
            const string key = "--deploy-nonce ";
            if (string.IsNullOrEmpty(arguments)) return "";
            int start = arguments.IndexOf(key, StringComparison.Ordinal);
            if (start < 0) return "";
            string value = arguments.Substring(start + key.Length).Trim();
            int end = value.IndexOf(' ');
            return end > 0 ? value.Substring(0, end) : value;
        }
        public static string? NonceFromArgs(string[] args)
        {
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i].Equals("--deploy-nonce", StringComparison.Ordinal)) return args[i + 1];
            return null;
        }
    }
}