// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;

namespace KTools_App.Core;

/// <summary>
/// Чистая (без зависимостей от UI) оценка условий видимости и отключения полей настроек.
/// Универсальный механизм для всех кодеков и фильтров: условие, ссылающееся на скрытый
/// (невидимый при текущих настройках) управляющий параметр, игнорируется. Это исключает
/// взаимное глушение полей разных энкодеров (например, полей x265 чужим ключом auto_bitrate
/// от NVENC) без ручной настройки связок для каждого нового кодека.
/// Все комментарии выполнены исключительно на русском языке.
/// </summary>
public static class SettingConditionEvaluator
{
    /// <summary>
    /// Проверяет, отключает ли одно условие поле при заданном строковом значении управляющего параметра.
    /// Сравнение регистронезависимое, с учётом флага инверсии <paramref name="negate"/>.
    /// </summary>
    public static bool IsConditionMatch(IReadOnlyList<string> values, string? actualValue, bool negate)
    {
        bool matches = false;
        foreach (var val in values)
        {
            if (val.Equals(actualValue, StringComparison.OrdinalIgnoreCase))
            {
                matches = true;
                break;
            }
        }

        return negate ? !matches : matches;
    }

    /// <summary>
    /// Определяет, должно ли поле быть отключено.
    /// </summary>
    /// <param name="field">Оцениваемое поле.</param>
    /// <param name="getValue">Функция чтения текущего строкового значения настройки по ключу.</param>
    /// <param name="isControllerVisible">Функция проверки видимости управляющего параметра по ключу.
    /// Скрытый контроллер (например, настройка невыбранного энкодера) не влияет на активность поля.</param>
    /// <returns>True, если поле должно быть отключено.</returns>
    public static bool IsDisabled(
        SettingField field,
        Func<string, string?> getValue,
        Func<string, bool> isControllerVisible)
    {
        if (field.DisableConditions == null || field.DisableConditions.Count == 0)
        {
            return false;
        }

        foreach (var cond in field.DisableConditions)
        {
            // Универсальное правило: чужой скрытый контроллер игнорируется.
            if (!isControllerVisible(cond.Key))
            {
                continue;
            }

            if (IsConditionMatch(cond.Values, getValue(cond.Key), cond.Negate))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Определяет, должно ли поле быть видимым (логическое И по всем условиям видимости).
    /// </summary>
    public static bool IsVisible(
        SettingField field,
        Func<string, string?> getValue)
    {
        if (field.VisibilityConditions != null && field.VisibilityConditions.Count > 0)
        {
            foreach (var cond in field.VisibilityConditions)
            {
                if (!IsConditionMatch(cond.Values, getValue(cond.Key), cond.Negate))
                {
                    return false;
                }
            }

            return true;
        }

        if (string.IsNullOrEmpty(field.VisibleIfKey) || field.VisibleIfValues == null)
        {
            return true;
        }

        return IsConditionMatch(field.VisibleIfValues, getValue(field.VisibleIfKey), negate: false);
    }
}
