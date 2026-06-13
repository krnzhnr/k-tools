@echo off
chcp 65001 >nul
setlocal

echo [*] Activating Python venv...
call src-python\venv\Scripts\activate.bat

echo [*] Running C# build script...
python src-python\build_csharp.py

pause
