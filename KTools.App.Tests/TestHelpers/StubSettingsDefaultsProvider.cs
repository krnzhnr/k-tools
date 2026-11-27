// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using KTools_App.Services.Contracts;

namespace KTools_App.Tests.TestHelpers;

/// <summary>
/// Тестовый поставщик значений по умолчанию для уровня настроек.
/// Позволяет проверять SettingsManager через абстракцию <see cref="ISettingsDefaultsProvider"/>
/// без зависимости от конкретных скриптов.
/// </summary>
public sealed class StubSettingsDefaultsProvider : ISettingsDefaultsProvider
{
    private readonly IReadOnlyList<ScriptSettingsDefaults> _defaults;

    /// <summary>
    /// Поставщик без скриптов: ни одна секция Script_* не считается известной.
    /// </summary>
    public static StubSettingsDefaultsProvider Empty { get; } =
        new StubSettingsDefaultsProvider(Array.Empty<ScriptSettingsDefaults>());

    /// <summary>
    /// Инициализирует поставщика фиксированным набором описаний скриптов.
    /// </summary>
    /// <param name="defaults">Описания скриптов; null трактуется как пустой набор.</param>
    public StubSettingsDefaultsProvider(IReadOnlyList<ScriptSettingsDefaults>? defaults = null)
    {
        _defaults = defaults ?? Array.Empty<ScriptSettingsDefaults>();
    }

    /// <inheritdoc/>
    public IReadOnlyList<ScriptSettingsDefaults> GetScriptDefaults()
    {
        return _defaults;
    }
}
