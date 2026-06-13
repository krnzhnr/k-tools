// -*- coding: utf-8 -*-
using KTools_App.Core;

namespace KTools_App.Models;

/// <summary>
/// Представляет отдельную запись в журнале событий (логе) приложения.
/// Хранит форматированный текст сообщения и уровень важности для последующей визуализации.
/// </summary>
public sealed class LogItem
{
    /// <summary>
    /// Возвращает или задает полный отформатированный текст сообщения лога.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Возвращает или задает уровень важности (критичности) данного события логирования.
    /// </summary>
    public LogLevel Level { get; set; }
}
