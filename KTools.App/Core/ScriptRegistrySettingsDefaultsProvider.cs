// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;

using KTools_App.Services.Contracts;

namespace KTools_App.Core;

/// <summary>
/// Поставщик значений по умолчанию поверх реестра скриптов.
/// Преобразует доменные схемы скриптов в плоские описания <see cref="ScriptSettingsDefaults"/>,
/// чтобы уровень настроек не зависел от <see cref="AbstractScript"/> (DIP).
/// Неперсистентные поля оформления (подзаголовки) уровню настроек не передаются.
/// </summary>
public sealed class ScriptRegistrySettingsDefaultsProvider : ISettingsDefaultsProvider
{
    private readonly IScriptRegistry? _registry;
    private readonly IReadOnlyList<AbstractScript>? _scripts;

    /// <summary>
    /// Инициализирует поставщика поверх реестра скриптов.
    /// Не вызывать изнутри фабрики ленивого построения самого реестра
    /// (рекурсивный доступ к Scripts бросает InvalidOperationException):
    /// там используйте перегрузку с готовым списком.
    /// </summary>
    /// <param name="registry">Реестр зарегистрированных скриптов.</param>
    public ScriptRegistrySettingsDefaultsProvider(IScriptRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <summary>
    /// Инициализирует поставщика поверх уже построенного списка скриптов.
    /// Предназначен для фабрики реестра: список передаётся напрямую,
    /// без повторного обращения к ленивому свойству Scripts.
    /// </summary>
    /// <param name="scripts">Готовый список скриптов.</param>
    public ScriptRegistrySettingsDefaultsProvider(IReadOnlyList<AbstractScript> scripts)
    {
        _scripts = scripts ?? throw new ArgumentNullException(nameof(scripts));
    }

    /// <inheritdoc/>
    public IReadOnlyList<ScriptSettingsDefaults> GetScriptDefaults()
    {
        IReadOnlyList<AbstractScript> scripts = _scripts ?? _registry!.Scripts;
        var result = new List<ScriptSettingsDefaults>(scripts.Count);
        foreach (AbstractScript script in scripts)
        {
            if (script is null)
            {
                continue;
            }

            result.Add(new ScriptSettingsDefaults(script.Name, MapFields(script.SettingsSchema)));
        }

        return result;
    }

    private static IReadOnlyList<SettingDefaultValue> MapFields(IReadOnlyList<SettingField> schema)
    {
        var mapped = new List<SettingDefaultValue>(schema.Count);
        foreach (SettingField field in schema)
        {
            if (field is null || field.Type == SettingType.Subtitle)
            {
                continue;
            }

            mapped.Add(new SettingDefaultValue(
                field.Key,
                field.DefaultValue,
                MapFields(field.ChildFields)));
        }

        return mapped;
    }
}
