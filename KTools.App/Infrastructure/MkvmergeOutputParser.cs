// -*- coding: utf-8 -*-
using System;
using System.Text.RegularExpressions;

namespace KTools_App.Infrastructure;

/// <summary>
/// Высокоэффективный парсер текстового вывода утилиты mkvmerge.
/// Вычисляет процент выполнения на основе строк "Progress: X%".
/// Все комментарии и XML-документация написаны исключительно на русском языке в соответствии с регламентом.
/// </summary>
public static class MkvmergeOutputParser
{
    // Регулярное выражение для извлечения процентов прогресса (например, "Progress: 42%" или "Progress: 42,5%" в неанглийской локали)
    private static readonly Regex ProgressRegex = new(
        @"Progress:\s*(\d+(?:[.,]\d+)?)%",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    /// <summary>
    /// Парсит отдельную строку вывода mkvmerge и извлекает процент прогресса.
    /// </summary>
    /// <param name="line">Строка текстового вывода mkvmerge.</param>
    /// <returns>Процент выполнения от 0.0 до 100.0, или null, если прогресс не обнаружен.</returns>
    public static double? ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        var match = ProgressRegex.Match(line);
        if (match.Success)
        {
            try
            {
                // Нормализуем запятую неанглийской локали к точке для инвариантного парсинга
                string raw = match.Groups[1].Value.Replace(',', '.');
                double percent = double.Parse(
                    raw,
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture
                );
                return Math.Min(Math.Max(percent, 0.0), 100.0);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }
}
