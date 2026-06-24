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

        // Предотвращаем зависание и сбой eac3to из-за кириллического пути.
        // Если целевой путь содержит не-ASCII символы, сохраняем временный файл в гарантированно
        // ASCII-совместимую директорию C:\Users\Public\KTools_Temp (к которой у любого пользователя есть права записи),
        // так как при отключенной генерации имен 8.3 на NTFS-томах eac3to/libFLAC падает при путях с кириллицей.
        bool usePublicTemp = targetDir.Any(c => c > 127);
        string tempDir = usePublicTemp
            ? Path.Combine(Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public", "KTools_Temp")
            : targetDir;

        try
        {
            if (!Directory.Exists(tempDir))
            {
                Directory.CreateDirectory(tempDir);
                LogService.Instance.DebugLog($"Создана временная папка для eac3to: '{tempDir}'", "AudioSpeedScript");
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Exception(ex, $"Не удалось создать временную директорию '{tempDir}', откат на стандартный путь", "AudioSpeedScript");
            tempDir = Path.GetTempPath();
        }

        string shortInputPath = PathManager.GetShortPath(filePath);
        string tempOutputName = $"temp_speed_{Guid.NewGuid():N}.{ext}";
        string tempOutputFilePath = Path.Combine(tempDir, tempOutputName);

        // Добавляем абсолютный путь к временному файлу (гарантированно ASCII)
        eac3toArgs.Add($"\"{tempOutputFilePath}\"");
        eac3toArgs.AddRange(options);

        progressCallback(
            fileIndex,
            totalCount,
            "Изменение скорости через eac3to...",
            0.0);

        using var cts = new CancellationTokenSource();
        var eac3toTask = Eac3toRunner.Instance.RunAsync(
            eac3toArgs,
            workingDir: tempDir,
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
            CleanupFailedOutputFile(tempOutputFilePath);
            CleanupFailedOutputFile(outputFilePath);
            results.Add($"⚠ Отменено: {outputName}");
            LogService.Instance.Info(
                $"Обработка файла '{originalName}' отменена пользователем.",
                "AudioSpeedScript");
            return results;
        }

        if (success && File.Exists(tempOutputFilePath))
        {
            try
            {
                if (File.Exists(outputFilePath))
                {
                    File.Delete(outputFilePath);
                    LogService.Instance.DebugLog($"Удален существующий файл результата перед заменой: '{outputFilePath}'", "AudioSpeedScript");
                }

                MoveFileSafe(tempOutputFilePath, outputFilePath);
                LogService.Instance.DebugLog($"Временный файл успешно перемещен: '{tempOutputFilePath}' -> '{outputFilePath}'", "AudioSpeedScript");

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
            catch (Exception ex)
            {
                string moveErr = $"❌ Ошибка при сохранении итогового файла: {ex.Message}";
                results.Add(moveErr);
                LogService.Instance.Exception(ex, $"Не удалось переместить временный файл '{tempOutputFilePath}' в '{outputFilePath}'", "AudioSpeedScript");
                CleanupFailedOutputFile(tempOutputFilePath);
                CleanupFailedOutputFile(outputFilePath);
            }
        }
        else
        {
            // Очищаем временный файл и выходной файл, если они остались пустыми или поврежденными
            CleanupFailedOutputFile(tempOutputFilePath);
            CleanupFailedOutputFile(outputFilePath);

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

    /// <summary>
    /// Безопасно перемещает файл между дисками и томами с поддержкой перезаписи.
    /// </summary>
    private static void MoveFileSafe(string source, string dest)
    {
        string? destDir = Path.GetDirectoryName(dest);
        if (destDir != null && !Directory.Exists(destDir))
        {
            Directory.CreateDirectory(destDir);
        }

        if (Path.GetPathRoot(source) == Path.GetPathRoot(dest))
        {
            File.Move(source, dest, overwrite: true);
        }
        else
        {
            File.Copy(source, dest, overwrite: true);
            File.Delete(source);
        }
    }
}

