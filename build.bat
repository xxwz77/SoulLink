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
