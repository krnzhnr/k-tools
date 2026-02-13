@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion

:: Создаем папку для результатов
mkdir "Slowed" 2>nul

:: Обрабатываем все AAC и AC3 файлы в текущей директории
for %%a in (*.aac *.ac3) do (
    set "input_file=%%a"
    set "output_file=Slowed\%%~na_slowed.wav"
    
    echo Обрабатываем: "!input_file!"
    
    :: Запускаем eac3to с опцией slowdown
    eac3to "!input_file!" "!output_file!" -slowdown
    
    if errorlevel 1 (
        echo Ошибка при обработке: "!input_file!"
    ) else (
        echo Успешно сохранено: "!output_file!"
    )
    echo.
)

echo Все файлы обработаны
pause