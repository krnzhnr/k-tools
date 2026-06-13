@echo off
chcp 65001 >nul
setlocal

echo [*] Активация venv...
call venv\Scripts\activate.bat

echo [*] Запуск build.py...
python build.py
