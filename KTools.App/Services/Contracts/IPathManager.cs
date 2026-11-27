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
    /// Проверяет, работает ли приложение в portable-режиме
    /// (маркер portable.txt рядом с исполняемым файлом).
    /// </summary>
    bool IsPortableMode();

    /// <summary>
    /// Переключает разрешение путей на каталог установки после сбоя миграции.
    /// Приложение стартует «по-старому», повторная попытка — при следующем запуске.
    /// </summary>
    void EnterLegacyFallbackMode();

    /// <summary>
    /// Получить абсолютный путь к бинарному файлу утилиты.
    /// </summary>
    string GetBinaryPath(string binaryName);

    /// <summary>
    /// Задать путь к исполняемому файлу, указанный вручную (ишью 54).
    /// Такой путь имеет высший приоритет в <see cref="GetBinaryPath"/>:
    /// возвращается как есть, даже если встроенный бинарник тоже существует.
    /// Пустое значение сбрасывает переопределение к встроенным бинарникам.
    /// </summary>
    /// <param name="binaryName">Имя бинарного файла утилиты.</param>
    /// <param name="fullPath">Абсолютный путь к файлу или null/пусто для сброса.</param>
    void SetCustomBinaryPath(string binaryName, string? fullPath);

    /// <summary>
    /// Включить или выключить поиск бинарника в системном PATH (ишью 54).
    /// Поиск выполняется после явного пути пользователя и перед встроенной
    /// цепочкой bin/: при отсутствии файла в PATH используется встроенный бинарник.
    /// По умолчанию выключено: изоляция bin/ сохраняется.
    /// </summary>
    /// <param name="binaryName">Имя бинарного файла утилиты.</param>
    /// <param name="enabled">True — искать в системном PATH.</param>
    void SetUseSystemPath(string binaryName, bool enabled);

    /// <summary>
    /// Инвалидировать кэш резолва бинарных путей (ишью 54).
    /// Вызывается при смене источника бинарника, чтобы следующий
    /// <see cref="GetBinaryPath"/> заново вычислил путь.
    /// </summary>
    /// <param name="binaryName">Имя бинарника или null для очистки всего кэша.</param>
    void InvalidateBinaryPathCache(string? binaryName = null);

    /// <summary>
    /// Получить короткий путь в формате 8.3 для совместимости с устаревшими утилитами.
    /// </summary>
    string GetShortPath(string path);
}
