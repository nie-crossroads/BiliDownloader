@echo off
setlocal
chcp 65001 >nul
cd /d "%~dp0.."

rem ============================================================
rem  BiliDownloader 安装包一键构建（WPF 自绘安装器）
rem
rem  产物：dist\BiliDownloader-Setup-1.0.0.exe（约 30 MB）
rem        单个 exe：内嵌主程序（zip）+ FFmpeg + 许可 + 卸载器，载荷已预压缩。
rem        三个程序全部运行在 .NET Framework 4.8（Win10 1903+ / Win11 系统自带），
rem        所以安装包和安装后的应用都不需要附带 .NET 运行时。
rem
rem  构建顺序（有依赖，不能乱）：
rem    1) 发布主程序   -> dist\publish\BiliDownloader.UI.exe
rem    2) 发布卸载器   -> dist\uninstaller\uninstall.exe
rem    3) 发布安装器   -> dist\setup\BiliDownloader.Setup.exe（嵌入 1、2 及 ffmpeg）
rem ============================================================
echo ============================================
echo  BiliDownloader 安装包构建
echo ============================================
echo.

rem ---- 定位 dotnet ----
set "DOTNET="
where dotnet >nul 2>&1 && set "DOTNET=dotnet"
if not defined DOTNET if exist "E:\environment\NET\NET SDK10\dotnet.exe" set "DOTNET=E:\environment\NET\NET SDK10\dotnet.exe"
if not defined DOTNET if exist "%ProgramFiles%\dotnet\dotnet.exe" set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
if not defined DOTNET (
    echo [错误] 找不到 dotnet.exe。请安装 .NET 10 SDK，或把 SDK 目录加进 PATH。
    exit /b 1
)
echo 使用 SDK: %DOTNET%
"%DOTNET%" --version
echo.

rem ---- 依赖检查 ----
if not exist "tools\ffmpeg\ffmpeg.exe" (
    echo [错误] 缺少 tools\ffmpeg\ffmpeg.exe，安装包无法内嵌 FFmpeg。
    exit /b 1
)
if not exist "licenses\ffmpeg-LGPL.txt" (
    echo [错误] 缺少 licenses\ffmpeg-LGPL.txt。
    exit /b 1
)

rem ---- 1) 主程序（net48 多文件，运行时由系统自带）----
rem      net48 没有 PublishSingleFile，产物是十几个文件（约 2.3 MB），
rem      安装时整体解压到目标目录。先清空目录，避免上次的单文件产物残留。
echo [1/3] 发布主程序 ...
if exist "dist\publish" rmdir /s /q "dist\publish"
"%DOTNET%" publish src\BiliDownloader.UI\BiliDownloader.UI.csproj ^
    -c Release -o dist\publish -p:DebugType=none
if errorlevel 1 goto :fail

rem ---- 2) 卸载器（.NET Framework 4.8，系统自带运行时，产物仅约 105 KB）----
echo [2/3] 发布卸载器 ...
"%DOTNET%" publish src\BiliDownloader.Uninstaller\BiliDownloader.Uninstaller.csproj ^
    -c Release -o dist\uninstaller
if errorlevel 1 goto :fail

rem ---- 3) 安装器（.NET Framework 4.8，嵌入以上二者 + FFmpeg + 许可）----
rem      载荷先由 scripts\pack-payload.ps1 压到 build\payload（约 243 MB -> 94 MB），
rem      再作为嵌入资源打进 exe。压缩步骤由 csproj 的 PackPayload target 自动调起，
rem      并按时间戳跳过未变化的文件，不会每次重复压。
echo [3/3] 发布安装器 ...
"%DOTNET%" publish src\BiliDownloader.Installer\BiliDownloader.Installer.csproj ^
    -c Release -o dist\setup
if errorlevel 1 goto :fail

rem ---- 汇总到固定文件名 ----
copy /y "dist\setup\BiliDownloader.Setup.exe" "dist\BiliDownloader-Setup-1.0.0.exe" >nul
if errorlevel 1 goto :fail

echo.
echo ============================================
echo  构建完成
echo  安装包: %CD%\dist\BiliDownloader-Setup-1.0.0.exe
echo  说明  : 双击运行即可；静默安装 uninstall.exe /S
echo ============================================
echo.
pause
exit /b 0

:fail
echo.
echo [失败] 构建中断，请查看上面的错误信息。
echo.
pause
exit /b 1
