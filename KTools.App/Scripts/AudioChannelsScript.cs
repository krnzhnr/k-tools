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
/// Скрипт для разделения многоканального аудио на моно-WAV файлы
/// с опциональной склейкой каналов в стереопары.
/// </summary>
public sealed class AudioChannelsScript : AbstractScript
{
    /// <summary>
    /// Русское название скрипта.
    /// </summary>
    public override string Name => AppConstants.ScriptMetadata.AudioSplitName;

    /// <summary>
    /// Русское описание назначения скрипта для интерфейса.
    /// </summary>
    public override string Description => AppConstants.ScriptMetadata.AudioSplitDesc;

    /// <summary>
    /// Категория медиаобработки.
    /// </summary>
    public override string Category => AppConstants.ScriptCategory.Audio;

    /// <summary>
    /// Имя Fluent-иконки для отображения в боковом меню.
    /// </summary>
    public override string IconName => "map";

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
    public override string[] RequiredDependencies => new[] { "eac3to", "ffmpeg" };

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
            "MergeStereo",
            "Склеивать каналы в стереопары",
            SettingType.Checkbox,
            true,
            "Параметры разделения"),

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
        bool mergeStereo = GetSettingValue(
            settings,
            "MergeStereo",
            true);
        bool deleteOriginal = GetSettingValue(
            settings,
            "DeleteOriginal",
            false);

        string originalName = Path.GetFileNameWithoutExtension(filePath);

        // Динамическая проверка необходимых зависимостей
        if (!DependencyManager.Instance.IsInstalled("eac3to") ||
            !DependencyManager.Instance.IsInstalled("ffmpeg"))
        {
            string errMsg = "❌ Ошибка: Для работы скрипта необходимы " +
                            "установленные утилиты 'eac3to' и 'ffmpeg'.";
            results.Add(errMsg);
            LogService.Instance.Error(errMsg, "AudioChannelsScript");
            return results;
        }

        // Определение целевой директории сохранения
        string targetDir = string.IsNullOrEmpty(outputPath)
            ? Path.GetDirectoryName(filePath) ?? AppContext.BaseDirectory
            : outputPath;

        // eac3to генерирует моно-файлы, если целевой файл имеет расширение .wavs
        string outputFilePath = Path.Combine(
            targetDir, 
            $"{originalName}.wavs");
        outputFilePath = GetSafeOutputPath(filePath, outputFilePath);

        string basePath = Path.ChangeExtension(outputFilePath, null);

        // Перед запуском очищаем старые файлы с такими же именами (если они есть)
        CleanupAllOutputs(basePath);

        // Предотвращаем зависание и сбой eac3to из-за кириллического пути.
        // Если целевой путь содержит не-ASCII символы, сохраняем временные файлы в гарантированно
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
                LogService.Instance.DebugLog($"Создана временная папка для eac3to: '{tempDir}'", "AudioChannelsScript");
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Exception(ex, $"Не удалось создать временную директорию '{tempDir}', откат на стандартный путь", "AudioChannelsScript");
            tempDir = Path.GetTempPath();
        }

        string shortInputPath = PathManager.GetShortPath(filePath);
        string tempBaseName = $"temp_split_{Guid.NewGuid():N}";
        string tempOutputFilePath = Path.Combine(tempDir, $"{tempBaseName}.wavs");

        // Подготовка аргументов для eac3to с абсолютным путем назначения (гарантированно ASCII)
        var eac3toArgs = new List<string>
        {
            $"\"{shortInputPath}\"",
            $"\"{tempOutputFilePath}\""
        };

        progressCallback(
            fileIndex,
            totalCount,
            "Разделение каналов через eac3to...",
            0.0);

        using var cts = new CancellationTokenSource();
        var eac3toTask = Eac3toRunner.Instance.RunAsync(
            eac3toArgs,
            workingDir: tempDir,
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
                "AudioChannelsScript");
        }

        if (IsCancelled)
        {
            CleanupTempOutputs(tempDir, tempBaseName);
            CleanupAllOutputs(basePath);
            results.Add($"⚠ Отменено: {originalName}");
            LogService.Instance.Info(
                $"Разделение каналов для '{originalName}' отменено.",
                "AudioChannelsScript");
            return results;
        }

        if (!success)
        {
            CleanupTempOutputs(tempDir, tempBaseName);
            CleanupAllOutputs(basePath);
            string errorMsg = $"❌ Ошибка eac3to при разделении " +
                              $"{Path.GetFileName(filePath)}";
            results.Add(errorMsg);
            LogService.Instance.Error(
                $"Ошибка разделения каналов в eac3to для '{filePath}'.",
                "AudioChannelsScript");
            return results;
        }

        try
        {
            // Переносим и переименовываем созданные eac3to файлы
            try
            {
                if (Directory.Exists(tempDir))
                {
                    string[] tempFiles = Directory.GetFiles(tempDir, $"{tempBaseName}*");
                    foreach (string tempFile in tempFiles)
                    {
                        string fileName = Path.GetFileName(tempFile);
                        string suffix = fileName.Substring(tempBaseName.Length); // например, ".L.wav"
                        string finalPath = basePath + suffix;

                        if (File.Exists(finalPath))
                        {
                            File.Delete(finalPath);
                        }
                        MoveFileSafe(tempFile, finalPath);
                        LogService.Instance.DebugLog($"Временный моно-канал перемещен: '{tempFile}' -> '{finalPath}'", "AudioChannelsScript");
                    }
                }
            }
            catch (Exception ex)
            {
                CleanupTempOutputs(tempDir, tempBaseName);
                CleanupAllOutputs(basePath);
                string errorMsg = $"❌ Ошибка перемещения моно-каналов для {Path.GetFileName(filePath)}";
                results.Add(errorMsg);
                LogService.Instance.Exception(ex, $"Не удалось переименовать временные моно-файлы после eac3to для '{originalName}'", "AudioChannelsScript");
                return results;
            }


            // Если разделение прошло успешно и включена склейка стереопар
            if (mergeStereo)
            {
                progressCallback(
                    fileIndex,
                    totalCount,
                    "Склеивание стереопар через FFmpeg...",
                    50.0);

                string fileL = $"{basePath}.L.wav";
                string fileR = $"{basePath}.R.wav";
                string fileSl = $"{basePath}.SL.wav";
                string fileSr = $"{basePath}.SR.wav";
                string fileBl = $"{basePath}.BL.wav";
                string fileBr = $"{basePath}.BR.wav";

                // Склеиваем Front L + R
                if (File.Exists(fileL) && File.Exists(fileR))
                {
                    await MergeStereoChannelsAsync(
                        fileL,
                        fileR,
                        $"{basePath}.LR.wav",
                        cts.Token);
                }

                // Склеиваем Surround L + R
                if (File.Exists(fileSl) && File.Exists(fileSr))
                {
                    await MergeStereoChannelsAsync(
                        fileSl,
                        fileSr,
                        $"{basePath}.SLSR.wav",
                        cts.Token);
                }

                // Склеиваем Back L + R
                if (File.Exists(fileBl) && File.Exists(fileBr))
                {
                    await MergeStereoChannelsAsync(
                        fileBl,
                        fileBr,
                        $"{basePath}.BLBR.wav",
                        cts.Token);
                }
            }

            if (IsCancelled)
            {
                CleanupAllOutputs(basePath);
                results.Add($"⚠ Отменено: {originalName}");
                return results;
            }

            // Сканируем созданные файлы результатов
            var createdFiles = new List<string>();
            string[] suffixes = {
                ".L.wav", ".R.wav", ".C.wav", ".LFE.wav", ".SL.wav", ".SR.wav",
                ".BL.wav", ".BR.wav", ".LR.wav", ".SLSR.wav", ".BLBR.wav"
            };

            foreach (var suffix in suffixes)
            {
                string path = $"{basePath}{suffix}";
                if (File.Exists(path))
                {
                    createdFiles.Add(Path.GetFileName(path));
                }
            }

            if (createdFiles.Count > 0)
            {
                progressCallback(
                    fileIndex,
                    totalCount,
                    "Успешно завершено!",
                    100.0);

                results.Add($"✅ Разделение завершено для: {originalName}");
                foreach (var file in createdFiles)
                {
                    results.Add($"  • Создан канал: {file}");
                }

                LogService.Instance.Info(
                    $"Успешно завершено разделение каналов для '{originalName}'. " +
                    $"Создано файлов: {createdFiles.Count}",
                    "AudioChannelsScript");

                if (deleteOriginal)
                {
                    DeleteSource(filePath, results);
                }
            }
            else
            {
                results.Add($"❌ Не найдено выходных файлов для " +
                            $"{Path.GetFileName(filePath)}");
            }
        }
        catch (Exception ex)
        {
            CleanupTempOutputs(tempDir, tempBaseName);
            CleanupAllOutputs(basePath);
            string errorMsg = $"❌ Ошибка выполнения скрипта для {Path.GetFileName(filePath)}: {ex.Message}";
            results.Add(errorMsg);
            LogService.Instance.Exception(ex, $"Ошибка обработки каналов для '{originalName}': {ex.Message}", "AudioChannelsScript");
        }

        return results;
    }

    /// <summary>
    /// Склеивает левый и правый моно-каналы в стереофайл pcm_s24le через FFmpeg.
    /// </summary>
    private async Task<bool> MergeStereoChannelsAsync(
        string fileLeft,
        string fileRight,
        string fileOutput,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(fileLeft) || !File.Exists(fileRight))
        {
            return false;
        }

        LogService.Instance.Info(
            $"Склеивание моно-каналов '{Path.GetFileName(fileLeft)}' и " +
            $"'{Path.GetFileName(fileRight)}' в стереопару...",
            "AudioChannelsScript");

        var extraArgs = new List<string>
        {
            "-i", $"\"{fileRight}\"",
            "-filter_complex", "join=inputs=2:channel_layout=stereo",
            "-c:a", "pcm_s24le"
        };

        bool success = await FFmpegRunner.Instance.RunAsync(
            inputPath: fileLeft,
            outputPath: fileOutput,
            extraArgs: extraArgs,
            overwrite: true,
            cancellationToken: cancellationToken);

        if (success && File.Exists(fileOutput))
        {
            try
            {
                File.Delete(fileLeft);
                File.Delete(fileRight);
                LogService.Instance.Info(
                    $"Успешно склеены каналы в '{Path.GetFileName(fileOutput)}'. " +
                    $"Исходные моно-файлы удалены.",
                    "AudioChannelsScript");
                return true;
            }
            catch (Exception ex)
            {
                LogService.Instance.Exception(
                    ex,
                    $"Ошибка при удалении моно-файлов после склеивания: " +
                    $"{ex.Message}",
                    "AudioChannelsScript");
            }
        }
        else
        {
            LogService.Instance.Error(
                $"Не удалось склеить каналы в '{Path.GetFileName(fileOutput)}'.",
                "AudioChannelsScript");
        }

        return false;
    }

    /// <summary>
    /// Физически удаляет все возможные выходные файлы скрипта с диска.
    /// </summary>
    private void CleanupAllOutputs(string basePath)
    {
        string[] suffixes = {
            ".L.wav", ".R.wav", ".C.wav", ".LFE.wav", ".SL.wav", ".SR.wav",
            ".BL.wav", ".BR.wav", ".LR.wav", ".SLSR.wav", ".BLBR.wav"
        };

        foreach (var suffix in suffixes)
        {
            string path = $"{basePath}{suffix}";
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    LogService.Instance.DebugLog(
                        $"Удален выходной/временный файл: '{Path.GetFileName(path)}'",
                        "AudioChannelsScript");
                }
            }
            catch (Exception ex)
            {
                LogService.Instance.Warn(
                    $"Не удалось удалить файл '{Path.GetFileName(path)}' " +
                    $"при очистке: {ex.Message}",
                    "AudioChannelsScript");
            }
        }
    }

    /// <summary>
    /// Физически удаляет временные моно-файлы eac3to из целевой папки при отмене или ошибках.
    /// </summary>
    private void CleanupTempOutputs(string targetDir, string tempBaseName)
    {
        try
        {
            if (Directory.Exists(targetDir))
            {
                string[] tempFiles = Directory.GetFiles(targetDir, $"{tempBaseName}*");
                foreach (string file in tempFiles)
                {
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                        LogService.Instance.DebugLog(
                            $"Удален неиспользованный временный моно-файл: '{Path.GetFileName(file)}'",
                            "AudioChannelsScript");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Warn(
                $"Не удалось выполнить очистку временных моно-файлов для '{tempBaseName}': {ex.Message}",
                "AudioChannelsScript");
        }
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
