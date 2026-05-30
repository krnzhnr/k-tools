// -*- coding: utf-8 -*-
using System;
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace KTools_App.Core;

/// <summary>
/// Модель данных, представляющая карточку скрипта обработки медиа.
/// Содержит метаданные и свойства для Fluent стилизации.
/// </summary>
public class ScriptInfo
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string IconName { get; set; } = string.Empty;
    public bool IsAvailable { get; set; } = true;

    /// <summary>
    /// Автоматическое сопоставление текстовой иконки с системным перечислением Symbol WinUI.
    /// </summary>
    public Symbol IconSymbol => IconName.ToLowerInvariant() switch
    {
        "video" => Symbol.Video,
        "share" => Symbol.Share,
        "forward" => Symbol.Forward,
        "delete" => Symbol.Delete,
        "clear" => Symbol.Delete,
        "music" => Symbol.Audio,
        "audio" => Symbol.Audio,
        "volume" => Symbol.Volume,
        "volume2" => Symbol.Volume,
        "sync" => Symbol.Sync,
        "refresh" => Symbol.Refresh,
        "map" => Symbol.Map,
        "add" => Symbol.Add,
        "list" => Symbol.List,
        "switch" => Symbol.Switch,
        "download" => Symbol.Download,
        "font" => Symbol.Font,
        "characters" => Symbol.Font,
        _ => Symbol.Document
    };

    /// <summary>
    /// Цвет фона для иконки с прозрачностью 20% в зависимости от категории медиа.
    /// </summary>
    public Brush CategoryBgColor => Category.Trim() switch
    {
        "Видео" => new SolidColorBrush(ColorHelper.FromArgb(0x33, 0x1B, 0x9D, 0xE3)),     // #331B9DE3 (Голубой)
        "Аудио" => new SolidColorBrush(ColorHelper.FromArgb(0x33, 0x28, 0xCA, 0xC6)),     // #3328CAC6 (Бирюзовый)
        "Контейнеры" => new SolidColorBrush(ColorHelper.FromArgb(0x33, 0xEB, 0x6E, 0x4D)), // #33EB6E4D (Терракотовый)
        "Субтитры" => new SolidColorBrush(ColorHelper.FromArgb(0x33, 0xA8, 0x78, 0xE8)),   // #33A878E8 (Фиолетовый)
        _ => new SolidColorBrush(ColorHelper.FromArgb(0x33, 0xFF, 0xFF, 0xFF))
    };

    /// <summary>
    /// Основной контрастный цвет иконки в зависимости от категории медиа.
    /// </summary>
    public Brush CategoryFgColor => Category.Trim() switch
    {
        "Видео" => new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x1B, 0x9D, 0xE3)),
        "Аудио" => new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x28, 0xCA, 0xC6)),
        "Контейнеры" => new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0xEB, 0x6E, 0x4D)),
        "Субтитры" => new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0xA8, 0x78, 0xE8)),
        _ => new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF))
    };
}
