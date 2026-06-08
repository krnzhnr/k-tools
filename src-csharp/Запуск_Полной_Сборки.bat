@echo off
:: Устанавливаем кодировку UTF-8 для корректного вывода русского текста
chcp 65001 > nul
title Сборка и упаковка K-Tools в MSIX
echo Запуск сценария автоматической сборки...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
if %errorlevel% neq 0 (
    echo.
    echo [ОШИБКА] Произошел сбой во время выполнения сборки.
    color 0C
) else (
    echo.
    echo [УСПЕХ] Сборка и упаковка завершены успешно.
)
pause
