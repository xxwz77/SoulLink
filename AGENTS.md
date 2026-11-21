# 项目规则

## 云端发布（必读 docs/RELEASE-RULES.md）

1. **所有发布只走 `scripts/publish-release.ps1`**，不手工上传 COS。
2. **默认增量**：`publish-release.ps1 -Push` —— 只含与上版相比有变化的文件。
3. **全量**：`publish-release.ps1 -Full -Push` —— 首次发布、新增目录结构、
   需要删除废弃文件、或要强制修复用户端文件时使用。
4. **绝对禁止把 `data\` 目录打进更新包** —— 那是用户配置/账号/日志。
   打包规则已在脚本中硬编码，改动前必须理解后果。
5. **发布顺序不可颠倒**：先上传 exe → HEAD 校验通过 → 才写 latest.json。
   清单一旦指向坏包，所有用户更新失败且无法自愈。
6. **版本号唯一来源是 `SoulLink.csproj` 的 `<Version>`**，改动必须升版本。
7. **发布域名不得出现在任何用户可见的位置**（界面、日志、异常提示）。
   只允许在 `Services/CloudUpdater.cs` 的两个常量里。

## 边界约定

- 未授权不得触碰 `dist/stable/SoulLink-Stable.exe`（挂机测试版）。
- 未授权不得使用 A1 / A4 账号。
