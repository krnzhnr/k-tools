@echo off
setlocal enabledelayedexpansion

echo Введите цифру для выбора шаблона сборки файлов и нажмите Enter.
echo 1 = video+ac3+ass (замена аудио)
echo 2 = video+ac3 (замена аудио)
echo 3 = video+ass (только добавление субтитров)
set /p q="Выбор: "

:: Создаем папку Completed без ошибок
if not exist "Completed" md "Completed" 2>nul

:: Определяем путь к MKVToolNix
set "mkvtoolnix_path="
if exist "%PROGRAMFILES%\MkvToolNix\mkvmerge.exe" (
    set "mkvtoolnix_path=%PROGRAMFILES%\MkvToolNix\mkvmerge.exe"
) else if exist "%PROGRAMFILES(x86)%\MkvToolNix\mkvmerge.exe" (
    set "mkvtoolnix_path=%PROGRAMFILES(x86)%\MkvToolNix\mkvmerge.exe"
)

if not defined mkvtoolnix_path (
    echo MKVToolNix не найден! Убедитесь, что программа установлена.
    pause
    exit /b 1
)

:: Основная логика сборки
if "%q%"=="1" (
    for %%f in (*.mkv *.mp4 *.hevc *.avi *.h264) do (
        if exist "%%~nf.ac3" if exist "%%~nf.ass" (
            if not exist "Completed\%%~nf.mkv" (
                "%mkvtoolnix_path%" --output "Completed\%%~nf.mkv" ^
                    --default-track 0:yes --language 0:rus --track-name 0:"[Надписи]" "%%~nf.ass" ^
                    --default-track 0:yes --language 0:rus "%%~nf.ac3" ^
                    --no-audio ^
                    --language 0:jpn "%%f" ^
                    --title "%%~nf" --track-order 2:0,1:0,0:0 ^
                    --disable-track-statistics-tags --no-global-tags
            )
        )
    )
) else if "%q%"=="2" (
    for %%f in (*.mkv *.mp4 *.hevc *.avi *.h264) do (
        if exist "%%~nf.ac3" (
            if not exist "Completed\%%~nf.mkv" (
                "%mkvtoolnix_path%" --output "Completed\%%~nf.mkv" ^
                    --default-track 0:yes --language 0:rus "%%~nf.ac3" ^
                    --no-audio ^
                    --language 0:jpn "%%f" ^
                    --title "%%~nf" --track-order 1:0,0:0 ^
                    --disable-track-statistics-tags --no-global-tags
            )
        )
    )
) else if "%q%"=="3" (
    for %%f in (*.mkv *.mp4 *.hevc *.avi *.h264) do (
        if exist "%%~nf.ass" (
            if not exist "Completed\%%~nf.mkv" (
                "%mkvtoolnix_path%" --output "Completed\%%~nf.mkv" ^
                    --default-track 0:yes --language 0:rus --track-name 0:"[Надписи]" "%%~nf.ass" ^
                    --language 0:jpn "%%f" ^
                    --title "%%~nf" --track-order 1:0,0:0 ^
                    --disable-track-statistics-tags --no-global-tags
            )
        )
    )
) else (
    echo Неверный выбор!
    pause
    exit /b 1
)

echo Сборка завершена!
pause