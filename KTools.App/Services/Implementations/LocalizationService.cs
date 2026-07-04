// -*- coding: utf-8 -*-
using System;
using Windows.ApplicationModel.Resources;
using Windows.Globalization;
using KTools_App.Services.Contracts;

namespace KTools_App.Services.Implementations;

/// <summary>
/// Реализация службы локализации с использованием MRT Core ResourceLoader.
/// </summary>
public sealed class LocalizationService : ILocalizationService
{
    private readonly ResourceLoader _resourceLoader;
    private readonly ILogService _logService;

    /// <inheritdoc />
    public event EventHandler<string>? LanguageChanged;

    /// <summary>
    /// Инициализирует новый экземпляр LocalizationService.
    /// </summary>
    public LocalizationService(ILogService logService)
    {
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _resourceLoader = ResourceLoader.GetForViewIndependentUse();
    }

    /// <inheritdoc />
    public string GetString(string key)
    {
        try
        {
            string value = _resourceLoader.GetString(key);
            if (string.IsNullOrEmpty(value))
            {
                _logService.Warn($"Ключ ресурса '{key}' не найден или пуст.", "LocalizationService");
                return key; // Возвращаем ключ как fallback
            }
            return value;
        }
        catch (Exception ex)
        {
            _logService.Exception(ex, $"Ошибка при получении строки ресурса для ключа: '{key}'", "LocalizationService");
            return key;
        }
    }

    /// <inheritdoc />
    public string CurrentLanguage
    {
        get
        {
            // Возвращаем переопределенный язык или текущий системный
            string overrideLang = ApplicationLanguages.PrimaryLanguageOverride;
            if (!string.IsNullOrEmpty(overrideLang))
            {
                return overrideLang;
            }
            
            if (ApplicationLanguages.Languages.Count > 0)
            {
                return ApplicationLanguages.Languages[0];
            }
            
            return "ru-RU";
        }
        set
        {
            string current = CurrentLanguage;
            if (!current.Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    ApplicationLanguages.PrimaryLanguageOverride = value;
                    _logService.Info($"Язык приложения переопределен на: '{value}' (предыдущий: '{current}')", "LocalizationService");
                    LanguageChanged?.Invoke(this, value);
                }
                catch (Exception ex)
                {
                    _logService.Exception(ex, $"Не удалось установить PrimaryLanguageOverride в '{value}'", "LocalizationService");
                }
            }
        }
    }
}
// -*- coding: utf-8 -*-
