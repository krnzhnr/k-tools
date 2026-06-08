// -*- coding: utf-8 -*-
using System;

namespace KTools_App.Core;

/// <summary>
/// Сохраненное состояние отдельного файла в очереди обработки.
/// Используется для сохранения и восстановления очереди файлов
/// при навигации между экранами скриптов без потери статуса.
/// </summary>
public sealed class SavedFileState
{
    /// <summary>
    /// Абсолютный путь к медиафайлу на диске.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Текстовый статус файла (например, "Ожидание", "Завершено", "Ошибка").
    /// </summary>
    public string Status { get; set; } = "Ожидание";

    /// <summary>
    /// Текущий прогресс обработки файла от 0.0 до 100.0.
    /// </summary>
    public double Progress { get; set; }

    /// <summary>
    /// Проанализированная техническая структура медиафайла.
    /// </summary>
    public MediaStructure? MediaInfo { get; set; }
}
