// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using KTools_App.Core;

namespace KTools_App.Scripts;

/// <summary>
/// Кандидат на автоматическую подстановку в строку замены дорожки.
/// </summary>
public sealed class StreamReplacementCandidate
{
    /// <summary>
    /// Полный путь к файлу-замене.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Идентификатор дорожки во внешнем файле (0 для простых файлов).
    /// </summary>
    public int SourceTrackId { get; set; }

    /// <summary>
    /// Метаданные дорожки контейнера (null для простых файлов без структуры).
    /// </summary>
    public MediaTrack? Track { get; set; }
}

/// <summary>
/// Автоподбор замены дорожки по совпадению названий и языков.
/// Вызывающая сторона заранее отбирает только совместимые по типу дорожки кандидаты.
/// Все комментарии выполнены исключительно на русском языке.
/// </summary>
public static class StreamReplacementMatcher
{
    private const int NameScore = 3;
    private const int FileNameScore = 2;
    private const int LanguageScore = 1;

    /// <summary>
    /// Находит лучший автоматический вариант замены для исходной дорожки.
    /// Возвращает null, если совпадений нет или лучший результат неоднозначен.
    /// </summary>
    /// <param name="source">Исходная дорожка контейнера.</param>
    /// <param name="candidates">Кандидаты, совместимые с типом исходной дорожки.</param>
    public static StreamReplacementCandidate? FindBestMatch(MediaTrack source, IEnumerable<StreamReplacementCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(candidates);

        var list = candidates.Where(c => c != null && !string.IsNullOrEmpty(c.FilePath)).ToList();
        if (list.Count == 0) return null;

        string sourceName = (source.Name ?? string.Empty).Trim();
        string sourceLanguage = (source.Language ?? string.Empty).Trim();

        var scored = new List<(StreamReplacementCandidate Candidate, int Score)>(list.Count);
        foreach (var candidate in list)
        {
            scored.Add((candidate, ScoreCandidate(sourceName, sourceLanguage, candidate)));
        }

        var best = scored.Where(s => s.Score > 0).OrderByDescending(s => s.Score).ToList();
        if (best.Count == 0) return null;

        // Неоднозначность лучшего результата оставляем на ручной выбор.
        if (best.Count > 1 && best[0].Score == best[1].Score) return null;

        return best[0].Candidate;
    }

    private static int ScoreCandidate(string sourceName, string sourceLanguage, StreamReplacementCandidate candidate)
    {
        // Точное совпадение названий дорожек имеет наивысший приоритет.
        if (sourceName.Length > 0
            && candidate.Track != null
            && string.Equals((candidate.Track.Name ?? string.Empty).Trim(), sourceName, StringComparison.OrdinalIgnoreCase))
        {
            return NameScore;
        }

        // Совпадение названия дорожки с именем простого файла.
        if (sourceName.Length > 0
            && candidate.Track == null
            && string.Equals(Path.GetFileNameWithoutExtension(candidate.FilePath).Trim(), sourceName, StringComparison.OrdinalIgnoreCase))
        {
            return FileNameScore;
        }

        // Единственный кандидат с тем же языком подставляется при отсутствии совпадений по названию.
        if (IsKnownLanguage(sourceLanguage)
            && candidate.Track != null
            && string.Equals((candidate.Track.Language ?? string.Empty).Trim(), sourceLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return LanguageScore;
        }

        return 0;
    }

    private static bool IsKnownLanguage(string language)
    {
        return language.Length > 0 && !string.Equals(language, "und", StringComparison.OrdinalIgnoreCase);
    }
}
