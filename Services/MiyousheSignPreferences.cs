using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SoulLink.Models;

namespace SoulLink.Services;

internal static class MiyousheSignPreferences
{
    internal const string DomesticGenshin = "hk4e_cn@cn";

    internal static string RegionFor(AppSettings settings, string account)
        => settings.MiyousheAccountRegions.TryGetValue(account, out var value) && value == "os" ? "os" : "cn";

    internal static bool AccountEnabled(AppSettings settings, string account)
        => !settings.MiyousheSignAccounts.TryGetValue(account, out bool enabled) || enabled;

    internal static string ProjectId(MiyousheGameProfile game) => game.GameBiz + "@" + game.Region;

    internal static IEnumerable<MiyousheGameProfile> Projects(AppSettings settings, string account)
        => MiyousheCatalog.ForRegion(RegionFor(settings, account))
            .Where(game => ProjectId(game) == DomesticGenshin || (game.ActVerified && game.ActId.Length > 0));

    internal static bool ProjectEnabled(AppSettings settings, string account, string id)
    {
        if (settings.MiyousheSignProjects.TryGetValue(account, out var selected))
            return selected.Contains(id, StringComparer.OrdinalIgnoreCase);
        if (settings.MiyousheExcludedTasks.Contains(account + "|" + id)) return false;
        return id == DomesticGenshin
            || (settings.MiyousheTaskEnabled.TryGetValue(id, out bool enabled) && enabled);
    }

    internal static TimeSpan TimeOfDay(AppSettings settings)
    {
        if (TimeSpan.TryParse(settings.MiyousheSignTime, CultureInfo.InvariantCulture, out var time)
            && time >= TimeSpan.Zero && time < TimeSpan.FromDays(1))
            return time;
        return TimeSpan.FromHours(Math.Clamp(settings.MiyousheSignHour, 0, 23));
    }

    internal static string ProjectLabel(MiyousheGameProfile game) => game.SignGame switch
    {
        "hk4e" => "原神",
        "hkrpg" => "崩坏：星穹铁道",
        "bh3" => "崩坏3",
        "bh2" => "崩坏学园2",
        "zzz" => "绝区零",
        _ => game.SignGame
    };
}
