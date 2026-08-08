using System;
using System.Text.RegularExpressions;

using KTools_App.Core;
using KTools_App.Services.Contracts;

namespace KTools_App.Infrastructure;

/// <summary>
/// Информация о текущем прогрессе обработки медиафайла.
/// </summary>
/// <param name="TimeSeconds">Текущее обработанное время в секундах.</param>
/// <param name="Percent">Процент выполнения операции (от 0.0 до 100.0).</param>
/// <param name="Fps">Текущая скорость кодирования кадров в секунду (для видео).</param>
/// <param name="Bitrate">Текущий битрейт потока.</param>
/// <param name="Speed">Скорость обработки относительно реального времени (например, 2.5x).</param>
/// <param name="Eta">Оставшееся расчетное время выполнения операции.</param>
public record ProgressInfo(
    double TimeSeconds,
    double Percent,
    double? Fps = null,
    string? Bitrate = null,
    double? Speed = null,
    string Eta = "н/д"
);

/// <summary>
/// Высокоэффективный парсер текстового вывода утилиты FFmpeg (поступающего из потока stderr).
/// Вычисляет процент выполнения, скорость обработки и оставшееся время (ETA).
/// Все комментарии написаны исключительно на русском языке в соответствии с регламентом.
/// </summary>
public static class FFmpegOutputParser
{
    // Регулярное выражение для извлечения времени (поддерживает разделители точку и запятую, опциональную дробную часть и часы любой длины)
    private static readonly Regex TimeRegex = new(
        @"(?:^|[\s(\[])time=(-?\d+):(\d{2}):(\d{2})(?:[\.\,](\d+))?",
        RegexOptions.Compiled
    );

    // Регулярное выражение для извлечения FPS
    private static readonly Regex FpsRegex = new(
        @"fps=\s*([\d\.]+)", 
        RegexOptions.Compiled
    );

    // Регулярное выражение для извлечения битрейта
    private static readonly Regex BitrateRegex = new(
        @"bitrate=\s*([\d\.N/A]+\s*[kmg]?bits/s|N/A)", 
        RegexOptions.Compiled
    );

    // Регулярное выражение для извлечения относительной скорости обработки (например, "1.5x")
    private static readonly Regex SpeedRegex = new(
        @"speed=\s*([\d\.]+)x", 
        RegexOptions.Compiled
    );

    // Регулярное выражение для извлечения заголовочной длительности (Duration: HH:MM:SS.ms) из вывода FFmpeg
    private static readonly Regex HeaderDurationRegex = new(
        @"Duration:\s*(\d+):(\d{2}):(\d{2})(?:[\.\,](\d+))?",
        RegexOptions.Compiled
    );

    /// <summary>
    /// Извлекает общую длительность медиафайла в секундах из заголовочного вывода FFmpeg (строка Duration: HH:MM:SS.ms).
    /// </summary>
    /// <param name="line">Строка текстового вывода FFmpeg.</param>
    /// <param name="logService">Сервис логирования.</param>
    /// <returns>Длительность в секундах при успешном парсинге, иначе 0.0.</returns>
    public static double ParseHeaderDuration(string line, ILogService logService)
    {
        if (string.IsNullOrWhiteSpace(line) || !line.Contains("Duration:")) return 0.0;

        var match = HeaderDurationRegex.Match(line);
        if (!match.Success) return 0.0;

        try
        {
            int h = Math.Abs(int.Parse(match.Groups[1].Value));
            int m = int.Parse(match.Groups[2].Value);
            int s = int.Parse(match.Groups[3].Value);
            string msStr = match.Groups[4].Value;

            double ms = 0.0;
            if (!string.IsNullOrEmpty(msStr))
            {
                ms = double.Parse(msStr) / Math.Pow(10, msStr.Length);
            }

            double totalSec = h * 3600 + m * 60 + s + ms;
            return totalSec;
        }
        catch (Exception ex)
        {
            logService.DebugLog($"Не удалось распарсить заголовочную длительность: {ex.Message}", "FFmpegOutputParser");
            return 0.0;
        }
    }

    /// <summary>
    /// Парсит отдельную строку вывода FFmpeg и вычисляет текущие метрики прогресса.
    /// </summary>
    /// <param name="line">Строка текстового вывода FFmpeg.</param>
    /// <param name="totalDuration">Общая длительность обрабатываемого аудио/видео файла в секундах.</param>
    /// <returns>Экземпляр ProgressInfo при успешном парсинге, иначе null.</returns>
    public static ProgressInfo? ParseLine(string line, double totalDuration, ILogService logService)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        var timeMatch = TimeRegex.Match(line);
        if (!timeMatch.Success) return null;

        try
        {
            int h = Math.Abs(int.Parse(timeMatch.Groups[1].Value));
            int m = int.Parse(timeMatch.Groups[2].Value);
            int s = int.Parse(timeMatch.Groups[3].Value);
            string msStr = timeMatch.Groups[4].Value;
            
            // Расчет дробной части миллисекунд с учетом ее длины
            double ms = 0.0;
            if (!string.IsNullOrEmpty(msStr))
            {
                ms = double.Parse(msStr) / Math.Pow(10, msStr.Length);
            }
            double currentTime = h * 3600 + m * 60 + s + ms;

            // Расчет процента выполнения
            double percent = 0.0;
            if (totalDuration > 0)
            {
                percent = (currentTime / totalDuration) * 100.0;
                percent = Math.Min(Math.Max(percent, 0.0), 100.0);
            }

            // Извлечение FPS
            double? fps = null;
            var fpsMatch = FpsRegex.Match(line);
            if (fpsMatch.Success && double.TryParse(fpsMatch.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double fpsVal))
            {
                fps = fpsVal;
            }

            // Извлечение битрейта
            string? bitrate = null;
            var bitMatch = BitrateRegex.Match(line);
            if (bitMatch.Success)
            {
                string bitRaw = bitMatch.Groups[1].Value.Trim();
                if (!bitRaw.Equals("N/A", StringComparison.OrdinalIgnoreCase))
                {
                    bitrate = bitRaw;
                }
            }

            // Извлечение скорости
            double? speed = null;
            var speedMatch = SpeedRegex.Match(line);
            if (speedMatch.Success && double.TryParse(speedMatch.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double speedVal))
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

            return new ProgressInfo(currentTime, percent, fps, bitrate, speed, eta);
        }
        catch (Exception ex)
        {
            // Молчаливый пропуск при ошибках парсинга некорректных строк
            logService.DebugLog($"Не удалось распарсить строку прогресса: {ex.Message}", "FFmpegOutputParser");
            return null;
        }
    }
}
