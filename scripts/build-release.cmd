@echo off
setlocal
chcp 65001 >nul
cd /d "%~dp0.."

echo ============================================
echo  BiliDownloader 自包含发布（win-x64）
echo  产物带 .NET 运行时，目标机器无需安装任何东西
echo ============================================
echo.

rem ---- 定位 dotnet（本机 SDK 装在 E 盘，可能不在 PATH）----
set "DOTNET="
where dotnet >nul 2>&1 && set "DOTNET=dotnet"
if not defined DOTNET if exist "E:\environment\NET\NET SDK10\dotnet.exe" set "DOTNET=E:\environment\NET\NET SDK10\dotnet.exe"
if not defined DOTNET if exist "%ProgramFiles%\dotnet\dotnet.exe" set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
if not defined DOTNET (
    echo [错误] 找不到 dotnet.exe。请安装 .NET 10 SDK，或自行把 SDK 目录加进 PATH。
    echo.
    pause
    exit /b 1
)
echo 使用 SDK: %DOTNET%
"%DOTNET%" --version
echo.

rem ---- 发布（自包含，非单文件：启动快、方便替换 FFmpeg）----
"%DOTNET%" publish src\BiliDownloader.UI\BiliDownloader.UI.csproj ^
    -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false ^
    -o publish\win-x64
if errorlevel 1 goto :fail

rem ---- 附带 FFmpeg（合并音视频必需）----
if exist tools\ffmpeg\ffmpeg.exe (
    copy /y tools\ffmpeg\ffmpeg.exe publish\win-x64\ffmpeg.exe >nul
    echo [OK] 已附带 ffmpeg.exe
) else (
    echo [注意] tools\ffmpeg\ffmpeg.exe 不存在，产物将缺少 FFmpeg，下载会在合并环节失败。
)

echo.
echo ============================================
echo  发布完成
echo  可执行文件: %CD%\publish\win-x64\BiliDownloader.UI.exe
echo  整个 publish\win-x64 目录可直接压缩分发。
echo ============================================
echo.
pause
exit /b 0

:fail
echo.
echo [失败] 发布未完成，请查看上面的错误信息。
echo.
pause
exit /b 1
