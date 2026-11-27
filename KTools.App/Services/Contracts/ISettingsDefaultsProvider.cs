// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;

namespace KTools_App.Services.Contracts;

/// <summary>
/// Одно значение по умолчанию из схемы скрипта в формате, понятном уровню настроек:
/// ключ, значение и вложенные значения раскрывающихся блоков.
/// Поля оформления без сохраняемого значения (подзаголовки) сюда не попадают:
/// их отсекает поставщик, уровень настроек о них не знает.
/// </summary>
public sealed class SettingDefaultValue
{
    /// <summary>
    /// Инициализирует новое значение по умолчанию.
    /// </summary>
    /// <param name="key">Ключ настройки в секции скрипта.</param>
    /// <param name="defaultValue">Значение по умолчанию.</param>
    /// <param name="children">Вложенные значения для составных полей.</param>
    public SettingDefaultValue(
        string key,
        object? defaultValue,
        IReadOnlyList<SettingDefaultValue>? children = null)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        DefaultValue = defaultValue;
        Children = children ?? Array.Empty<SettingDefaultValue>();
    }

    /// <summary>
    /// Ключ настройки в секции скрипта.
    /// </summary>
    public string Key { get; }

    /// <summary>
    /// Значение по умолчанию.
    /// </summary>
    public object? DefaultValue { get; }

    /// <summary>
    /// Вложенные значения для составных полей.
    /// </summary>
    public IReadOnlyList<SettingDefaultValue> Children { get; }
}

/// <summary>
/// Описание значений по умолчанию одного скрипта: имя и набор полей.
/// Уровень настроек вычисляет имя секции через <see cref="ISettingsManager.GetSafeGroupName"/>.
/// </summary>
public sealed class ScriptSettingsDefaults
{
    /// <summary>
    /// Инициализирует описание значений по умолчанию скрипта.
    /// </summary>
    /// <param name="scriptName">Имя скрипта.</param>
    /// <param name="fields">Поля схемы со значениями по умолчанию.</param>
    public ScriptSettingsDefaults(string scriptName, IReadOnlyList<SettingDefaultValue>? fields = null)
    {
        ScriptName = scriptName ?? throw new ArgumentNullException(nameof(scriptName));
        Fields = fields ?? Array.Empty<SettingDefaultValue>();
    }

    /// <summary>
    /// Имя скрипта.
    /// </summary>
    public string ScriptName { get; }

    /// <summary>
    /// Поля схемы со значениями по умолчанию.
    /// </summary>
    public IReadOnlyList<SettingDefaultValue> Fields { get; }
}

/// <summary>
/// Поставщик схем значений по умолчанию для уровня настроек.
/// Изолирует <see cref="ISettingsManager"/> от конкретных скриптов (DIP):
/// менеджер настроек работает только с этим интерфейсом,
/// а реализация строится поверх <see cref="IScriptRegistry"/>.
/// </summary>
public interface ISettingsDefaultsProvider
{
    /// <summary>
    /// Возвращает описания значений по умолчанию всех зарегистрированных скриптов.
    /// </summary>
    IReadOnlyList<ScriptSettingsDefaults> GetScriptDefaults();
}
