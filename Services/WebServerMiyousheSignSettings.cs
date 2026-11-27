using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using SoulLink.Models;

namespace SoulLink.Services;

public static partial class WebServer
{
    private static readonly object MiyousheSignSettingsGate = new();

    private static Task HandleMiyousheSignSettingsAsync(HttpListenerContext ctx, string account,
        MiyousheApiRequest? request = null)
    {
        var config = App.Config;
        if (config?.Settings == null)
            return WriteJson(ctx, new { ok = false, error = "配置未加载" });

        string? configuredAccount = AccountScope.Accounts(config)
            .FirstOrDefault(item => string.Equals(item.UserName, account, StringComparison.OrdinalIgnoreCase))?.UserName;
        if (string.IsNullOrEmpty(configuredAccount))
            return WriteJson(ctx, new { ok = false, error = "请选择有效的账号" });

        account = configuredAccount;
        var settings = config.Settings;
        object result;
        lock (MiyousheSignSettingsGate)
        {
            var games = MiyousheSignPreferences.Projects(settings, account).ToArray();
            if (request != null)
            {
                TimeSpan time = default;
                if (request.signTime != null && (!TimeSpan.TryParseExact(request.signTime, @"hh\:mm",
                    CultureInfo.InvariantCulture, out time) || time >= TimeSpan.FromDays(1)))
                    return WriteJson(ctx, new { ok = false, error = "签到时间须为 00:00 至 23:59" });

                var allowed = games.Select(MiyousheSignPreferences.ProjectId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (request.signProjects != null && request.signProjects.Any(id => !allowed.Contains(id)))
                    return WriteJson(ctx, new { ok = false, error = "包含当前区服不支持的签到项目" });

                if (request.globalSignEnabled.HasValue) settings.MiyousheSignEnabled = request.globalSignEnabled.Value;
                if (request.signTime != null)
                {
                    settings.MiyousheSignTime = time.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
                    settings.MiyousheSignHour = time.Hours;
                }
                if (request.accountSignEnabled.HasValue)
                    settings.MiyousheSignAccounts = new Dictionary<string, bool>(settings.MiyousheSignAccounts,
                        StringComparer.OrdinalIgnoreCase) { [account] = request.accountSignEnabled.Value };
                if (request.signProjects != null)
                    settings.MiyousheSignProjects = new Dictionary<string, string[]>(settings.MiyousheSignProjects,
                        StringComparer.OrdinalIgnoreCase) { [account] = request.signProjects.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
                ConfigStore.Save(config);
            }

            result = new
            {
                ok = true,
                globalEnabled = settings.MiyousheSignEnabled,
                accountEnabled = MiyousheSignPreferences.AccountEnabled(settings, account),
                time = MiyousheSignPreferences.TimeOfDay(settings).ToString(@"hh\:mm", CultureInfo.InvariantCulture),
                projects = games.Select(game =>
                {
                    string id = MiyousheSignPreferences.ProjectId(game);
                    return new { id, name = MiyousheSignPreferences.ProjectLabel(game),
                        enabled = MiyousheSignPreferences.ProjectEnabled(settings, account, id) };
                }).ToArray()
            };
        }

        return WriteJson(ctx, result);
    }
}
