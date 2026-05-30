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
    /// Найти путь к исполняемому файлу утилиты (ffmpeg, mkvmerge, eac3to и др.).
    /// Ищет как в локальной директории сборки, так и в корневом репозитории при разработке.
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

        // 1. Поиск в каталоге сборки (релизный режим)
        string localPath = Path.Combine(BaseDir, "bin", targetName);
        if (File.Exists(localPath))
        {
            return localPath;
        }

        // 2. Поиск в подпапках каталога сборки (релизный режим)
        string subfolderLocalPath = Path.Combine(BaseDir, "bin", subfolder, targetName);
        if (File.Exists(subfolderLocalPath))
        {
            return subfolderLocalPath;
        }

        // 3. Поиск в режиме разработки (поднимаемся до корня репозитория)
        // Структура: src-csharp/KTools.App/bin/Debug/net8.0-windows10... -> поднимаемся на 5 уровней вверх
        string devRepoRoot = Path.GetFullPath(Path.Combine(BaseDir, @"..\..\..\..\.."));
        
        string devPath = Path.Combine(devRepoRoot, "bin", subfolder, targetName);
        if (File.Exists(devPath))
        {
            return devPath;
        }

        string devPathRoot = Path.Combine(devRepoRoot, "bin", targetName);
        if (File.Exists(devPathRoot))
        {
            return devPathRoot;
        }

        // 4. Резервный возврат (поиск в системном PATH операционной системы)
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
