using System;
using SoulLink.Services;

internal static class Program
{
    private static int Main()
    {
        int failed = 0;
        void Check(string name, bool condition)
        {
            Console.WriteLine((condition ? "PASS " : "FAIL ") + name);
            if (!condition) failed++;
        }
        Check("standard relay accepted", BrokerProtocol.ValidPipeName("SoulLink_Relay_2_ab12cd34"));
        Check("stable relay accepted", BrokerProtocol.ValidPipeName("SoulLink-Stable_Relay_3_ab12cd34"));
        Check("uppercase hexadecimal accepted", !BrokerProtocol.ValidPipeName("SoulLink-Stable_Relay_3_AB12CD34"));
        Check("wrong stable spelling rejected", !BrokerProtocol.ValidPipeName("SoulLinkStable_Relay_3_ab12cd34"));
        Check("prefix spoof rejected", !BrokerProtocol.ValidPipeName("SoulLink-StableExtra_Relay_3_ab12cd34"));
        Check("arbitrary identity rejected", !BrokerProtocol.ValidPipeName("OtherProduct_Relay_3_ab12cd34"));
        Check("session zero rejected", !BrokerProtocol.ValidPipeName("SoulLink-Stable_Relay_0_ab12cd34"));
        Check("negative session rejected", !BrokerProtocol.ValidPipeName("SoulLink-Stable_Relay_-1_ab12cd34"));
        Check("short random suffix rejected", !BrokerProtocol.ValidPipeName("SoulLink-Stable_Relay_3_ab12cd3"));
        Check("pipe traversal rejected", !BrokerProtocol.ValidPipeName("SoulLink-Stable_Relay_3_ab12cd34\\..\\evil"));
        Check("empty rejected", !BrokerProtocol.ValidPipeName(""));
        Console.WriteLine(failed == 0 ? "ALL PASSED" : "FAILURES=" + failed);
        return failed;
    }
}
