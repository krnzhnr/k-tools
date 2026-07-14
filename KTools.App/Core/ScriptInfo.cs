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
    /// Непрозрачность карточки (уменьшается для недоступных скриптов).
    /// </summary>
    public double CardOpacity => IsAvailable ? 1.0 : 0.4;



    /// <summary>
    /// Цвет фона для иконки с прозрачностью 20% в зависимости от категории медиа.
    /// </summary>
    public Brush CategoryBgColor => Category.Trim() switch
    {
        AppConstants.ScriptCategory.Video => new SolidColorBrush(AppConstants.CategoryColors.VideoBg),
        AppConstants.ScriptCategory.Audio => new SolidColorBrush(AppConstants.CategoryColors.AudioBg),
        AppConstants.ScriptCategory.Containers => new SolidColorBrush(AppConstants.CategoryColors.ContainersBg),
        AppConstants.ScriptCategory.Subtitles => new SolidColorBrush(AppConstants.CategoryColors.SubtitlesBg),
        AppConstants.ScriptCategory.Network => new SolidColorBrush(AppConstants.CategoryColors.NetworkBg),
        AppConstants.ScriptCategory.Tools => new SolidColorBrush(AppConstants.CategoryColors.ToolsBg),
        _ => new SolidColorBrush(AppConstants.CategoryColors.DefaultBg)
    };

    /// <summary>
    /// Основной контрастный цвет иконки в зависимости от категории медиа.
    /// </summary>
    public Brush CategoryFgColor => Category.Trim() switch
    {
        AppConstants.ScriptCategory.Video => new SolidColorBrush(AppConstants.CategoryColors.VideoFg),
        AppConstants.ScriptCategory.Audio => new SolidColorBrush(AppConstants.CategoryColors.AudioFg),
        AppConstants.ScriptCategory.Containers => new SolidColorBrush(AppConstants.CategoryColors.ContainersFg),
        AppConstants.ScriptCategory.Subtitles => new SolidColorBrush(AppConstants.CategoryColors.SubtitlesFg),
        AppConstants.ScriptCategory.Network => new SolidColorBrush(AppConstants.CategoryColors.NetworkFg),
        AppConstants.ScriptCategory.Tools => new SolidColorBrush(AppConstants.CategoryColors.ToolsFg),
        _ => new SolidColorBrush(AppConstants.CategoryColors.DefaultFg)
    };
}
