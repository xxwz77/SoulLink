@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion

echo.
echo  ============================================
echo   SoulLink 构建脚本
echo  ============================================
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [错误] 没有找到 dotnet SDK，请先安装 .NET 11 SDK：
    echo        https://dotnet.microsoft.com/download/dotnet/11.0
    echo        本仓库 global.json 锁定了 11.0.100。
    pause
    exit /b 1
)

echo [1/2] 编译 Release 版本...
dotnet build -c Release --nologo
if errorlevel 1 goto :fail

echo.
echo [2/2] 发布「框架依赖 + 单文件」版...
goto :publish

rem ============================================================
rem  2.19.0 发布形态说明（改过三次，前两次都踩了坑）：
rem
rem  【为什么不自包含】
rem    .NET 11 的 WPF 混合模式程序集 DirectWriteForwarder.dll 被重写
rem    （526KB -> 203KB，CLR 目录偏移也变了），
rem    打进「自包含单文件」后加载会抛 BadImageFormatException，
rem    进程在 Application 构造时直接退出。最小复现：空 WPF 工程，
rem    net8/net10 自包含单文件都正常，只有 net11 崩。
rem
rem  【为什么框架依赖反而是好事】
rem    框架依赖时 DirectWriteForwarder.dll 直接从用户装的运行时加载，
rem    不进 bundle，那个回归被彻底绕开。实测：
rem      自包含单文件   67.7 MB / 启动 4101ms
rem      框架依赖单文件  3.3 MB / 启动 2617ms
rem    体积降到 1/20，启动还快 1.5 秒（不用解压、直接映射共享运行时）。
rem    代价：用户必须装 .NET 11 桌面运行时，用 tools\check-runtime.ps1 引导。
rem
rem  【不用 IncludeAllContentForSelfExtract】
rem    它能让自包含也能跑，但那是「.NET Core 3.1 兼容模式」，
rem    官方明说可能在未来版本移除，且每次启动要把全部程序集解压到
rem    %TEMP%\.net。不适合作为正式发布方案。
rem
rem  【不再 rmdir /s /q dist】
rem    dist 是版本留档目录，里面的 SoulLink-2.17.48-before-boundfix.exe
rem    这类文件本身就是事故复盘的一手材料。改为发布到 artifacts 再覆盖。
rem ============================================================

:publish
for /f "usebackq delims=" %%v in (`dotnet msbuild SoulLink.csproj -getProperty:Version -nologo`) do set "VER=%%v"
if "!VER!"=="" set "VER=unknown"
echo      版本：!VER!

set "STAGE=artifacts\dist-!VER!"
if exist "!STAGE!" rmdir /s /q "!STAGE!"

rem ★ 注意：框架依赖发布不能同时开压缩（会报 NETSDK1176），所以没有 EnableCompressionInSingleFile。
rem ★ 多目标 TFM 后必须显式 -f 指定要发布的框架（WGC 需要版本化 TFM 的 WinRT 投影）。
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -f net11.0-windows10.0.26100.0 -o "!STAGE!" --nologo
if errorlevel 1 goto :fail

rem 运行时检测脚本随包发出，用户双击 exe 前先跑它
rem （放在 scripts\ 下而不是 tools\，因为 .gitignore 把整个 tools\ 排除了）
copy /y "scripts\check-runtime.ps1" "!STAGE!\check-runtime.ps1" >nul 2>nul

rem ★ 使用手册随包发出：用户装完就能看到怎么用，不用回仓库找。
rem   publish-release.ps1 只排除 .log/.bak/.old/.tmp/.pdb，.md 会被正常打进更新包。
copy /y "README.md" "!STAGE!\README.md" >nul 2>nul
if exist "README.md" echo   使用手册：README.md

rem ★ 网页界面随包发出（3.3.10）：
rem   主程序优先读 exe 同目录的 web-index.html，读不到才用内置常量。
rem   这样改网页样式不需要重编译、不需要重启程序（重启会断开 RDP 会话）。
copy /y "docs\web-index.html" "!STAGE!\web-index.html" >nul 2>nul
if exist "docs\web-index.html" echo   网页界面：web-index.html

if not exist dist mkdir dist
copy /y "!STAGE!\SoulLink.exe" "dist\SoulLink.exe" >nul

rem ★ 注入代理随包发出（3.3.38+）：
rem   SoulLink.Agent.exe 是主程序的副本，按文件名切身份（Services/AppIdentity.cs
rem   IsAgentExecutable），不需要改参数。以前不在包里，只能靠运行时从主程序复制，
rem   导致主程序升级后 agent 还是旧版、投送直接失败（BrokerInstall.ValidAgent
rem   要求 FileVersion 完全一致）。
rem   ★ 待办：后续会拆成真正的独立 exe（去掉 WPF 依赖、体积从 4.4MB 降到 ~1.4MB），
rem     届时这里改为复制 SoulLink.Agent 项目产物，路径不变，无需改调用方。
rem ★ t7a（2026-11-24）：注入代理拆成独立无 UI 工程，不再是主程序字节副本。
rem   产物由 Agent\SoulLink.Agent.csproj 单独 publish（含 SoulLink.Agent.version）。
dotnet publish "Agent\SoulLink.Agent.csproj" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o "!STAGE!\agent" --nologo
if errorlevel 1 goto :fail
rem ★ t16（T7b）：Broker 服务本体独立 publish（无 UI）。产物直接进 STAGE 根，与主程序/agent 平级。
dotnet publish "Agent\SoulLink.Broker.csproj" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o "!STAGE!\broker" --nologo
if errorlevel 1 goto :fail
rem ★ copy 失败断言：上一轮踩过「dist\SoulLink.exe 被占用、copy 静默失败停在旧版」。
rem   这里每个产物复制后立即校验目标存在，失败即报错退出，绝不静默。
copy /y "!STAGE!\agent\SoulLink.Agent.exe" "dist\SoulLink.Agent.exe" >nul || (echo [失败] 复制 dist\SoulLink.Agent.exe 失败（文件被占用？） & goto :fail)
copy /y "!STAGE!\agent\SoulLink.Agent.version" "dist\SoulLink.Agent.version" >nul || (echo [失败] 复制 dist\SoulLink.Agent.version 失败 & goto :fail)
copy /y "!STAGE!\SoulLink.Agent.required.version" "dist\SoulLink.Agent.required.version" >nul || (echo [失败] 复制 dist\SoulLink.Agent.required.version 失败 & goto :fail)
rem ★ t16（T7b）：Broker 服务本体进 STAGE 根 + dist（服务路径迁移后 SCM 启动它）。copy 失败即报错，不静默。
copy /y "!STAGE!\broker\SoulLink.Broker.exe" "!STAGE!\SoulLink.Broker.exe" >nul || (echo [失败] 复制 !STAGE!\SoulLink.Broker.exe 失败 & goto :fail)
copy /y "!STAGE!\broker\SoulLink.Broker.exe" "dist\SoulLink.Broker.exe" >nul || (echo [失败] 复制 dist\SoulLink.Broker.exe 失败 & goto :fail)
if exist "dist\SoulLink.Broker.exe" echo   Broker 服务：dist\SoulLink.Broker.exe
if exist "dist\SoulLink.Agent.exe" echo   注入代理：dist\SoulLink.Agent.exe
if exist "dist\SoulLink.Agent.version" echo   注入代理版本标记：dist\SoulLink.Agent.version
if exist "dist\SoulLink.Agent.required.version" echo   要求代理版本标记：dist\SoulLink.Agent.required.version

rem ★ 同步 tools\ 子目录（服务安装器在这里）。
rem   BrokerSetup 现在是框架依赖单文件（约 1.1 MB），由 csproj 的
rem   PublishBrokerSetup 目标自动发布；build.bat 只负责同步到 dist。
if exist "!STAGE!\tools" (
    if not exist "dist\tools" mkdir "dist\tools"
    copy /y "!STAGE!\tools\*.*" "dist\tools\" >nul
    echo   服务安装器：dist\tools\
)
rem ★ 独立更新器必须一起分发：主程序启动它来替换自己。
rem   首次安装缺少它，后续的"检查更新"会因为找不到组件而失败。
rem ★ 一键救援程序也要一起分发：主程序卡死时用户双击它解除光标/释放鼠标/结束进程。
if exist "!STAGE!\sl-rescue.exe" (
    copy /y "!STAGE!\sl-rescue.exe" "dist\sl-rescue.exe" >nul
    echo   救援程序：dist\sl-rescue.exe
)

if exist "!STAGE!\SoulLink.Updater.exe" (
    copy /y "!STAGE!\SoulLink.Updater.exe" "dist\SoulLink.Updater.exe" >nul
    echo   更新器：dist\SoulLink.Updater.exe
) else (
    echo.
    echo  [警告] 没找到 SoulLink.Updater.exe，云更新将无法完成替换。
    echo         请确认 tools\updater 已构建。
)

if errorlevel 1 (
    echo.
    echo  [提示] 覆盖 dist\SoulLink.exe 失败，通常是它正在运行。
    echo         请先退出托盘里的 SoulLink，再重新执行 build.bat。
    echo         已发布的成品仍在：!STAGE!
    goto :fail
)

rem ★ 第三方许可证声明必须随包分发（3.3.40+，task t11）。
rem   为什么做成构建步骤而不是手工放：MIT 要求「copyright notice 与
rem   permission notice 必须随副本或实质部分一并提供」，LGPL-2.1 §6 要求
rem   随分发提供许可证副本。手工复制一定会漏，而漏了不会有任何报错 ——
rem   用户拿到的包里就是没有，谁也不会发现。所以这里复制 + 找不到即判红。
rem   ★ 故意放在上面那个 if errorlevel 1 之后：本段的 copy/echo 会改写
rem     errorlevel，放在前面会把「覆盖 exe 失败」的判定搅乱。
rem   见仓库根 THIRD-PARTY-NOTICES.txt。
if not exist "THIRD-PARTY-NOTICES.txt" (
    echo.
    echo  [错误] 缺少 THIRD-PARTY-NOTICES.txt（仓库根）。
    echo         它会随包分发给每个用户，是 MIT / LGPL-2.1 的合规要求。
    echo         不要为了让构建通过而删掉这段检查 —— 请恢复该文件。
    goto :fail
)
copy /y "THIRD-PARTY-NOTICES.txt" "dist\THIRD-PARTY-NOTICES.txt" >nul
if exist "!STAGE!" copy /y "THIRD-PARTY-NOTICES.txt" "!STAGE!\THIRD-PARTY-NOTICES.txt" >nul
echo   第三方许可证声明：dist\THIRD-PARTY-NOTICES.txt

echo.
echo  --------------------------------------------
echo   完成！成品：dist\SoulLink.exe
echo   暂存：!STAGE!
echo  --------------------------------------------
for %%f in ("dist\SoulLink.exe") do echo   大小：%%~zf 字节
echo.
echo   ★ 这是「框架依赖」版本，用户需要先安装 .NET 11 桌面运行时。
echo     随包附带的 check-runtime.ps1 可以检测并引导安装。
echo.
pause
exit /b 0

:fail
echo.
echo  [失败] 构建没有完成，请把上面的错误信息发出来。
pause
exit /b 1
