@echo off
chcp 65001 >nul
rem ============================================
rem  DuplicateMediaCleaner (C# WinForms) 一键构建
rem  产物: publish\DuplicateMediaCleaner.exe (~1MB)
rem  框架依赖单文件, 体积小; 需目标机器装有 .NET 10 运行时
rem  运行后的程序本身不会弹黑框(本窗口只是构建过程)
rem ============================================
setlocal

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [错误] 未找到 dotnet 命令，请先安装 .NET SDK。
    pause
    exit /b 1
)

echo 正在发布框架依赖单文件版本 ...
dotnet publish DuplicateMediaCleaner.csproj -c Release -r win-x64 --self-contained false ^
    -p:PublishSingleFile=true ^
    -p:DebugType=None ^
    -p:DebugSymbols=false ^
    -o publish

if errorlevel 1 (
    echo [失败] 构建出错。
    pause
    exit /b 1
)

echo.
echo [完成] 已生成 publish\DuplicateMediaCleaner.exe
echo 该文件为框架依赖单文件，约 1MB。目标电脑需已安装 .NET 10 运行时才可运行。
pause