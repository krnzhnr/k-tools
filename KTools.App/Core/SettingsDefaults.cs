// -*- coding: utf-8 -*-
using System.Collections.Generic;

namespace KTools_App.Core;

/// <summary>
/// Содержит списки стандартных шаблонов поиска и замены для фильтрации и переименования файлов.
/// Все комментарии выполнены исключительно на русском языке.
/// </summary>
public static class SettingsDefaults
{
    /// <summary>
    /// Возвращает стандартные шаблоны регулярных выражений для поиска.
    /// </summary>
    public static List<TemplateItem> GetDefaultSearchTemplates()
    {
        return new List<TemplateItem>
        {
            new() { Pattern = " - (\\d+)", Description = "поиск серии" },
            new() { Pattern = "\\d+", Description = "поиск любых цифр" },
            new() { Pattern = "\\.mkv$", Description = "поиск расширения '.mkv'" },
            new() { Pattern = "\\s+", Description = "пробелы" },
            new() { Pattern = "[^a-zA-Z0-9]", Description = "спецсимволы" }
        };
    }

    /// <summary>
    /// Возвращает стандартные шаблоны подстановки для замены.
    /// </summary>
    public static List<TemplateItem> GetDefaultReplaceTemplates()
    {
        return new List<TemplateItem>
        {
            new() { Pattern = "$1", Description = "первая группа" },
            new() { Pattern = "$2", Description = "вторая группа" },
            new() { Pattern = " - [$1]", Description = "замена в [скобки]" },
            new() { Pattern = "${num}", Description = "порядковый номер" },
            new() { Pattern = "${num:2}", Description = "номер с нулями (01, 02)" },
            new() { Pattern = "${ruuidv4}", Description = "случайный UUID v4" },
            new() { Pattern = "${YYYY}", Description = "текущий год" },
            new() { Pattern = "${MM}", Description = "текущий месяц" },
            new() { Pattern = "${DD}", Description = "текущий день" },
            new() { Pattern = "${hh}", Description = "часы (24-часовой)" },
            new() { Pattern = "${mm}", Description = "минуты" },
            new() { Pattern = "${ss}", Description = "секунды" }
        };
    }
}
