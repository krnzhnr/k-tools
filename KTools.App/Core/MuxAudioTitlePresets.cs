// -*- coding: utf-8 -*-
using System.Collections.Generic;
using System.Linq;

namespace KTools_App.Core;

/// <summary>
/// Преобразование списка заготовленных заголовков аудиодорожек между
/// хранимым видом настроек (KeywordList: word/active) и рабочими строками.
/// Активный флажок означает показ заготовки в диалоге ввода заголовка.
/// Все комментарии выполнены исключительно на русском языке.
/// </summary>
public static class MuxAudioTitlePresets
{
    /// <summary>
    /// Ключ настройки со списком заготовок в группе скрипта сборки.
    /// </summary>
    public const string PresetsSettingKey = "audio_title_presets";

    /// <summary>
    /// Извлекает тексты активных заготовок из хранимого значения настройки.
    /// Терпит отсутствие, мусор и элементы JSON любого вида.
    /// </summary>
    public static List<string> ToWords(object? stored)
    {
        var result = new List<string>();
        if (stored is not List<Dictionary<string, object>> list)
        {
            return result;
        }

        foreach (var row in list)
        {
            if (!row.TryGetValue("active", out object? active) || !SettingValue.AsBool(active, false))
            {
                continue;
            }

            if (row.TryGetValue("word", out object? word))
            {
                string text = SettingValue.AsString(word, string.Empty);
                if (!string.IsNullOrWhiteSpace(text) && !result.Contains(text.Trim()))
                {
                    result.Add(text.Trim());
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Упаковывает строки заготовок в хранимый вид настройки (все активны).
    /// </summary>
    public static List<Dictionary<string, object>> FromWords(IEnumerable<string> words)
    {
        return words
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .Select(w => w.Trim())
            .Distinct()
            .Select(w => new Dictionary<string, object> { { "word", w }, { "active", true } })
            .ToList();
    }

    /// <summary>
    /// Возвращает действующий заголовок аудиофайла: ручной из дропа,
    /// иначе встроенный из метаданных первой аудиодорожки, иначе пустая строка.
    /// </summary>
    public static string GetEffectiveTitle(FileQueueItem file)
    {
        string manual = file.MuxAudioTitle?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(manual))
        {
            return manual;
        }

        var tracks = file.MediaInfo?.Tracks;
        if (tracks != null)
        {
            foreach (var track in tracks)
            {
                if (track.TrackType.Equals("audio", System.StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(track.Name))
                {
                    return track.Name.Trim();
                }
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Переименовывает заголовок у файлов с указанным текущим заголовком.
    /// Пустой новый заголовок очищает ручные заголовки совпавших файлов.
    /// Возвращает число затронутых файлов.
    /// </summary>
    public static int ApplyRename(IEnumerable<FileQueueItem> files, string oldTitle, string? newTitle)
    {
        string source = oldTitle ?? string.Empty;
        string? target = string.IsNullOrWhiteSpace(newTitle) ? null : newTitle.Trim();

        int renamed = 0;
        foreach (var file in files)
        {
            if (!string.Equals(GetEffectiveTitle(file), source, System.StringComparison.Ordinal))
            {
                continue;
            }

            file.MuxAudioTitle = target;
            renamed++;
        }

        return renamed;
    }
}
