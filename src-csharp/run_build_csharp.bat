@echo off
chcp 65001 >nul
setlocal

echo [*] Activating Python venv...
call ..\venv\Scripts\activate.bat

echo [*] Running C# build script...
python ..\build_csharp.py

pause
