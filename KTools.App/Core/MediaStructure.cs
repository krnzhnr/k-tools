// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;

namespace KTools_App.Core;

/// <summary>
/// Представляет полную техническую структуру медиафайла, включая все дорожки
/// (видео, аудио, субтитры) и встроенные вложения (шрифты и др.).
/// Все комментарии выполнены исключительно на русском языке.
/// </summary>
public sealed class MediaStructure
{
    /// <summary>
    /// Абсолютный путь к проанализированному медиафайлу на диске.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Общая длительность воспроизведения медиафайла в секундах.
    /// </summary>
    public double Duration { get; set; }

    /// <summary>
    /// Полный список всех обнаруженных дорожек в контейнере.
    /// </summary>
    public List<MediaTrack> Tracks { get; } = new();

    /// <summary>
    /// Полный список встроенных вложений (например, файлов шрифтов).
    /// </summary>
    public List<MediaAttachment> Attachments { get; } = new();

    /// <summary>
    /// Возвращает список только видеодорожек.
    /// </summary>
    public IReadOnlyList<MediaTrack> GetVideoTracks()
    {
        return Tracks
            .Where(t => t.TrackType.Equals("video", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Возвращает список только аудиодорожек.
    /// </summary>
    public IReadOnlyList<MediaTrack> GetAudioTracks()
    {
        return Tracks
            .Where(t => t.TrackType.Equals("audio", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Возвращает список только дорожек субтитров.
    /// </summary>
    public IReadOnlyList<MediaTrack> GetSubtitleTracks()
    {
        return Tracks
            .Where(t => t.TrackType.Equals("subtitles", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Возвращает список только вложенных файлов шрифтов.
    /// </summary>
    public IReadOnlyList<MediaAttachment> GetFontAttachments()
    {
        return Attachments
            .Where(a => a.IsFont)
            .ToList();
    }
}
