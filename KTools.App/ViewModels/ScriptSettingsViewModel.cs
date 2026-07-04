// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using KTools_App.Core;
using KTools_App.Services.Contracts;

namespace KTools_App.ViewModels;

/// <summary>
/// Модель представления для динамической настройки параметров скрипта.
/// Все комментарии и логирование выполнены исключительно на русском языке.
/// </summary>
public sealed partial class ScriptSettingsViewModel : ThreadSafeViewModel
{
    private readonly ISettingsManager _settingsManager;
    private readonly ILogService _logService;

    [ObservableProperty]
    private AbstractScript? _activeScript;

    [ObservableProperty]
    private string _settingsGroup = string.Empty;

    public ScriptSettingsViewModel(ISettingsManager settingsManager, ILogService logService)
    {
        _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
    }

    /// <summary>
    /// Инициализирует настройки для конкретного скрипта.
    /// </summary>
    public void InitializeScript(AbstractScript script)
    {
        ActiveScript = script;
        SettingsGroup = _settingsManager.GetSafeGroupName(script.Name);
    }

    /// <summary>
    /// Возвращает сохраненное значение настройки.
    /// </summary>
    public T GetSetting<T>(string key, T defaultValue)
    {
        if (string.IsNullOrEmpty(SettingsGroup)) return defaultValue;
        return _settingsManager.GetSetting(SettingsGroup, key, defaultValue);
    }

    /// <summary>
    /// Сохраняет значение настройки.
    /// </summary>
    public void SaveSetting<T>(string key, T value)
    {
        if (string.IsNullOrEmpty(SettingsGroup)) return;
        _settingsManager.SetSetting(SettingsGroup, key, value);
        _logService.Info($"Сохранена настройка: [{SettingsGroup}] {key} = {value}", "ScriptSettingsViewModel");
    }
}
