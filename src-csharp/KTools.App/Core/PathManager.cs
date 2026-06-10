// -*- coding: utf-8 -*-
using System;
using System.IO;

namespace KTools_App.Core;

/// <summary>
/// Класс для управления путями приложения (поиск внешних утилит и рабочих директорий).
/// </summary>
public static class PathManager
{
    private static readonly string BaseDir;

    static PathManager()
    {
        // Инициализация базового пути приложения с фиксацией в логе (эмуляция логгера)
        BaseDir = AppContext.BaseDirectory;
    }

    /// <summary>
    /// Получить базовую директорию сборки приложения.
    /// </summary>
    /// <returns>Абсолютный путь к директории сборки.</returns>
    public static string GetBaseDirectory()
    {
        return BaseDir;
    }

    /// <summary>
    /// Возвращает абсолютный путь к директории bin для утилит.
    /// 
    /// Для обычных приложений: использует папку bin рядом с исполняемым файлом.
    /// Для MSIX приложений: использует LocalAppData (так как Program Files доступен только для чтения).
    /// Это обеспечивает работу скачивания и распаковки зависимостей в MSIX приложениях.
    /// </summary>
    /// <returns>Абсолютный путь к локальной папке bin утилит.</returns>
    public static string GetBinDirectory()
    {
        // Железобетонная проверка: если приложение запущено из защищенной системной папки, это MSIX
        bool isMsix = BaseDir.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase);

        if (isMsix)
        {
            // Пишем в стандартный AppData пользователя, у FullTrust приложений туда есть доступ
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(appData, "KTools", "bin");
        }

        // Для запуска из Visual Studio: используем папку сборки
        return Path.Combine(BaseDir, "bin");
    }

    /// <summary>
    /// Возвращает путь к директории для хранения конфигурационных файлов.
    /// 
    /// Для обычных приложений (Portable): попытается использовать папку приложения,
    /// если нет прав - использует LOCALAPPDATA.
    /// Для MSIX приложений: всегда использует LOCALAPPDATA (так как Program Files доступен только для чтения).
    /// </summary>
    /// <returns>Абсолютный путь к папке настроек.</returns>
    public static string GetSettingsDirectory()
    {
        bool isMsix = BaseDir.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase);

        if (isMsix)
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string msixSettingsPath = Path.Combine(appData, "KTools");
            if (!Directory.Exists(msixSettingsPath))
            {
                Directory.CreateDirectory(msixSettingsPath);
            }
            return msixSettingsPath;
        }

        // 1. Проверяем доступность папки приложения на запись (Portable/VS)
        string testFile = Path.Combine(BaseDir, ".write_test");
        try
        {
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
            return BaseDir;
        }
        catch (Exception)
        {
            // 2. Fallback в LOCALAPPDATA при отсутствии прав
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string fallbackPath = Path.Combine(appData, "KTools");

            if (!Directory.Exists(fallbackPath))
            {
                Directory.CreateDirectory(fallbackPath);
            }
            return fallbackPath;
        }
    }

    /// <summary>
    /// Найти путь к исполняемому файлу утилиты (ffmpeg, mkvmerge, eac3to и др.).
    /// </summary>
    /// <param name="binaryName">Имя бинарного файла утилиты.</param>
    /// <returns>Полный абсолютный путь к файлу утилиты на диске.</returns>
    public static string GetBinaryPath(string binaryName)
    {
        if (OperatingSystem.IsWindows() && !binaryName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            binaryName += ".exe";
        }

        // Замена имени в соответствии с соглашением именования K-Tools
        string targetName = binaryName.ToLowerInvariant() switch
        {
            "ffmpeg.exe" => "kt-ffmpeg.exe",
            "ffprobe.exe" => "kt-ffprobe.exe",
            _ => binaryName
        };

        // Определение подпапки в зависимости от типа утилиты
        string subfolder = GetSubfolderName(targetName);
        string binDir = GetBinDirectory();

        // 1. Поиск непосредственно в папке bin
        string pathInBin = Path.Combine(binDir, targetName);
        if (File.Exists(pathInBin))
        {
            return pathInBin;
        }

        // 2. Поиск в соответствующей подпапке (например, bin/ffmpeg/kt-ffmpeg.exe)
        string pathInSubfolder = Path.Combine(binDir, subfolder, targetName);
        if (File.Exists(pathInSubfolder))
        {
            return pathInSubfolder;
        }

        // 3. Резервный возврат имени файла (поиск в системном PATH операционной системы)
        return targetName;
    }

    /// <summary>
    /// Получить имя подпапки для группировки бинарных утилит в папке bin/.
    /// </summary>
    private static string GetSubfolderName(string binaryName)
    {
        string name = binaryName.Replace(".exe", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
        return name switch
        {
            "mkvmerge" => "mkvtoolnix",
            "ffprobe" => "ffmpeg",
            "ffmpeg" => "ffmpeg",
            "kt-ffmpeg" => "ffmpeg",
            "kt-ffprobe" => "ffmpeg",
            "deew" => "DEE",
            "dee" => "DEE",
            "qaac64" => "ffmpeg",
            _ => name
        };
    }
}
