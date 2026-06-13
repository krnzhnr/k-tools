@echo off
chcp 65001 > nul
title Сборка и упаковка K-Tools в MSIX
echo Запуск сценария автоматической сборки...

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
if errorlevel 1 goto error

echo.
echo [УСПЕХ] Сборка и упаковка завершены успешно.
goto end

:error
echo.
echo [ОШИБКА] Произошел сбой во время выполнения сборки.
color 0C

:end
pause