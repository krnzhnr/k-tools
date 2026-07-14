// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using KTools_App.Core;
using KTools_App.Services.Contracts;

namespace KTools_App.Scripts;

/// <summary>
/// Скрипт для изменения тайминга (сдвига времени) субтитров форматов SRT, ASS, SSA и WebVTT.
/// Все комментарии, логи и документация выполнены на русском языке согласно регламенту.
/// </summary>
public sealed class SubtitleShiftScript : AbstractScript
{
    private static readonly Regex SrtVttTimeRegex = new(
        @"^(\d{1,2}:\d{2}:\d{2}[,\.]\d{3})\s*-->\s*(\d{1,2}:\d{2}:\d{2}[,\.]\d{3})(.*)$",
        RegexOptions.Compiled);

    public SubtitleShiftScript(ILogService logService, ISettingsManager settingsManager, IPathManager pathManager)
        : base(logService, settingsManager, pathManager)
    {
    }

    /// <summary>
    /// Локализованное название скрипта.
    /// </summary>
    public override string Name => AppConstants.ScriptMetadata.SubtitleShiftName;

    /// <summary>
    /// Описание назначения скрипта для UI.
    /// </summary>
    public override string Description => AppConstants.ScriptMetadata.SubtitleShiftDesc;

    /// <summary>
    /// Категория обработки.
    /// </summary>
    public override string Category => AppConstants.ScriptCategory.Subtitles;

    /// <summary>
    /// Имя системной Fluent-иконки.
    /// </summary>
    public override string IconName => AppConstants.ScriptIcons.SubtitlesShift;

    /// <summary>
    /// Поддерживаемые входящие форматы файлов субтитров.
    /// </summary>
    public override string[] FileExtensions => AppConstants.SubtitleExtensions.ToArray();

    /// <summary>
    /// Зависимости скрипта (для сдвига субтитров внешние утилиты не требуются).
    /// </summary>
    public override string[] RequiredDependencies => Array.Empty<string>();

    /// <summary>
    /// Поддерживает ли параллельную обработку файлов.
    /// </summary>
    public override bool SupportsParallel => true;

    /// <summary>
    /// Декларативная схема параметров настроек скрипта.
    /// </summary>
    public override List<SettingField> SettingsSchema => new()
    {
        new SettingField(
            "ShiftMs",
            "Величина сдвига (мс или Ч:ММ:СС.сс)",
            SettingType.Text,
            "1000",
            "Настройки сдвига"),

        new SettingField(
            "ShiftDirection",
            "Направление сдвига",
            SettingType.Combo,
            "Вперед",
            "Настройки сдвига",
            options: new List<string> { "Вперед", "Назад" })
    };

    /// <summary>
    /// Асинхронное выполнение сдвига тайминга для одного файла субтитров.
    /// </summary>
    public override async Task<List<string>> ExecuteSingleAsync(
        string filePath,
        Dictionary<string, object> settings,
        string? outputPath,
        ScriptProgressCallback progressCallback,
        int fileIndex,
        int totalCount)
    {
        var results = new List<string>();
        string originalName = Path.GetFileName(filePath);
        string originalExt = Path.GetExtension(filePath);

        _logService.Info($"Начало обработки субтитров: '{originalName}'", "SubtitleShiftScript");
        progressCallback(fileIndex, totalCount, "Чтение файла...", 0.0);
        object shiftMsRaw = GetSettingValue(settings, "ShiftMs", (object)"1000");
        int shiftMs = ParseShiftValue(shiftMsRaw);
        string direction = GetSettingValue(settings, "ShiftDirection", "Вперед");

        _logService.Info($"Параметры сдвига: {shiftMs} мс (сырое значение: '{shiftMsRaw}'), направление: {direction}", "SubtitleShiftScript");

        // Определение целевой директории
        string targetDir = string.IsNullOrEmpty(outputPath)
            ? Path.GetDirectoryName(filePath) ?? AppContext.BaseDirectory
            : outputPath;

        string outputName = $"{Path.GetFileNameWithoutExtension(filePath)}_shifted{originalExt}";
        string outputFilePath = Path.Combine(targetDir, outputName);
        outputFilePath = GetSafeOutputPath(filePath, outputFilePath, settings);

        // Проверка флага перезаписи существующего файла
        bool overwrite = _settingsManager.GetSetting("General", "OverwriteExisting", false);
        if (File.Exists(outputFilePath) && !overwrite)
        {
            string msg = $"Пропуск (существует): {outputName}";
            progressCallback(fileIndex, totalCount, msg, 100.0);
            results.Add($"⏭ ПРОПУСК (файл существует): {outputName}");
            _logService.Info($"Файл результата '{outputFilePath}' уже существует, обработка пропущена.", "SubtitleShiftScript");
            return results;
        }

        try
        {
            // Чтение файла с автоопределением кодировки (UTF-8 или Windows-1251)
            string content = ReadFileWithFallbackEncoding(filePath);
            string[] lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var updatedLines = new List<string>(lines.Length);

            bool isAss = originalExt.Equals(".ass", StringComparison.OrdinalIgnoreCase) ||
                         originalExt.Equals(".ssa", StringComparison.OrdinalIgnoreCase);

            int totalLines = lines.Length;
            for (int i = 0; i < totalLines; i++)
            {
                if (IsCancelled)
                {
                    break;
                }

                string line = lines[i];

                if (isAss)
                {
                    line = ProcessAssLine(line, shiftMs, direction);
                }
                else
                {
                    line = ProcessSrtVttLine(line, shiftMs, direction);
                }

                updatedLines.Add(line);

                if (i % 100 == 0 || i == totalLines - 1)
                {
                    double percent = ((double)(i + 1) / totalLines) * 100.0;
                    progressCallback(fileIndex, totalCount, $"Обработка строк: {i + 1}/{totalLines}", percent);
                }
            }

            if (IsCancelled)
            {
                results.Add($"⚠ Отменено: {outputName}");
                _logService.Info($"Обработка файла '{originalName}' отменена пользователем.", "SubtitleShiftScript");
                return results;
            }

            // Запись результата в UTF-8
            await File.WriteAllLinesAsync(outputFilePath, updatedLines, Encoding.UTF8);

            _logService.Info($"Файл субтитров успешно сохранен: '{outputFilePath}'", "SubtitleShiftScript");
            progressCallback(fileIndex, totalCount, "Завершено", 100.0);
            results.Add($"✔ Сдвиг выполнен успешно: {outputName}");
        }
        catch (Exception ex)
        {
            _logService.Exception(ex, $"Ошибка при сдвиге тайминга субтитров '{originalName}': {ex.Message}", "SubtitleShiftScript");
            results.Add($"❌ Ошибка: {originalName} ({ex.Message})");
            progressCallback(fileIndex, totalCount, "Ошибка выполнения", 100.0);
        }

        return results;
    }

    private string ProcessSrtVttLine(string line, int shiftMs, string direction)
    {
        var match = SrtVttTimeRegex.Match(line);
        if (!match.Success)
        {
            return line;
        }

        string startStr = match.Groups[1].Value;
        string endStr = match.Groups[2].Value;
        string remainder = match.Groups[3].Value;

        char separator = startStr.Contains(',') ? ',' : '.';

        TimeSpan startTs = ParseSrtVttTime(startStr);
        TimeSpan endTs = ParseSrtVttTime(endStr);

        startTs = ShiftTime(startTs, shiftMs, direction);
        endTs = ShiftTime(endTs, shiftMs, direction);

        string newStart = FormatSrtVttTime(startTs, separator);
        string newEnd = FormatSrtVttTime(endTs, separator);

        return $"{newStart} --> {newEnd}{remainder}";
    }

    private string ProcessAssLine(string line, int shiftMs, string direction)
    {
        string prefix = string.Empty;
        if (line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
        {
            prefix = "Dialogue:";
        }
        else if (line.StartsWith("Comment:", StringComparison.OrdinalIgnoreCase))
        {
            prefix = "Comment:";
        }
        else
        {
            return line;
        }

        string fieldsPart = line.Substring(prefix.Length);
        string[] fields = fieldsPart.Split(',', 10);

        if (fields.Length < 9)
        {
            return line;
        }

        string startStr = fields[1].Trim();
        string endStr = fields[2].Trim();

        TimeSpan startTs = ParseAssTime(startStr);
        TimeSpan endTs = ParseAssTime(endStr);

        startTs = ShiftTime(startTs, shiftMs, direction);
        endTs = ShiftTime(endTs, shiftMs, direction);

        fields[1] = FormatAssTime(startTs);
        fields[2] = FormatAssTime(endTs);

        return $"{prefix}{string.Join(',', fields)}";
    }

    private TimeSpan ShiftTime(TimeSpan original, int shiftMs, string direction)
    {
        TimeSpan shift = TimeSpan.FromMilliseconds(shiftMs);
        TimeSpan result = direction == "Вперед" ? original + shift : original - shift;
        return result < TimeSpan.Zero ? TimeSpan.Zero : result;
    }

    private TimeSpan ParseSrtVttTime(string timeStr)
    {
        string clean = timeStr.Replace(',', '.');
        string[] parts = clean.Split('.');
        string[] t = parts[0].Split(':');
        int h = int.Parse(t[0]);
        int m = int.Parse(t[1]);
        int s = int.Parse(t[2]);
        int ms = int.Parse(parts[1]);
        return new TimeSpan(0, h, m, s, ms);
    }

    private string FormatSrtVttTime(TimeSpan ts, char separator)
    {
        int hours = (int)ts.TotalHours;
        return $"{hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}{separator}{ts.Milliseconds:D3}";
    }

    private TimeSpan ParseAssTime(string timeStr)
    {
        string[] parts = timeStr.Split('.');
        string[] t = parts[0].Split(':');
        int h = int.Parse(t[0]);
        int m = int.Parse(t[1]);
        int s = int.Parse(t[2]);
        int cs = int.Parse(parts[1]);
        return new TimeSpan(0, h, m, s, cs * 10);
    }

    private string FormatAssTime(TimeSpan ts)
    {
        int hours = (int)ts.TotalHours;
        int cs = ts.Milliseconds / 10;
        return $"{hours}:{ts.Minutes:D2}:{ts.Seconds:D2}.{cs:D2}";
    }

    private string ReadFileWithFallbackEncoding(string filePath)
    {
        var utf8Strict = new UTF8Encoding(false, true);
        try
        {
            return File.ReadAllText(filePath, utf8Strict);
        }
        catch
        {
            var cp1251 = Encoding.GetEncoding("windows-1251");
            return File.ReadAllText(filePath, cp1251);
        }
    }

    /// <summary>
    /// Разбирает величину сдвига, которая может быть представлена в миллисекундах или формате Aegisub (Ч:ММ:СС.сс).
    /// </summary>
    private int ParseShiftValue(object rawValue)
    {
        if (rawValue == null) return 0;

        if (rawValue is int intVal)
        {
            return intVal;
        }
        if (rawValue is long longVal)
        {
            return (int)longVal;
        }

        string input = rawValue.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(input)) return 0;
        input = input.Trim();

        // 1. Проверяем, является ли числом (миллисекунды)
        if (int.TryParse(input, out int ms))
        {
            return ms;
        }

        // 2. Проверяем формат Aegisub (Ч:ММ:СС.сс или Ч:ММ:СС,сс)
        if (input.Length == 10 && input[1] == ':' && input[4] == ':' && (input[7] == '.' || input[7] == ','))
        {
            try
            {
                int hours = int.Parse(input.Substring(0, 1));
                int minutes = int.Parse(input.Substring(2, 2));
                int seconds = int.Parse(input.Substring(5, 2));
                int hundredths = int.Parse(input.Substring(8, 2));

                long totalMs = ((hours * 3600L) + (minutes * 60L) + seconds) * 1000L + (hundredths * 10L);
                return (int)totalMs;
            }
            catch (Exception ex)
            {
                _logService.Warn($"Не удалось разобрать Aegisub тайминг '{input}': {ex.Message}", "SubtitleShiftScript");
            }
        }

        // 3. Общий разбор через TimeSpan
        string tsInput = input.Replace(',', '.');
        if (TimeSpan.TryParse(tsInput, System.Globalization.CultureInfo.InvariantCulture, out TimeSpan parsedTs))
        {
            return (int)parsedTs.TotalMilliseconds;
        }

        _logService.Warn($"Неизвестный формат величины сдвига: '{input}'. Будет использован сдвиг 0 мс.", "SubtitleShiftScript");
        return 0;
    }
}
