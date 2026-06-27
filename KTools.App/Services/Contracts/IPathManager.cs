// -*- coding: utf-8 -*-
namespace KTools_App.Services.Contracts;

/// <summary>
/// Интерфейс для поиска исполняемых файлов утилит и рабочих директорий приложения.
/// </summary>
public interface IPathManager
{
    /// <summary>
    /// Получить базовую директорию сборки приложения.
    /// </summary>
    string GetBaseDirectory();

    /// <summary>
    /// Получить директорию bin, в которой хранятся скачанные утилиты.
    /// </summary>
    string GetBinDirectory();

    /// <summary>
    /// Получить директорию для хранения файлов конфигурации.
    /// </summary>
    string GetSettingsDirectory();

    /// <summary>
    /// Получить абсолютный путь к бинарному файлу утилиты.
    /// </summary>
    string GetBinaryPath(string binaryName);

    /// <summary>
    /// Получить короткий путь в формате 8.3 для совместимости с устаревшими утилитами.
    /// </summary>
    string GetShortPath(string path);
}
