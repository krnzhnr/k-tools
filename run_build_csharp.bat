@echo off
chcp 65001 >nul
setlocal

echo [*] Running C# build script...
python build_csharp.py

pause
