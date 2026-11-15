# SoulLink 版本管理与回退

## 当前稳定基线

- 分支：`main`
- 基线提交：`bac46e8 SoulLink 稳定基线：多用户 RDP 中控台与自动化管理`
- 位置：`E:\Documents\deepseek-harness\SoulLink` 的本地 Git 仓库。

提交包含源码、XAML、文档和验证截图；不包含构建产物、测试工具、日志或本地凭据配置。

## 日常提交

每次修改并实机验证后再提交：

```powershell
git add --all
git commit -m "说明本次修改"
```

不要只凭编译通过提交稳定版本。HANDOFF.md 中应同时记录实测结论与未验证限制。

## 改出问题后回退

### 1. 查看最近提交

```powershell
git log --oneline --decorate -10
```

### 2. 丢弃尚未提交的源码修改，回到最近提交

```powershell
git restore --source=HEAD --staged --worktree -- .
```

这会覆盖已跟踪源码，请先把需要的改动另存为提交或补丁。`dist` 和 `tools` 被忽略，不会被此命令删除。

### 3. 回到稳定基线

```powershell
git switch --detach bac46e8
```

这会让仓库进入 detached HEAD，只读查看旧版本。若要继续开发，建议创建修复分支：

```powershell
git switch -c repair-from-baseline
```

### 4. 只回退某个文件

```powershell
git restore --source=bac46e8 -- Views/ConsolePage.xaml.cs
```

### 5. 撤销已经提交的错误提交，保留修改历史

```powershell
git revert <错误的提交哈希>
```

`revert` 创建反向提交，适合已经分享的提交；不要对 shared/public 提交使用 reset。

### 6. 仅查看旧版本，不修改当前工作区

```powershell
git show bac46e8:Views/ConsolePage.xaml.cs
```

## 发布与安全

- `dist\SoulLink.exe` 不纳入 Git，回退源码后需重新 `dotnet publish`。
- 本地 `%LOCALAPPDATA%\SoulLink\config.json` 不受 Git 管理，**不会因为回退源码而自动回退配置**。
- 配置和 DPAPI 凭据的备份必须在实际修改前单独进行，不能依赖 Git。
- 禁止使用 `git clean -fd` 作为常规回退命令，避免误删测试/本地文件。
