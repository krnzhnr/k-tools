// -*- coding: utf-8 -*-
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace KTools_App.Core;

/// <summary>
/// Модель данных, представляющая внешнюю бинарную зависимость приложения (например, FFmpeg или MKVToolNix).
/// Содержит метаданные для отображения в интерфейсе и параметры для скачивания/верификации.
/// </summary>
public class DependencyInfo
{
    /// <summary>
    /// Уникальный текстовый идентификатор зависимости (ключ).
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Отображаемое имя зависимости в интерфейсе пользователя.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Подробное описание назначения зависимости.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Имя иконки для визуального сопоставления.
    /// </summary>
    public string IconName { get; set; } = string.Empty;

    /// <summary>
    /// Подпапка внутри каталога bin/, куда будет распакована зависимость.
    /// </summary>
    public string Subfolder { get; set; } = string.Empty;

    /// <summary>
    /// Приблизительный размер зависимости на диске после распаковки (в мегабайтах).
    /// </summary>
    public double SizeMb { get; set; }

    /// <summary>
    /// Имя архивного файла (.tar.xz) в облачном хранилище релизов.
    /// </summary>
    public string ArchiveName { get; set; } = string.Empty;

    /// <summary>
    /// Относительный путь к исполняемому файлу-маркеру для проверки успешности установки.
    /// </summary>
    public string VerifyBinary { get; set; } = string.Empty;

    /// <summary>
    /// Указывает, является ли зависимость строго обязательной для базовой работы приложения.
    /// </summary>
    public bool IsRequired { get; set; }

    /// <summary>
    /// Возвращает соответствующую системную иконку WinUI для отображения на карточке.
    /// </summary>
    public Symbol IconSymbol => IconName.ToLowerInvariant() switch
    {
        "video" => Symbol.Video,
        "share" => Symbol.Share,
        "audio" => Symbol.Audio,
        "music" => Symbol.Audio,
        "headphone" => Symbol.Audio,
        _ => Symbol.Document
    };

    /// <summary>
    /// Возвращает цвет фона подложки иконки с прозрачностью 20% в зависимости от типа зависимости.
    /// </summary>
    public Brush CategoryBgColor => Key.ToLowerInvariant() switch
    {
        "ffmpeg" => new SolidColorBrush(ColorHelper.FromArgb(0x33, 0x1B, 0x9D, 0xE3)),       // Голубой
        "mkvtoolnix" => new SolidColorBrush(ColorHelper.FromArgb(0x33, 0xEB, 0x6E, 0x4D)),   // Терракотовый
        "eac3to" => new SolidColorBrush(ColorHelper.FromArgb(0x33, 0x28, 0xCA, 0xC6)),       // Бирюзовый
        "dee" => new SolidColorBrush(ColorHelper.FromArgb(0x33, 0x28, 0xCA, 0xC6)),          // Бирюзовый
        _ => new SolidColorBrush(ColorHelper.FromArgb(0x33, 0xFF, 0xFF, 0xFF))
    };

    /// <summary>
    /// Возвращает основной контрастный цвет иконки в зависимости от типа зависимости.
    /// </summary>
    public Brush CategoryFgColor => Key.ToLowerInvariant() switch
    {
        "ffmpeg" => new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x1B, 0x9D, 0xE3)),
        "mkvtoolnix" => new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0xEB, 0x6E, 0x4D)),
        "eac3to" => new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x28, 0xCA, 0xC6)),
        "dee" => new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x28, 0xCA, 0xC6)),
        _ => new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF))
    };
}
