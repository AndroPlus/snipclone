@echo off
rem Publishes dist\SnipClone\SnipClone.exe (single file, uses the installed .NET 9 Desktop Runtime)
rem and installs it for the current user.
setlocal
cd /d "%~dp0"
where dotnet >nul 2>nul || (echo The .NET 9 SDK is required: https://dotnet.microsoft.com/download & exit /b 1)
dotnet publish src\SnipClone.csproj -c Release -r win-x64 --self-contained false ^
    -p:PublishSingleFile=true -p:DebugType=none -o dist\SnipClone || exit /b 1
powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1
