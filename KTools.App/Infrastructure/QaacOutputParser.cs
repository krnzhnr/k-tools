// -*- coding: utf-8 -*-
using System;
using System.Text.RegularExpressions;

namespace KTools_App.Infrastructure;

/// <summary>
/// Высокоэффективный парсер текстового вывода кодировщика Apple AAC (qaac64.exe),
/// поступающего из stderr. Вычисляет процент выполнения и скорость кодирования.
/// Все комментарии написаны исключительно на русском языке в соответствии с регламентом.
/// </summary>
public static class QaacOutputParser
{
    // Регулярное выражение для извлечения процента и скорости (например, " 12.5% [14.2x]")
    private static readonly Regex ProgressRegex = new(
        @"^\s*([\d\.]+)%\s*(?:\[([\d\.]+)x\])?",
        RegexOptions.Compiled
    );

    // Регулярное выражение для извлечения времени и скорости (например, " 0:11.413 (104.7x)")
    private static readonly Regex TimeRegex = new(
        @"^\s*(?:(\d+):)?(\d+):(\d+)(?:\.(\d+))?\s*\(([\d\.]+)x\)",
        RegexOptions.Compiled
    );

    // Регулярное выражение для нового формата (например, "[100.0%] 0:05.000/0:05.000 (161.3x), ETA 0:00.000")
    private static readonly Regex NewFormatRegex = new(
        @"\[([\d\.]+)%\]\s*(?:(?:(\d+):)?(\d+):(\d+)(?:\.(\d+))?)?/(?:(?:(\d+):)?(\d+):(\d+)(?:\.(\d+))?)?\s*\(([\d\.]+)x\)(?:,\s*ETA\s*([\d\:\.]+))?",
        RegexOptions.Compiled
    );

    /// <summary>
    /// Парсит отдельную строку вывода QAAC и извлекает метрики прогресса.
    /// </summary>
    /// <param name="line">Строка текстового вывода QAAC.</param>
    /// <param name="totalDuration">Общая длительность аудиофайла в секундах.</param>
    /// <returns>Экземпляр ProgressInfo при успешном парсинге, иначе null.</returns>
    public static ProgressInfo? ParseLine(string line, double totalDuration = 0.0)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        // Вариант 1: Новый формат с квадратными скобками в начале (например, "[100.0%] 0:05.000/0:05.000 (161.3x), ETA 0:00.000")
        var matchNew = NewFormatRegex.Match(line);
        if (matchNew.Success)
        {
            try
            {
                double percent = double.Parse(
                    matchNew.Groups[1].Value,
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture
                );
                percent = Math.Min(Math.Max(percent, 0.0), 100.0);

                double? speed = null;
                if (matchNew.Groups[10].Success &&
                    double.TryParse(
                        matchNew.Groups[10].Value,
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out double speedVal))
                {
                    speed = speedVal;
                }

                // Извлечение текущего времени
                double currentTime = 0.0;
                int hours = 0;
                int minutes = 0;
                int seconds = 0;
                double ms = 0.0;

                if (matchNew.Groups[2].Success)
                {
                    hours = int.Parse(matchNew.Groups[2].Value);
                    minutes = int.Parse(matchNew.Groups[3].Value);
                    seconds = int.Parse(matchNew.Groups[4].Value);
                }
                else if (matchNew.Groups[3].Success)
                {
                    minutes = int.Parse(matchNew.Groups[3].Value);
                    seconds = int.Parse(matchNew.Groups[4].Value);
                }

                if (matchNew.Groups[5].Success)
                {
                    string msStr = matchNew.Groups[5].Value;
                    ms = double.Parse(msStr) / Math.Pow(10, msStr.Length);
                }

                currentTime = hours * 3600 + minutes * 60 + seconds + ms;

                // Извлечение ETA
                string eta = "н/д";
                if (matchNew.Groups[11].Success)
                {
                    eta = matchNew.Groups[11].Value;
                }

                return new ProgressInfo(
                    TimeSeconds: currentTime,
                    Percent: percent,
                    Fps: null,
                    Bitrate: null,
                    Speed: speed,
                    Eta: eta
                );
            }
            catch
            {
                // Игнорируем и пробуем другие варианты
            }
        }

        // Вариант 2: Строка содержит явный процент (например, при других версиях или параметрах)
        var matchProgress = ProgressRegex.Match(line);
        if (matchProgress.Success)
        {
            try
            {
                double percent = double.Parse(
                    matchProgress.Groups[1].Value,
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture
                );
                percent = Math.Min(Math.Max(percent, 0.0), 100.0);

                double? speed = null;
                if (matchProgress.Groups[2].Success &&
                    double.TryParse(
                        matchProgress.Groups[2].Value,
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out double speedVal))
                {
                    speed = speedVal;
                }

                return new ProgressInfo(
                    TimeSeconds: 0.0,
                    Percent: percent,
                    Fps: null,
                    Bitrate: null,
                    Speed: speed,
                    Eta: "н/д"
                );
            }
            catch
            {
                // Игнорируем и пробуем другой вариант
            }
        }

        // Вариант 2: Строка содержит время и скорость в скобках (стандартный вывод qaac64.exe)
        var matchTime = TimeRegex.Match(line);
        if (matchTime.Success)
        {
            try
            {
                int hours = 0;
                int minutes = 0;
                int seconds = 0;
                double ms = 0.0;

                if (matchTime.Groups[1].Success)
                {
                    hours = int.Parse(matchTime.Groups[1].Value);
                    minutes = int.Parse(matchTime.Groups[2].Value);
                    seconds = int.Parse(matchTime.Groups[3].Value);
                }
                else
                {
                    minutes = int.Parse(matchTime.Groups[2].Value);
                    seconds = int.Parse(matchTime.Groups[3].Value);
                }

                if (matchTime.Groups[4].Success)
                {
                    string msStr = matchTime.Groups[4].Value;
                    ms = double.Parse(msStr) / Math.Pow(10, msStr.Length);
                }

                double currentTime = hours * 3600 + minutes * 60 + seconds + ms;
                double percent = totalDuration > 0 ? (currentTime / totalDuration) * 100.0 : 0.0;
                percent = Math.Min(Math.Max(percent, 0.0), 100.0);

                double? speed = null;
                if (matchTime.Groups[5].Success &&
                    double.TryParse(
                        matchTime.Groups[5].Value,
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out double speedVal))
                {
                    speed = speedVal;
                }

                // Расчет времени до завершения (ETA)
                string eta = "н/д";
                if (speed.HasValue && speed.Value > 0 && totalDuration > currentTime)
                {
                    double remainingSec = (totalDuration - currentTime) / speed.Value;
                    int remH = (int)(remainingSec / 3600);
                    int remM = (int)((remainingSec % 3600) / 60);
                    int remS = (int)(remainingSec % 60);

                    eta = remH > 0
                        ? $"{remH}:{remM:D2}:{remS:D2}"
                        : $"{remM:D2}:{remS:D2}";
                }

                return new ProgressInfo(
                    TimeSeconds: currentTime,
                    Percent: percent,
                    Fps: null,
                    Bitrate: null,
                    Speed: speed,
                    Eta: eta
                );
            }
            catch
            {
                return null;
            }
        }

        return null;
    }
}
