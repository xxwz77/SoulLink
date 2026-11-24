# 服务器地址外部化改造报告

> 日期：2026-11-24　·　版本：3.3.36　·　作者：server-url-implementer（team_mwu35l8pae8oj5 / task_mwu5umfmj8i3jd）

## 一、目标

把联机大厅（LobbyApi）、路线更新（RouteUpdater）、客户端更新（CloudUpdater）三处各自的服务器地址归一为**单一设置项**，源码中不再硬编码任何发布域名（example.invalid 从源码 0 命中，发布脚本除外）。

## 二、改动清单

| 文件 | 改动 |
|---|---|
| `Services/ServerEndpoints.cs` | **新增**。单一服务器地址来源：`Base`（去空白/去尾斜杠）、`IsConfigured`、`UpdateBase`、`UpdateManifest`、`RouteManifest` |
| `Models/Models.cs` | `AppSettings` 新增 `ServerBaseUrl`；`LobbyServerUrl` 降级为历史兼容字段；`RouteUpdateSettings.DefaultCloudUrl` 常量删除、`CloudUrl` 改 `[JsonIgnore]` 历史字段、`EffectiveCloudUrl` 改为 `ServerEndpoints.RouteManifest`；文件头补 `using SoulLink.Services` |
| `Services/CloudUpdater.cs` | 删除 `BaseUrl`/`ManifestUrl` 两个硬编码常量；`FetchManifestAsync`/​`DownloadAsync` 未配置时短路、URL 改从 `ServerEndpoints` 取 |
| `Services/LobbyApi.cs` | `ConfiguredUrl`/`LoadFromConfig`/`SaveToConfig` 改读写 `ServerBaseUrl`（经 `ServerEndpoints`） |
| `Services/ConfigStore.cs` | 加载时迁移：`ServerBaseUrl` 为空则从 `RouteUpdate.CloudUrl`（裁掉 /pathing/ 后缀）或 `LobbyServerUrl` 回填，回填后清空旧字段 |
| `Views/SettingsPage.xaml` | 「联机大厅」区块改为「服务器」，文案说明三处共用、留空不联网；占位文字改「留空 = 未配置服务器（不联网）」 |
| `Views/SettingsPage.xaml.cs` | 保存日志文案改「服务器地址已更新」；`Activate()` 回显逻辑不变（只显示用户填的值） |
| `Views/MainWindow.xaml.cs` | 手动「检查更新」未配置时弹「请在 设置 → 服务器 填写服务器地址」，不再误报「已是最新」 |
| `Views/RouteUpdatePage.xaml.cs` | `CheckCloud_Click` 未配置时显示「未配置服务器地址（设置 → 服务器）」 |
| `Views/LobbyPage.xaml.cs` | 仅注释修正（预填说明） |
| `Services/RouteUpdater.cs` | 注释去除域名（cdn.example.invalid） |
| `SoulLink.csproj` | 版本 3.3.35 → 3.3.36 |
| `docs/RELEASE-RULES.md` | 发布域名规则更新：客户端不再硬编码域名，域名只允许出现在 `scripts/publish-release.ps1` |
| `design/navigation-icons/preview-config.json` | 补充 `ServerBaseUrl` 字段（一致性，非运行时） |

## 三、拼接规则（保持不变）

统一基地址 `base = ServerBaseUrl`（去尾斜杠）：

- **联机大厅**：`{base}/api/…`（`LobbyApi.Req` 里的 `_baseUrl + path`）
- **客户端更新**：`{base}/SoulLink/latest.json`（清单）、`{base}/SoulLink/{包名}`（下载）
- **路线更新**：`{base}/pathing/update.txt`（清单，随后按清单内包名相对解析）

原行为对比：联机大厅原本就是 `{base}/api/…`；客户端更新原为 `https://cdn.example.invalid/SoulLink/…`；路线更新原为 `https://cdn.example.invalid/pathing/update.txt`。路径后缀三段保持不变，只把域名/host 部分收敛到 `ServerBaseUrl`。

## 四、未配置行为

`ServerBaseUrl` 为空时：

- `LobbyApi.HasServer == false` → 所有接口返回「未配置服务器地址」，不发请求（原有守卫保持）。
- `CloudUpdater.FetchManifestAsync/DownloadAsync` 直接短路（静默检查返回 null / 下载抛「未配置服务器地址」）。
- `RouteUpdater` 经 `EffectiveCloudUrl == ""` 短路，返回「没有可用的更新源」。
- 界面：设置页「服务器」输入框留空；主窗口手动检查更新、路线页「检查更新」均给出明确提示。

## 五、老用户兼容

`ConfigStore.Load` 迁移：`ServerBaseUrl` 为空时依次从 `RouteUpdate.CloudUrl`（老路线源，裁掉 `/pathing/…` 保留 host）或 `LobbyServerUrl` 回填，回填后清空旧字段并保存。已存地址继续生效。

## 六、自证输出

1. **grep 域名**：源码（`.cs`/`.xaml`/`.csproj`）中 `example.invalid` **0 命中**；仅剩 `scripts/publish-release.ps1`（发布脚本，约定允许）。
2. **grep 设置项**：`ServerBaseUrl` 在三处消费点（LobbyApi / RouteUpdater 经 EffectiveCloudUrl / CloudUpdater 经 ServerEndpoints）均从单一设置读取。
3. **构建**：`dotnet build -c Release --nologo` → **0 错误**（322 警告均为既有历史警告，与本改动无关）。

## 七、验收结论

- 源码不再出现 example.invalid 域名（发布脚本除外）：✅
- Settings 只有一个服务器地址填写项、界面上一个输入框：✅（原「联机大厅」输入框即唯一入口，已改标题为「服务器」）
- LobbyApi / RouteUpdater / CloudUpdater 三处均从该设置读取且拼接规则保持现状：✅
- 未配置时不发任何请求且有明确提示：✅
- 老用户已存地址继续生效：✅（ConfigStore 迁移）
- SoulLink.csproj 版本已递增：✅ 3.3.35 → 3.3.36
- `dotnet build -c Release` 0 error：✅
