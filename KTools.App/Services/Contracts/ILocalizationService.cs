// -*- coding: utf-8 -*-
using System;

namespace KTools_App.Services.Contracts;

/// <summary>
/// Интерфейс службы локализации для управления ресурсами мультиязычности и смены языка.
/// </summary>
public interface ILocalizationService
{
    /// <summary>
    /// Возвращает локализованную строку по ее ключу.
    /// </summary>
    /// <param name="key">Ключ ресурса.</param>
    /// <returns>Локализованная строка.</returns>
    string GetString(string key);

    /// <summary>
    /// Текущий активный язык приложения (например, "ru-RU" или "en-US").
    /// </summary>
    string CurrentLanguage { get; set; }

    /// <summary>
    /// Событие, возникающее при изменении языка интерфейса.
    /// </summary>
    event EventHandler<string>? LanguageChanged;
}
