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
        int colSpan = 2,
        string? visibleIfKey = null,
        List<string>? visibleIfValues = null,
        bool requiresWarning = false,
        string? warningTitle = null,
        string? warningText = null,
        List<SettingVisibilityCondition>? visibilityConditions = null,
        List<SettingDisableCondition>? disableConditions = null,
        double? minimum = null,
        double? maximum = null)
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
        VisibleIfKey = visibleIfKey;
        VisibleIfValues = visibleIfValues;
        RequiresWarning = requiresWarning;
        WarningTitle = warningTitle;
        WarningText = warningText;
        VisibilityConditions = visibilityConditions;
        DisableConditions = disableConditions;
        Minimum = minimum;
        Maximum = maximum;
    }

    /// <summary>
    /// Минимальное допустимое значение для числовых полей ввода (SettingType.Int / Number).
    /// </summary>
    public double? Minimum { get; }

    /// <summary>
    /// Максимальное допустимое значение для числовых полей ввода (SettingType.Int / Number).
    /// </summary>
    public double? Maximum { get; }

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

    /// <summary>
    /// Имя настройки, от которой зависит видимость данного поля.
    /// </summary>
    public string? VisibleIfKey { get; }

    /// <summary>
    /// Список значений управляющей настройки, при которых данное поле должно быть видимым.
    /// </summary>
    public List<string>? VisibleIfValues { get; }

    /// <summary>
    /// Список множественных условий видимости (логическое И), управляющих этим полем.
    /// </summary>
    public List<SettingVisibilityCondition>? VisibilityConditions { get; set; }

    /// <summary>
    /// Список множественных условий отключения (логическое И), управляющих активностью этого поля.
    /// </summary>
    public List<SettingDisableCondition>? DisableConditions { get; set; }

    /// <summary>
    /// Флаг необходимости показа предупреждающего диалога при выключении чекбокса.
    /// </summary>
    public bool RequiresWarning { get; }

    /// <summary>
    /// Заголовок предупреждающего окна.
    /// </summary>
    public string? WarningTitle { get; }

    /// <summary>
    /// Текст предупреждения о возможных последствиях отключения опции.
    /// </summary>
    public string? WarningText { get; }

    /// <summary>
    /// Текст-подсказка (плейсхолдер) для отображения в пустом поле ввода.
    /// </summary>
    public string PlaceholderText { get; set; } = string.Empty;
}

/// <summary>
/// Представляет одно условие видимости параметра в пользовательском интерфейсе.
/// Позволяет связать видимость поля с текущим значением другого параметра.
/// Все комментарии и документация выполнены на русском языке.
/// </summary>
public sealed class SettingVisibilityCondition
{
    /// <summary>
    /// Ключ управляющего параметра, от которого зависит видимость.
    /// </summary>
    public string Key { get; }

    /// <summary>
    /// Список строковых значений управляющего параметра, при которых условие считается истинным.
    /// </summary>
    public List<string> Values { get; }

    /// <summary>
    /// Если установлено в true, условие инвертируется (поле видно, если значение НЕ совпадает ни с одним из списка).
    /// </summary>
    public bool Negate { get; }

    /// <summary>
    /// Инициализирует новое условие видимости с списком значений.
    /// </summary>
    public SettingVisibilityCondition(string key, List<string> values, bool negate = false)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Values = values ?? throw new ArgumentNullException(nameof(values));
        Negate = negate;
    }

    /// <summary>
    /// Инициализирует новое условие видимости для одного конкретного значения.
    /// </summary>
    public SettingVisibilityCondition(string key, string value, bool negate = false)
        : this(key, new List<string> { value }, negate)
    {
    }
}

/// <summary>
/// Представляет одно условие отключения (disable) параметра в пользовательском интерфейсе.
/// Позволяет связать активность поля с текущим значением другого параметра.
/// Все комментарии и документация выполнены на русском языке.
/// </summary>
public sealed class SettingDisableCondition
{
    /// <summary>
    /// Ключ управляющего параметра, от которого зависит отключение.
    /// </summary>
    public string Key { get; }

    /// <summary>
    /// Список строковых значений управляющего параметра, при которых условие считается истинным (поле отключается).
    /// </summary>
    public List<string> Values { get; }

    /// <summary>
    /// Если установлено в true, условие инвертируется (поле отключается, если значение НЕ совпадает ни с одним из списка).
    /// </summary>
    public bool Negate { get; }

    /// <summary>
    /// Инициализирует новое условие отключения с списком значений.
    /// </summary>
    public SettingDisableCondition(string key, List<string> values, bool negate = false)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Values = values ?? throw new ArgumentNullException(nameof(values));
        Negate = negate;
    }

    /// <summary>
    /// Инициализирует новое условие отключения для одного конкретного значения.
    /// </summary>
    public SettingDisableCondition(string key, string value, bool negate = false)
        : this(key, new List<string> { value }, negate)
    {
    }
}
