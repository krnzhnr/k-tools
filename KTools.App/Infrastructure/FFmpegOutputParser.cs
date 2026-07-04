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
    // Регулярное выражение для извлечения времени (поддерживает разделители точку и запятую)
    private static readonly Regex TimeRegex = new(
        @"(?:^|[\s(\[])time=(\d{2}):(\d{2}):(\d{2})[\.\,](\d+)",
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
            int h = int.Parse(timeMatch.Groups[1].Value);
            int m = int.Parse(timeMatch.Groups[2].Value);
            int s = int.Parse(timeMatch.Groups[3].Value);
            string msStr = timeMatch.Groups[4].Value;
            
            // Расчет дробной части миллисекунд с учетом ее длины
            double ms = double.Parse(msStr) / Math.Pow(10, msStr.Length);
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
            if (fpsMatch.Success && double.TryParse(fpsMatch.Groups[1].Value, out double fpsVal))
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
            if (speedMatch.Success && double.TryParse(speedMatch.Groups[1].Value, out double speedVal))
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
