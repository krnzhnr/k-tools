// -*- coding: utf-8 -*-
using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace KTools_App.Infrastructure;

/// <summary>
/// Парсер потоков стандартного вывода и вывода ошибок консольной утилиты whisper-cli.
/// Извлекает проценты интегрального прогресса (-pp) и сегменты субтитров с временными метками.
/// </summary>
public static class WhisperOutputParser
{
    private static readonly Regex ProgressRegex = new(
        @"progress\s*=\s*(\d{1,3})\s*%",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SegmentRegex = new(
        @"^\[(\d{2}:\d{2}:\d{2}\.\d{3})\s*-->\s*(\d{2}:\d{2}:\d{2}\.\d{3})\]\s*(.*)$",
        RegexOptions.Compiled);

    /// <summary>
    /// Пытается извлечь процент выполнения задачи из строки вывода stderr.
    /// </summary>
    /// <param name="line">Строка вывода процесса whisper-cli.</param>
    /// <param name="percent">Полученное значение процента выполнения (0..100).</param>
    /// <returns>True, если процент успешно распознан.</returns>
    public static bool TryParseProgress(string line, out int percent)
    {
        percent = 0;
        if (string.IsNullOrWhiteSpace(line)) return false;

        var match = ProgressRegex.Match(line);
        if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int val))
        {
            percent = Math.Clamp(val, 0, 100);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Пытается извлечь сегмент субтитра (таймкоды и текст) из строки стандартного вывода stdout.
    /// </summary>
    /// <param name="line">Строка вывода процесса whisper-cli.</param>
    /// <param name="startTime">Время начала сегмента.</param>
    /// <param name="endTime">Время окончания сегмента.</param>
    /// <param name="text">Текст распознанной реплики.</param>
    /// <returns>True, если строка представляет собой сегмент субтитра.</returns>
    public static bool TryParseSegment(string line, out TimeSpan startTime, out TimeSpan endTime, out string text)
    {
        startTime = TimeSpan.Zero;
        endTime = TimeSpan.Zero;
        text = string.Empty;

        if (string.IsNullOrWhiteSpace(line)) return false;

        var match = SegmentRegex.Match(line.Trim());
        if (match.Success)
        {
            if (TimeSpan.TryParseExact(match.Groups[1].Value, @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture, out startTime) &&
                TimeSpan.TryParseExact(match.Groups[2].Value, @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture, out endTime))
            {
                text = match.Groups[3].Value.Trim();
                return true;
            }
        }

        return false;
    }
}
