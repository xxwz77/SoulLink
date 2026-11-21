# 工作区规范

> 建立时间：2026-11-21
> 背景：长期开发积累到 **17.8 GB**，其中真正的源码只有几 MB，
> 其余全是历史构建产物、一次性实验目录、截图。
> 这份文档规定"东西该放哪、什么时候清"，避免再次失控。

---

## 一、目录职责（唯一权威）

```
SoulLink/
├── Views/ Services/ Controls/ Models/ Relay/ Server/ Theme/   源码
├── tools/           构建依赖的子系统（见 §2）
├── scripts/         发布与运维脚本
├── docs/            文档
│   └── research/    研究记录、故障复盘、交接文档（归档区）
├── dist/            ★ 当前可运行产物（含 stable 挂机版）
├── backups/         ★ 唯一的源码备份（只保留最近一份）
├── Assets/          图标等静态资源
├── design/          设计稿（只留源文件，不留导出图）
├── bgi-scripts/     BGI 相关脚本
└── research/        外部参考资料（txt 等）
```

**除以上之外的顶层目录，一律视为临时产物，应当删除。**

---

## 二、tools 目录：只留能构建的

### 必须保留（被 `SoulLink.csproj` 引用）

| 目录 | 用途 |
|---|---|
| `tools/7za/` | 7-Zip 命令行，`BgiUpdater` 解压依赖 |
| `tools/broker-setup/` | 服务安装器源码 |
| `tools/rescue/` | 救援程序源码 |
| `tools/updater/` | 独立更新器源码 |

### 可保留（诊断/测试工具的**源码**，不含构建产物）

`layout-check/`（108 个测试项目）、`hangwatch/`、`runtime-memory-probe/` 等。
这些目录**只应存在 `.cs` 与 `.csproj`**，总大小应在几 MB 以内。

### 禁止出现

- `bin/`、`obj/`、`dist/`、`out/`（任何层级）
- `binXxx/`、`objXxx/` 这种带前缀的产物目录
- 单文件 `.exe`（除 `tools/7za/7za.exe`）
- 截图（`.png`/`.gif`）
- 一次性实验目录（如曾经的 `focus-flash-*` 12 个，各 63 MB）

> ★ 曾经 `tools/layout-check` 有 **107 个 bin 目录、472 MB**，
> 而它的源码只有 1.8 MB。删掉产物后体积降到 1.8 MB。

---

## 三、日常规则

### 3.1 构建产物不入库、不囤积

`bin/`、`obj/`、`dist/` 已在 `.gitignore` 里。但**不进 git ≠ 可以留着**。

每次大改动后清理一次：

```powershell
# 清所有构建产物
Get-ChildItem -Recurse -Directory -Include bin,obj |
    Where-Object { $_.FullName -notmatch '\\.git\\' } |
    Remove-Item -Recurse -Force
```

### 3.2 实验产物当场删

做一次性实验（对比两种实现、抓帧、临时验证）时：

```
✅ tools/xxx-experiment/     只放 .cs 与 .csproj
✅ 跑完就删整个目录
❌ 保留 12 个变体，每个 63 MB 的构建产物
```

### 3.3 截图不入库

验证用的截图放 `%TEMP%`，**不要放工作区**。
曾经工作区里散落 100+ 张截图（`bugfix-t1`~`bugfix-t14`、`g2-step1`~`g2-step6` 等）。

### 3.4 备份只保留一份

`backups/` 里**只应有最近一次源码备份的 zip**。
新的备份做出来后，删掉旧的。

```powershell
# 备份（排除构建产物）
# 见 §5 的脚本
```

### 3.5 文档归档到 docs/research/

研究记录、故障复盘、交接文档统一放 `docs/research/`，并在
`docs/research/README.md` 里登记。

**不要**散落在项目根或 `releases/`。

---

## 四、定期体检

每隔一段时间（或感觉"东西又多起来了"）跑一次：

```powershell
# 各顶层目录体积
Get-ChildItem -Directory | ForEach-Object {
    $s = (Get-ChildItem $_.FullName -Recurse -File -EA SilentlyContinue |
          Measure-Object Length -Sum).Sum
    [pscustomobject]@{ Name = $_.Name; MB = [math]::Round($s/1MB,1) }
} | Sort-Object MB -Descending | Format-Table -AutoSize
```

### 健康标准

| 项目 | 目标 |
|---|---|
| 工作区总计（不含 `.git`） | **< 1 GB** |
| `tools/` | < 50 MB |
| `backups/` | 只有 1 个 zip |
| 根目录 `.md` 文件 | < 10 个（其余归入 `docs/`） |
| 任何 `bin/` `obj/` | 0 个 |

---

## 五、源码备份脚本

```powershell
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$dest = "$env:TEMP\sl-src-$stamp"
New-Item -ItemType Directory -Path $dest -Force | Out-Null

$exts = @('*.cs','*.xaml','*.csproj','*.sln','*.json','*.bat','*.ps1',
          '*.md','*.manifest','*.config','*.ico','*.txt','*.resx')
$dirs = @('.','Views','Services','Controls','Models','Relay','Server',
          'Theme','scripts','docs','Assets','bgi-scripts',
          'tools\updater','tools\rescue','tools\broker-setup')

foreach ($d in $dirs) {
    if (-not (Test-Path $d)) { continue }
    $target = Join-Path $dest $(if ($d -eq '.') { '' } else { $d })
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    foreach ($ext in $exts) {
        Get-ChildItem $d -Filter $ext -File -EA SilentlyContinue |
            Copy-Item -Destination $target -Force
    }
}

$zip = "backups\source-$stamp.zip"
Compress-Archive -Path "$dest\*" -DestinationPath $zip -CompressionLevel Optimal -Force
Remove-Item $dest -Recurse -Force

# ★ 删掉旧备份，只留新的
Get-ChildItem backups -Filter 'source-*.zip' |
    Where-Object { $_.Name -ne (Split-Path $zip -Leaf) } |
    Remove-Item -Force
```

---

## 六、这次清理的成绩

| 项目 | 清理前 | 清理后 |
|---|---|---|
| `releases` | 9,100 MB | **已删除**（文档归档到 `docs/research/`） |
| `artifacts` | 4,021 MB | **已删除**（测试构建目录 70+ 个） |
| `dist-cpuexp` | 1,984 MB | 已删除 |
| `tools` | 1,881 MB | **18 MB** |
| `backups` | 944 MB | **122 MB**（1 份源码备份） |
| `Server` | 568 MB | **0.1 MB** |
| 各类 `dist-backup-*` `dist-fix-*` | 674 MB | 已删除 |
| `bin` `obj` `out` `release-build` | 704 MB | 已删除 |
| **合计** | **17,781 MB** | **583 MB** |

### 归档的成果

`docs/research/` —— 33 个文件，0.4 MB：
- RDP / 远程桌面架构研究 9 篇
- 交接与状态文档 9 篇
- 变更记录与版本规范 12 篇
- 其它 2 篇

---

## 七、不要做的事

| ❌ | 原因 |
|---|---|
| 把 `releases/` 当发布历史仓库 | 9 GB 里 99% 是旧 exe，需要旧版本就从 git 重新构建 |
| 保留 `dist-backup-20261120-*` 这类带日期的目录 | 源码在 git 里，产物重新构建即可 |
| 把截图存工作区 | 用 `%TEMP%` |
| 每个实验建一个新目录且不删 | 12 个 `focus-flash-*` 累积了 700 MB |
| 在 `tools/` 里跑构建却不清理 | 107 个 bin 目录 = 472 MB |
