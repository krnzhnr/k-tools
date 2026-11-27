// -*- coding: utf-8 -*-
using System;

namespace KTools_App.Core;

/// <summary>
/// Направление сдвига времени для скриптов сдвига и калькулятора тайминга.
/// Все комментарии выполнены на русском языке.
/// </summary>
public enum ShiftDirection
{
    /// <summary>Сдвиг вперёд (увеличение таймингов).</summary>
    Forward = 0,

    /// <summary>Сдвиг назад (уменьшение таймингов).</summary>
    Backward = 1,
}

/// <summary>
/// Единый источник строк направления сдвига и разбора сохранённых значений.
/// Каноническая метка «вперёд» пишется с «ё»; устаревшее написание без «ё»
/// распознаётся для совместимости с ранее сохранёнными настройками и историей.
/// </summary>
public static class ShiftDirectionLabels
{
    /// <summary>Каноническая метка направления «вперёд».</summary>
    public const string Forward = "Вперёд";

    /// <summary>Устаревшее написание «вперёд» без «ё» (ранее сохранённые настройки).</summary>
    public const string ForwardLegacy = "Вперед";

    /// <summary>Метка направления «назад».</summary>
    public const string Backward = "Назад";

    /// <summary>Метка совпадения таймингов в истории калькулятора.</summary>
    public const string Match = "Совпадает";

    /// <summary>
    /// Проверяет, означает ли значение направление «вперёд» (с учётом legacy-написания).
    /// </summary>
    public static bool IsForwardLabel(string? value)
    {
        return string.Equals(value, Forward, StringComparison.Ordinal)
            || string.Equals(value, ForwardLegacy, StringComparison.Ordinal);
    }

    /// <summary>
    /// Разбирает строковое значение в направление; неизвестные значения считаются сдвигом назад,
    /// сохраняя прежнее поведение ветвления скриптов.
    /// </summary>
    public static ShiftDirection Parse(string? value)
    {
        return IsForwardLabel(value) ? ShiftDirection.Forward : ShiftDirection.Backward;
    }

    /// <summary>
    /// Возвращает каноническую метку направления.
    /// </summary>
    public static string GetLabel(ShiftDirection direction)
    {
        return direction == ShiftDirection.Forward ? Forward : Backward;
    }
}
