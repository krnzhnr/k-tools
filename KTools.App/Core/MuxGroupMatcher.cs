// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;

namespace KTools_App.Core;

/// <summary>
/// Единый источник правды для сопоставления сопутствующих файлов с видео при сборке MKV.
/// Правило: точное совпадение базового имени (stem) либо префиксное совпадение
/// через разделитель (например, "video.dub", "video_en", "video [LostFilm]").
/// Используется и скриптом сборки, и таблицей муксинга в UI во избежание рассинхрона.
/// Все комментарии на русском языке.
/// </summary>
public static class MuxGroupMatcher
{
    /// <summary>
    /// Разделители между базовым именем видео и суффиксом сопутствующего файла.
    /// </summary>
    public static readonly char[] Delimiters = ['.', '_', '-', ' ', '(', '[', '{'];

    /// <summary>
    /// Проверяет, является ли файл с именем companionStem сопутствующим для видео videoStem.
    /// </summary>
    public static bool IsCompanionOf(string videoStem, string companionStem)
    {
        if (string.IsNullOrEmpty(videoStem) || string.IsNullOrEmpty(companionStem))
        {
            return false;
        }

        if (companionStem.Equals(videoStem, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (companionStem.Length <= videoStem.Length)
        {
            return false;
        }

        if (!companionStem.StartsWith(videoStem, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        char boundary = companionStem[videoStem.Length];
        foreach (char delimiter in Delimiters)
        {
            if (boundary == delimiter)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Проверяет принадлежность сопутствующего файла видео: ручной пин приоритетнее автосопоставления.
    /// </summary>
    public static bool BelongsToVideo(string videoStem, string companionStem, string? pinnedStem)
    {
        if (!string.IsNullOrEmpty(pinnedStem))
        {
            return pinnedStem.Equals(videoStem, StringComparison.OrdinalIgnoreCase);
        }

        return IsCompanionOf(videoStem, companionStem);
    }

    /// <summary>
    /// Находит наиболее подходящее видео для сопутствующего файла.
    /// Точное совпадение приоритетнее префиксного; среди префиксных выбирается самое длинное имя.
    /// </summary>
    /// <param name="videoStems">Базовые имена видеофайлов-кандидатов.</param>
    /// <param name="companionStem">Базовое имя сопутствующего файла.</param>
    /// <returns>Имя видео или null, если совпадений нет.</returns>
    public static string? FindBestVideoStem(IEnumerable<string> videoStems, string companionStem)
    {
        if (videoStems == null || string.IsNullOrEmpty(companionStem))
        {
            return null;
        }

        string? bestPrefix = null;
        foreach (string videoStem in videoStems)
        {
            if (string.IsNullOrEmpty(videoStem))
            {
                continue;
            }

            if (companionStem.Equals(videoStem, StringComparison.OrdinalIgnoreCase))
            {
                return videoStem;
            }

            if (IsCompanionOf(videoStem, companionStem))
            {
                if (bestPrefix == null || videoStem.Length > bestPrefix.Length)
                {
                    bestPrefix = videoStem;
                }
            }
        }

        return bestPrefix;
    }
}
