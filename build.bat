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
    echo [错误] 没有找到 dotnet SDK，请先安装 .NET 10 SDK：
    echo        https://dotnet.microsoft.com/download/dotnet/10.0
    echo        本仓库 global.json 锁定了 10.0.403。
    pause
    exit /b 1
)

echo [1/2] 编译 Release 版本...
dotnet build -c Release --nologo
if errorlevel 1 goto :fail

echo.
echo [2/2] 打包单文件免安装版...
goto :publish

rem ============================================================
rem  2.18.0 迁移到 .NET 10 时改的三处，都是踩过的坑：
rem
rem  ① 不再 rmdir /s /q dist。
rem     dist 是本仓库的版本留档目录，里面的
rem     SoulLink-2.17.48-before-boundfix.exe 这类文件本身就是事故复盘的一手材料。
rem     直接清空重建会把它们全删。现在改为：发布到临时目录，再覆盖 exe。
rem
rem  ② 目标框架是 net10.0-windows，不是 net11。
rem     net11 编译和运行都正常，但它的 WPF 单文件发布有回归：
rem     DirectWriteForwarder.dll（混合模式程序集）打包后加载失败，
rem     Application 构造时抛 BadImageFormatException，进程直接退出。
rem     最小复现：空 WPF 工程，net8/net10 单文件正常，只有 net11 崩。
rem     net10 是 LTS（支持到 2028-11），单文件发布已验证可用。
rem
rem  ③ 版本号用 MSBuild 读 csproj 的 Version，不手写、也不用 findstr 抓。
rem     findstr 会因为 FileVersion / AssemblyVersion 也在同一文件里而抓到脏值。
rem ============================================================

:publish
for /f "usebackq delims=" %%v in (`dotnet msbuild SoulLink.csproj -getProperty:Version -nologo`) do set "VER=%%v"
if "!VER!"=="" set "VER=unknown"
echo      版本：!VER!

set "STAGE=artifacts\dist-!VER!"
if exist "!STAGE!" rmdir /s /q "!STAGE!"

dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o "!STAGE!" --nologo
if errorlevel 1 goto :fail

if not exist dist mkdir dist
copy /y "!STAGE!\SoulLink.exe" "dist\SoulLink.exe" >nul
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
pause
exit /b 0

:fail
echo.
echo  [失败] 构建没有完成，请把上面的错误信息发出来。
pause
exit /b 1
