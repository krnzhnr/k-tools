// -*- coding: utf-8 -*-
using System;
using KTools_App.Core;

namespace KTools_App.Services.Contracts;

/// <summary>
/// Интерфейс для сервиса логирования событий приложения K-Tools.
/// </summary>
public interface ILogService
{
    /// <summary>
    /// Инициализировать файл лога и запустить очистку устаревших файлов.
    /// </summary>
    void InitializeLogFile();

    /// <summary>
    /// Инициализировать файл лога в кастомной директории.
    /// </summary>
    void InitializeLogFile(string? customLogDir);

    /// <summary>
    /// Записать событие с указанным уровнем детализации.
    /// </summary>
    /// <param name="level">Уровень критичности.</param>
    /// <param name="message">Сообщение.</param>
    /// <param name="source">Компонент-источник.</param>
    void Log(LogLevel level, string message, string source = "System");

    /// <summary>
    /// Записать отладочное сообщение.
    /// </summary>
    void DebugLog(string message, string source = "System");

    /// <summary>
    /// Записать информационное сообщение.
    /// </summary>
    void Info(string message, string source = "System");

    /// <summary>
    /// Записать предупреждение.
    /// </summary>
    void Warn(string message, string source = "System");

    /// <summary>
    /// Записать ошибку.
    /// </summary>
    void Error(string message, string source = "System");

    /// <summary>
    /// Записать фатальную ошибку.
    /// </summary>
    void Fatal(string message, string source = "System");

    /// <summary>
    /// Записать перехваченное исключение с детализацией стека вызовов.
    /// </summary>
    void Exception(Exception ex, string message, string source = "System");

    /// <summary>
    /// Прочитать весь текст текущего файла логов.
    /// </summary>
    string ReadCurrentLog();

    /// <summary>
    /// Полностью очистить содержимое текущего файла логов.
    /// </summary>
    void ClearCurrentLog();
}
