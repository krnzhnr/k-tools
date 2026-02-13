@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion

:: Создаем уникальный временный файл
set "tempFile=%temp%\filelist_%random%.txt"

:: Собираем список файлов
for %%X in (mp4 mkv mov avi) do (
    for %%F in (*."%%X") do (
        set "fname=%%~nF"
        set "fname_clean=!fname:_cl=!"
        if "!fname!"=="!fname_clean!" (
            echo %%F>>"%tempFile%"
        )
    )
)

:: Проверяем, есть ли файлы для обработки
if not exist "%tempFile%" (
    echo Нет файлов для обработки.
    pause
    exit /b 0
)

echo Найдены файлы для обработки:
type "%tempFile%"
echo.

:: Обрабатываем файлы
for /f "usebackq delims=" %%F in ("%tempFile%") do (
    set "outputFile=%%~nF_cl%%~xF"
    if not exist "!outputFile!" (
        echo Обработка: %%F
        ffmpeg -hide_banner -loglevel error -i "%%F" -map_metadata -1 -c:v copy -c:a copy "!outputFile!" >nul 2>&1
        if errorlevel 1 (
            echo Ошибка при обработке: %%F
        ) else (
            echo Успешно очищены метаданные: %%F
        )
    ) else (
        echo Файл уже существует: !outputFile!
    )
)

:: Удаляем временный файл
if exist "%tempFile%" del "%tempFile%"

echo Готово!
pause