@echo off
chcp 65001 >nul
setlocal

echo.
echo  ============================================
echo   SoulLink 构建脚本
echo  ============================================
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [错误] 没有找到 dotnet SDK，请先安装 .NET 8 SDK：
    echo        https://dotnet.microsoft.com/download/dotnet/8.0
    pause
    exit /b 1
)

echo [1/2] 编译 Release 版本...
dotnet build -c Release --nologo
if errorlevel 1 goto :fail

echo.
echo [2/2] 打包单文件免安装版...
if exist dist rmdir /s /q dist
dotnet publish -c Release -r win-x64 --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    -p:DebugType=none -o dist --nologo
if errorlevel 1 goto :fail

echo.
echo  --------------------------------------------
echo   完成！成品：dist\SoulLink.exe
echo  --------------------------------------------
dir /b dist
echo.
pause
exit /b 0

:fail
echo.
echo  [失败] 构建没有完成，请把上面的错误信息发出来。
pause
exit /b 1
