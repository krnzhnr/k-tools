// -*- coding: utf-8 -*-
using System;
using System.Text.RegularExpressions;

namespace KTools_App.Infrastructure;

/// <summary>
/// Высокоэффективный парсер текстового вывода утилиты eac3to.
/// Вычисляет процент выполнения на основе строк "process: X%" и "analyze: X%".
/// Все комментарии написаны исключительно на русском языке в соответствии с регламентом.
/// </summary>
public static class Eac3toOutputParser
{
    // Регулярное выражение для извлечения процента прогресса (например, "process: 12%" или "analyze: 4%")
    private static readonly Regex ProcessRegex = new(
        @"(?:process|analyze):\s*(\d+)%",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    /// <summary>
    /// Парсит отдельную строку вывода eac3to и извлекает процент прогресса.
    /// </summary>
    /// <param name="line">Строка текстового вывода eac3to.</param>
    /// <returns>Процент выполнения от 0.0 до 100.0, или null, если прогресс не найден.</returns>
    public static double? ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        var match = ProcessRegex.Match(line);
        if (match.Success)
        {
            try
            {
                double percent = double.Parse(
                    match.Groups[1].Value,
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
