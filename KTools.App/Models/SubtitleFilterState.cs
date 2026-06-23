// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;

namespace KTools_App.Models;

/// <summary>
/// Класс состояния фильтрации субтитров.
/// Хранит настройки исключения по актёрам, стилям, эффектам, а также ручные включения/исключения реплик.
/// </summary>
public sealed class SubtitleFilterState
{
    /// <summary>
    /// Список исключенных актёров.
    /// </summary>
    public HashSet<string> ExcludedActors { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Список исключенных стилей.
    /// </summary>
    public HashSet<string> ExcludedStyles { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Список исключенных эффектов.
    /// </summary>
    public HashSet<string> ExcludedEffects { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Флаг удаления тегов форматирования.
    /// </summary>
    public bool StripFormatting { get; set; } = true;

    /// <summary>
    /// Флаг удаления капса (текста в верхнем регистре).
    /// </summary>
    public bool StripCaps { get; set; } = false;

    /// <summary>
    /// Ручные включения реплик (ключ - путь к файлу субтитров, значение - набор индексов строк диалогов).
    /// Строки из этого списка принудительно сохраняются, даже если они попали под фильтры.
    /// </summary>
    public Dictionary<string, HashSet<int>> ManualInclusions { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Ручные исключения реплик (ключ - путь к файлу субтитров, значение - набор индексов строк диалогов).
    /// Строки из этого списка принудительно удаляются, даже если они прошли фильтры.
    /// </summary>
    public Dictionary<string, HashSet<int>> ManualExclusions { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Очистить все сохраненные настройки фильтрации.
    /// </summary>
    public void Clear()
    {
        ExcludedActors.Clear();
        ExcludedStyles.Clear();
        ExcludedEffects.Clear();
        ManualInclusions.Clear();
        ManualExclusions.Clear();
    }
}
