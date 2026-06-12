// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KTools_App.Core;
using KTools_App.Infrastructure;

namespace KTools_App.Scripts;

/// <summary>
/// Скрипт для изменения скорости и тона аудио (PAL ↔ NTSC) через eac3to.
/// </summary>
public sealed class AudioSpeedScript : AbstractScript
{
    /// <summary>
    /// Русское название скрипта.
    /// </summary>
    public override string Name => AppConstants.ScriptMetadata.AudioSpeedName;

    /// <summary>
    /// Русское описание назначения скрипта для интерфейса.
    /// </summary>
    public override string Description => AppConstants.ScriptMetadata.AudioSpeedDesc;

    /// <summary>
    /// Категория медиаобработки.
    /// </summary>
    public override string Category => AppConstants.ScriptCategory.Audio;

    /// <summary>
    /// Имя Fluent-иконки для отображения в боковом меню.
    /// </summary>
    public override string IconName => "sync";

    /// <summary>
    /// Список поддерживаемых расширений файлов.
    /// </summary>
    public override string[] FileExtensions => AppConstants.AudioContainers
        .Concat(AppConstants.AudioStreams)
        .Concat(AppConstants.VideoContainers)
        .ToArray();

    /// <summary>
    /// Список внешних зависимостей, необходимых для выполнения скрипта.
    /// </summary>
    public override string[] RequiredDependencies => new[] { "eac3to" };

    /// <summary>
    /// Поддерживает ли скрипт параллельную обработку файлов.
    /// </summary>
    public override bool SupportsParallel => true;

    /// <summary>
    /// Схема настроек параметров скрипта для генерации UI.
    /// </summary>
    public override List<SettingField> SettingsSchema => new()
    {
        new SettingField(
            "SpeedMode",
            "Режим преобразования",
            SettingType.Combo,
            "Slowdown (25.000 → 23.976)",
            "Настройки скорости",
            options: new List<string>
            {
                "Slowdown (25.000 → 23.976)",
                "Speedup (23.976 → 25.000)",
                "Custom (24.000 → 23.976)",
                "Custom (25.000 → 24.000)"
            }),

        new SettingField(
            "OutputFormat",
            "Формат вывода",
            SettingType.Combo,
            "FLAC",
            "Настройки скорости",
            options: new List<string>
            {
                "FLAC",
                "WAV"
            }),

        new SettingField(
            "DeleteOriginal",
            "Удалить исходный файл",
            SettingType.Checkbox,
            false,
            "Общие")
    };

    /// <summary>
    /// Асинхронное выполнение обработки одного файла.
    /// </summary>
    public override async Task<List<string>> ExecuteSingleAsync(
        string filePath,
        Dictionary<string, object> settings,
        string? outputPath,
        Action<int, int, string, double?> progressCallback,
        int fileIndex,
        int totalCount)
    {
        ResetCancellation();
        var results = new List<string>();

        // Извлекаем пользовательские настройки
        string mode = GetSettingValue(
            settings,
            "SpeedMode",
            "Slowdown (25.000 → 23.976)");
        string format = GetSettingValue(
            settings,
            "OutputFormat",
            "FLAC");
        bool deleteOriginal = GetSettingValue(
            settings,
            "DeleteOriginal",
            false);

        string originalName = Path.GetFileNameWithoutExtension(filePath);

        // Динамическая проверка зависимости eac3to
        if (!DependencyManager.Instance.IsInstalled("eac3to"))
        {
            string errMsg = "❌ Ошибка: Необходимая утилита 'eac3to' " +
                            "не установлена в системе.";
            results.Add(errMsg);
            LogService.Instance.Error(errMsg, "AudioSpeedScript");
            return results;
        }

        // Подготовка аргументов для eac3to
        var eac3toArgs = new List<string>();
        eac3toArgs.Add($"\"{filePath}\"");

        // Подготовка опций изменения скорости
        var options = new List<string>();
        string suffix = "_slowdown";
        if (mode == "Slowdown (25.000 → 23.976)")
        {
            options.Add("-slowdown");
            suffix = "_slowdown";
        }
        else if (mode == "Speedup (23.976 → 25.000)")
        {
            options.Add("-speedup");
            suffix = "_speedup";
        }
        else if (mode == "Custom (24.000 → 23.976)")
        {
            options.Add("-24.000");
            options.Add("-slowdown");
            suffix = "_24_to_23";
        }
        else if (mode == "Custom (25.000 → 24.000)")
        {
            options.Add("-25.000");
            options.Add("-changeTo24.000");
            suffix = "_25_to_24";
        }

        string ext = format.ToLowerInvariant();
        string outputName = $"{originalName}{suffix}.{ext}";

        // Определение целевой директории сохранения
        string targetDir = string.IsNullOrEmpty(outputPath)
            ? Path.GetDirectoryName(filePath) ?? AppContext.BaseDirectory
            : outputPath;

        string outputFilePath = Path.Combine(targetDir, outputName);
        outputFilePath = GetSafeOutputPath(filePath, outputFilePath);

        // Проверка флага перезаписи существующего файла
        bool overwrite = SettingsManager.Instance.GetSetting(
            "General",
            "OverwriteExisting",
            false);

        if (File.Exists(outputFilePath) && !overwrite)
        {
            string msg = $"Пропуск (существует): {outputName}";
            progressCallback(fileIndex, totalCount, msg, 100.0);
            results.Add($"⏭ ПРОПУСК (файл существует): {outputName}");
            LogService.Instance.Info(
                $"Файл результата '{outputFilePath}' уже существует, " +
                $"обработка пропущена.",
                "AudioSpeedScript");
            return results;
        }

        // Добавляем путь выходного файла и опции к аргументам eac3to
        eac3toArgs.Add($"\"{outputFilePath}\"");
        eac3toArgs.AddRange(options);

        progressCallback(
            fileIndex,
            totalCount,
            "Изменение скорости через eac3to...",
            0.0);

        using var cts = new CancellationTokenSource();
        var eac3toTask = Eac3toRunner.Instance.RunAsync(
            eac3toArgs,
            onProgress: pct =>
            {
                progressCallback(
                    fileIndex,
                    totalCount,
                    $"Изменение скорости через eac3to... {pct:0}%",
                    pct);
            },
            cancellationToken: cts.Token);

        while (!eac3toTask.IsCompleted)
        {
            if (IsCancelled)
            {
                cts.Cancel();
                break;
            }
            await Task.Delay(200);
        }

        bool success = false;
        try
        {
            success = await eac3toTask;
        }
        catch (Exception ex)
        {
            LogService.Instance.Exception(
                ex,
                $"Ошибка работы eac3to для '{originalName}': {ex.Message}",
                "AudioSpeedScript");
        }

        if (IsCancelled)
        {
            CleanupIfCancelled(outputFilePath);
            results.Add($"⚠ Отменено: {outputName}");
            LogService.Instance.Info(
                $"Обработка файла '{originalName}' отменена пользователем.",
                "AudioSpeedScript");
            return results;
        }

        if (success && File.Exists(outputFilePath))
        {
            progressCallback(
                fileIndex,
                totalCount,
                "Успешно завершено!",
                100.0);
            results.Add($"✅ Скорость изменена: {outputName}");
            LogService.Instance.Info(
                $"Успешно завершено изменение скорости для '{originalName}'. " +
                $"Результат сохранен в '{outputName}'",
                "AudioSpeedScript");

            if (deleteOriginal)
            {
                DeleteSource(filePath, results);
            }
        }
        else
        {
            string errorMsg = $"❌ Ошибка обработки для " +
                              $"{Path.GetFileName(filePath)}";
            results.Add(errorMsg);
            LogService.Instance.Error(
                $"Ошибка выполнения eac3to при обработке файла " +
                $"'{filePath}'. Выходной файл не создан.",
                "AudioSpeedScript");
        }

        return results;
    }
}
