using System;

// App 垫片：store 在后台线程写 App.AppendErrorLog；验证工程里打到控制台。
namespace SoulLink
{
    internal static class App
    {
        internal static void AppendErrorLog(string source, Exception? ex)
            => Console.WriteLine("[app-diag] " + source + ": " + (ex?.Message ?? ""));
    }
}

namespace SoulLink.Services
{
    internal static class LogBus
    {
        internal static void Ok(string title, string detail) => Console.WriteLine(title + ": " + detail);
    }
}
