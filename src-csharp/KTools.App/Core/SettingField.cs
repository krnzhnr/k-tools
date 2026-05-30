// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;

namespace KTools_App.Core;

/// <summary>
/// Класс-описание одного поля параметров скрипта медиаобработки.
/// Используется для декларативного объявления настроек в скриптах
/// и автоматической динамической генерации Fluent UI на странице WorkPanel.
/// </summary>
public class SettingField
{
    /// <summary>
    /// Инициализирует новый экземпляр класса описания настройки.
    /// </summary>
    public SettingField(
        string key,
        string label,
        SettingType type,
        object? defaultValue = null,
        string group = "Общие",
        string comment = "",
        List<string>? options = null,
        int column = 0,
        int colSpan = 2)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Label = label ?? throw new ArgumentNullException(nameof(label));
        Type = type;
        DefaultValue = defaultValue ?? string.Empty;
        Group = group ?? "Общие";
        Comment = comment ?? string.Empty;
        Options = options ?? new List<string>();
        Column = column;
        ColSpan = colSpan;
    }

    /// <summary>
    /// Уникальный текстовый идентификатор настройки (ключ в конфигурации).
    /// </summary>
    public string Key { get; }

    /// <summary>
    /// Отображаемая русская текстовая метка параметра в пользовательском интерфейсе.
    /// </summary>
    public string Label { get; }

    /// <summary>
    /// Тип элемента управления, который должен визуализировать параметр.
    /// </summary>
    public SettingType Type { get; }

    /// <summary>
    /// Значение настройки по умолчанию.
    /// </summary>
    public object DefaultValue { get; }

    /// <summary>
    /// Категория группировки настроек (формат: "ГлавнаяГруппа:Подгруппа").
    /// </summary>
    public string Group { get; }

    /// <summary>
    /// Всплывающий комментарий, описывающий назначение параметра.
    /// </summary>
    public string Comment { get; }

    /// <summary>
    /// Список вариантов для выбора, если параметр имеет тип SettingType.Combo.
    /// </summary>
    public List<string> Options { get; }

    /// <summary>
    /// Индекс колонки размещения в сетке XAML (0 или 1).
    /// </summary>
    public int Column { get; }

    /// <summary>
    /// Сколько колонок сетки занимает параметр в ширину.
    /// </summary>
    public int ColSpan { get; }
}
