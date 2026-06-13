// -*- coding: utf-8 -*-
using System;

namespace KTools_App.Core;

/// <summary>
/// Представляет подробную техническую информацию об отдельной дорожке
/// (видео, аудио или субтитры) внутри медиа-контейнера.
/// Все комментарии выполнены исключительно на русском языке.
/// </summary>
public sealed class MediaTrack
{
    /// <summary>
    /// Идентификатор дорожки в контейнере.
    /// Соответствует индексу потока ffmpeg или ID трека в mkvmerge.
    /// </summary>
    public int TrackId { get; set; }

    /// <summary>
    /// Тип дорожки в нижнем регистре (например: "video", "audio", "subtitles").
    /// </summary>
    public string TrackType { get; set; } = string.Empty;

    /// <summary>
    /// Название кодека дорожки (например: "h264", "hevc", "ac3", "srt").
    /// </summary>
    public string Codec { get; set; } = string.Empty;

    /// <summary>
    /// Двух- или трехбуквенный код языка дорожки (например: "rus", "eng").
    /// </summary>
    public string Language { get; set; } = "und";

    /// <summary>
    /// Пользовательский заголовок дорожки (например: "Дубляж [Line]").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Геометрическое разрешение кадра для видео (например: "1920x1080").
    /// Для аудио и субтитров возвращает пустую строку.
    /// </summary>
    public string Resolution { get; set; } = string.Empty;

    /// <summary>
    /// Количество каналов для аудиодорожки (например: 2 для стерео, 6 для 5.1).
    /// </summary>
    public int Channels { get; set; }

    /// <summary>
    /// Флаг, определяющий, является ли дорожка дорожкой по умолчанию.
    /// </summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// Флаг принудительного воспроизведения дорожки (forced track).
    /// </summary>
    public bool IsForced { get; set; }

    /// <summary>
    /// Флаг дорожки, подготовленной для людей с нарушениями слуха.
    /// </summary>
    public bool IsHearingImpaired { get; set; }

    /// <summary>
    /// Флаг дорожки, содержащей аудиокомментарии создателей.
    /// </summary>
    public bool IsCommentary { get; set; }

    /// <summary>
    /// Флаг оригинальной языковой аудиодорожки.
    /// </summary>
    public bool IsOriginal { get; set; }

    /// <summary>
    /// Флаг дорожки для людей с нарушениями зрения (описание видеоряда).
    /// </summary>
    public bool IsVisualImpaired { get; set; }

    /// <summary>
    /// Возвращает локализованное на русский язык название типа дорожки.
    /// </summary>
    public string TypeLabel
    {
        get
        {
            return TrackType.ToLowerInvariant() switch
            {
                "video" => "Видео",
                "audio" => "Аудио",
                "subtitles" => "Субтитры",
                _ => TrackType
            };
        }
    }
}
