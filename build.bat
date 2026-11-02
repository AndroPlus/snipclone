@echo off
rem Builds dist\SnipClone\SnipClone.exe in a private build venv, then installs it for the current user.
setlocal
cd /d "%~dp0"
if not exist .build-venv\Scripts\python.exe (
    python -m venv .build-venv || exit /b 1
)
.build-venv\Scripts\python.exe -m pip install --quiet pyinstaller pillow || exit /b 1
.build-venv\Scripts\python.exe -m PyInstaller --noconfirm --clean --windowed --name SnipClone ^
    --icon assets\icon.ico --add-data "assets;assets" main.py || exit /b 1
powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1
