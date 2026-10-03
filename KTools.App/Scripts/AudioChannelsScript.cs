// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Infrastructure;
using KTools_App.Models;
using KTools_App.Services.Contracts;

using ExecutionContext = KTools_App.Models.ExecutionContext;

namespace KTools_App.Scripts;

/// <summary>
/// Скрипт для разделения многоканального аудио на моно-WAV файлы
/// с опциональной склейкой каналов в стереопары.
/// </summary>
public sealed class AudioChannelsScript : AbstractScript
{
    private readonly IDependencyManager _dependencyManager;
    private readonly IFFmpegRunner _ffmpegRunner;
    private readonly IEac3toRunner _eac3toRunner;

    public AudioChannelsScript(ILogService logService, ISettingsManager settingsManager, IPathManager pathManager, IDependencyManager dependencyManager, IFFmpegRunner ffmpegRunner, IEac3toRunner eac3toRunner)
        : base(logService, settingsManager, pathManager)
    {
        _dependencyManager = dependencyManager ?? throw new ArgumentNullException(nameof(dependencyManager));
        _ffmpegRunner = ffmpegRunner ?? throw new ArgumentNullException(nameof(ffmpegRunner));
        _eac3toRunner = eac3toRunner ?? throw new ArgumentNullException(nameof(eac3toRunner));
    }

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
    public override string IconName => AppConstants.ScriptIcons.AudioChannels;

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
    /// Суффиксы всех выходных моно-файлов и склеенных стереопар.
    /// </summary>
    private static readonly string[] ChannelSuffixes =
    {
        ".L.wav", ".R.wav", ".C.wav", ".LFE.wav", ".SL.wav", ".SR.wav",
        ".BL.wav", ".BR.wav", ".LR.wav", ".SLSR.wav", ".BLBR.wav"
    };

    /// <summary>
    /// Асинхронное выполнение обработки одного файла.
    /// </summary>
    public override async Task<ExecutionResult> ExecuteSingleAsync(
        string filePath,
        Dictionary<string, object> settings,
        string? outputPath,
        ScriptProgressCallback progressCallback,
        int fileIndex,
        int totalCount,
        ExecutionContext context)
    {
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
        var missingDependencies = new List<string>();
        foreach (string dependency in RequiredDependencies)
        {
            if (!_dependencyManager.IsInstalled(dependency))
            {
                missingDependencies.Add(dependency);
            }
        }

        if (missingDependencies.Count > 0)
        {
            string errMsg = "❌ Ошибка: Для работы скрипта необходимы установленные утилиты: " +
                            string.Join(", ", missingDependencies) + ".";
            results.Add(errMsg);
            _logService.Write(
                "script.audio_channels.failed",
                LogLevel.Error,
                LogStatus.Failed,
                errMsg,
                source: Name,
                context: context.ToLogContext(),
                properties: LogProps
                    .Create("ErrorCode", "AUDIO_CHANNEL_SPLIT_FAILED")
                    .With("InputName", LogProps.FileName(filePath)));
            return ExecutionResult.Failed(
                context,
                results,
                errorCode: "missing-dependency",
                retryable: true,
                cleanupState: CleanupState.NotRequired);
        }

        // Определение целевой директории сохранения
        string targetDir = string.IsNullOrEmpty(outputPath)
            ? Path.GetDirectoryName(filePath) ?? AppContext.BaseDirectory
            : outputPath;

        // eac3to генерирует моно-файлы, если целевой файл имеет расширение .wavs
        string outputFilePath = Path.Combine(
            targetDir,
            $"{originalName}.wavs");
        outputFilePath = GetSafeOutputPath(filePath, outputFilePath, settings);

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
                _logService.Write(
                "script.audio_channels.temp_dir_created",
                LogLevel.Debug,
                LogStatus.Succeeded,
                "Создана временная папка для eac3to",
                source: Name,
                properties: LogProps
                    .Create("Tool", "eac3to")
                    .With("FileName", LogProps.FileName(tempDir)));
            }
        }
        catch (Exception ex)
        {
            _logService.Write(
                "script.audio_channels.temp_dir_failed",
                LogLevel.Warning,
                LogStatus.PartiallySucceeded,
                "Временная папка для eac3to не создана, используется стандартный рабочий путь",
                ex,
                Name,
                properties: LogProps
                    .Create("Tool", "eac3to")
                    .With("FileName", LogProps.FileName(tempDir))
                    .With("ErrorCode", "EAC3TO_TEMP_DIR_FAILED")
                    .With("Retryable", false));
            tempDir = Path.GetTempPath();
        }

        // Проверяем, поддерживает ли eac3to формат входного файла нативно.
        // Если нет (например, .m4a), предварительно декодируем его в WAV с помощью FFmpeg.
        var nativeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".wav", ".flac", ".ac3", ".eac3", ".dts", ".dtshd", ".truehd", ".thd", ".aac"
        };

        string fileExtension = Path.GetExtension(filePath);
        bool shouldPreDecode = !nativeExtensions.Contains(fileExtension);
        string eac3toInputPath = filePath;
        string? tempInputWavPath = null;

        if (shouldPreDecode)
        {
            _logService.Write(
                "script.audio_channels.predecode_started",
                LogLevel.Debug,
                LogStatus.Running,
                $"Контейнер {LogRedactor.CompactSafeToken(fileExtension)} не поддерживается eac3to напрямую, выполняется предварительное декодирование в WAV",
                source: Name,
                properties: LogProps
                    .Create("Tool", "eac3to")
                    .With("Extension", LogRedactor.CompactSafeToken(fileExtension))
                    .With("Container", "wav"));
            progressCallback(
                fileIndex,
                totalCount,
                "Декодирование во временный WAV...",
                0.0);

            tempInputWavPath = Path.Combine(tempDir, $"temp_input_{Guid.NewGuid():N}.wav");

            var decodeArgs = new List<string> { "-c:a", "pcm_s24le" };
            using var decodeCts = new CancellationTokenSource();

            var decodeTask = _ffmpegRunner.RunAsync(
                inputPath: filePath,
                outputPath: tempInputWavPath,
                extraArgs: decodeArgs,
                overwrite: true,
                onProgress: p =>
                {
                    string pLabel = p.Percent > 0 ? $"{p.Percent:F1}%" : $"{p.TimeSeconds:F1} сек";
                    progressCallback(fileIndex, totalCount, $"Декодирование во временный WAV ({pLabel})...", p.Percent, p.Fps, p.Bitrate);
                },
                cancellationToken: decodeCts.Token);

            while (!decodeTask.IsCompleted)
            {
                if (IsCancelled)
                {
                    decodeCts.Cancel();
                    break;
                }
                await Task.Delay(200);
            }

            ProcessResult? decodeSuccess = null;
            try
            {
                decodeSuccess = await decodeTask;
            }
            catch (Exception ex)
            {
                _logService.Write(
                "script.audio_channels.predecode_failed",
                LogLevel.Error,
                LogStatus.Failed,
                $"Предварительное декодирование файла '{originalName}' через FFmpeg не выполнено",
                ex,
                Name,
                properties: LogProps
                    .Create("Tool", "ffmpeg")
                    .With("InputName", LogProps.FileName(filePath))
                    .With("ErrorCode", "PREDECODE_FAILED")
                    .With("Retryable", true));
            }

            if (IsCancelled || decodeSuccess?.IsSuccess != true || !File.Exists(tempInputWavPath))
            {
                await CleanupFailedOutputFileAsync(tempInputWavPath);
                CleanupAllOutputs(basePath);
                if (IsCancelled)
                {
                    results.Add($"⚠ Отменено: {originalName}");
                    _logService.Write(
                "script.audio_channels.predecode_cancelled",
                LogLevel.Info,
                LogStatus.Cancelled,
                $"Предварительное декодирование файля '{originalName}' отменено",
                source: Name,
                properties: LogProps
                    .Create("InputName", LogProps.FileName(filePath))
                    .With("Reason", "UserRequested")
                    .With("CleanupState", "NotStarted"));
                }
                else
                {
                    results.Add($"❌ Ошибка декодирования исходного файла для {Path.GetFileName(filePath)}");
                    _logService.Write(
                "script.audio_channels.predecode_failed",
                LogLevel.Error,
                LogStatus.Failed,
                $"Предварительное декодирование в WAV для '{LogProps.FileName(filePath)}' не выполнено",
                source: Name,
                properties: LogProps
                    .Create("InputName", LogProps.FileName(filePath))
                    .With("ErrorCode", "PREDECODE_FAILED")
                    .With("Retryable", true));
                }
                return IsCancelled
                    ? ExecutionResult.Cancelled(
                        context,
                        results,
                        errorCode: "cancelled",
                        outputFile: outputFilePath,
                        outputExists: false,
                        cleanupState: CleanupState.Completed)
                    : ExecutionResult.Failed(
                        context,
                        results,
                        errorCode: "predecode-failed",
                        outputFile: outputFilePath,
                        outputExists: false);
            }

            eac3toInputPath = tempInputWavPath;
        }

        string shortInputPath = _pathManager.GetShortPath(eac3toInputPath);
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
        var eac3toTask = _eac3toRunner.RunAsync(
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

        ProcessResult? success = null;
        try
        {
            success = await eac3toTask;
        }
        catch (Exception ex)
        {
            _logService.Write(
                "script.audio_channels.eac3to_failed",
                LogLevel.Error,
                LogStatus.Failed,
                $"Разделение каналов через eac3to для '{originalName}' не выполнено",
                ex,
                Name,
                properties: LogProps
                    .Create("Tool", "eac3to")
                    .With("InputName", LogProps.FileName(filePath))
                    .With("ErrorCode", "EAC3TO_FAILED")
                    .With("Retryable", true));
        }
        finally
        {
            if (!string.IsNullOrEmpty(tempInputWavPath))
            {
                await CleanupFailedOutputFileAsync(tempInputWavPath);
                _logService.Write(
                "script.audio_channels.temp_input_removed",
                LogLevel.Debug,
                LogStatus.Succeeded,
                "Временный входной WAV-файл удалён",
                source: Name,
                properties: LogProps
                    .Create("FileName", LogProps.FileName(tempInputWavPath))
                    .With("CleanupState", "Removed"));
            }
        }

        if (IsCancelled)
        {
            CleanupTempOutputs(tempDir, tempBaseName);
            CleanupAllOutputs(basePath);
            results.Add($"⚠ Отменено: {originalName}");
            _logService.Write(
                "script.audio_channels.cancelled",
                LogLevel.Info,
                LogStatus.Cancelled,
                $"Разделение каналов для '{originalName}' отменено",
                source: Name,
                properties: LogProps
                    .Create("InputName", LogProps.FileName(filePath))
                    .With("Reason", "UserRequested")
                    .With("CleanupState", "Completed"));
            return ExecutionResult.Cancelled(
                context,
                results,
                errorCode: "cancelled",
                outputFile: outputFilePath,
                outputExists: false,
                cleanupState: CleanupState.Completed);
        }

        if (success?.IsSuccess != true)
        {
            CleanupTempOutputs(tempDir, tempBaseName);
            CleanupAllOutputs(basePath);
            string errorMsg = $"❌ Ошибка eac3to при разделении " +
                              $"{Path.GetFileName(filePath)}";
            results.Add(errorMsg);
            _logService.Write(
                "script.audio_channels.eac3to_failed",
                LogLevel.Error,
                LogStatus.Failed,
                $"Разделение каналов в eac3to для '{LogProps.FileName(filePath)}' не выполнено",
                source: Name,
                properties: LogProps
                    .Create("Tool", "eac3to")
                    .With("InputName", LogProps.FileName(filePath))
                    .With("ErrorCode", "EAC3TO_FAILED")
                    .With("Retryable", true));
            return ExecutionResult.Failed(
                context,
                results,
                errorCode: "process-failed",
                outputFile: outputFilePath,
                outputExists: false);
        }

        var mergeFailures = new List<string>();
        try
        {
            // Переносим и переименовываем созданные eac3то файлы
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
                        _logService.Write(
                "script.audio_channels.mono_moved",
                LogLevel.Debug,
                LogStatus.Succeeded,
                $"Временный монофайл перемещён: '{LogProps.FileName(tempFile)}' -> '{LogProps.FileName(finalPath)}'",
                source: Name,
                properties: LogProps
                    .Create("OutputName", LogProps.FileName(finalPath))
                    .With("Container", "wav"));
                    }
                }
            }
            catch (Exception ex)
            {
                CleanupTempOutputs(tempDir, tempBaseName);
                CleanupAllOutputs(basePath);
                string errorMsg = $"❌ Ошибка перемещения моно-каналов для {Path.GetFileName(filePath)}";
                results.Add(errorMsg);
                _logService.Write(
                "script.audio_channels.rename_failed",
                LogLevel.Error,
                LogStatus.Failed,
                $"Временные монофайлы после eac3to для '{originalName}' переименовать не удалось",
                ex,
                Name,
                properties: LogProps
                    .Create("InputName", LogProps.FileName(filePath))
                    .With("ErrorCode", "TEMP_MONO_RENAME_FAILED")
                    .With("CleanupState", "Failed"));
                return ExecutionResult.Failed(
                    context,
                    results,
                    errorCode: "output-move-failed",
                    outputFile: outputFilePath,
                    outputExists: false);
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
                if (File.Exists(fileL) && File.Exists(fileR) &&
                    !await MergeStereoChannelsAsync(fileL, fileR, $"{basePath}.LR.wav", cts.Token))
                {
                    mergeFailures.Add(Path.GetFileName($"{basePath}.LR.wav"));
                }

                // Склеиваем Surround L + R
                if (File.Exists(fileSl) && File.Exists(fileSr) &&
                    !await MergeStereoChannelsAsync(fileSl, fileSr, $"{basePath}.SLSR.wav", cts.Token))
                {
                    mergeFailures.Add(Path.GetFileName($"{basePath}.SLSR.wav"));
                }

                // Склеиваем Back L + R
                if (File.Exists(fileBl) && File.Exists(fileBr) &&
                    !await MergeStereoChannelsAsync(fileBl, fileBr, $"{basePath}.BLBR.wav", cts.Token))
                {
                    mergeFailures.Add(Path.GetFileName($"{basePath}.BLBR.wav"));
                }

                foreach (string failed in mergeFailures)
                {
                    results.Add($"⚠ Не удалось склеить стереопару: {failed}");
                }
            }

            if (IsCancelled)
            {
                CleanupAllOutputs(basePath);
                results.Add($"⚠ Отменено: {originalName}");
                return ExecutionResult.Cancelled(
                    context,
                    results,
                    errorCode: "cancelled",
                    outputFile: outputFilePath,
                    outputExists: false,
                    cleanupState: CleanupState.Completed);
            }

            var createdFiles = new List<string>();
            foreach (string suffix in ChannelSuffixes)
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

                _logService.Write(
                "script.audio_channels.completed",
                LogLevel.Info,
                LogStatus.Succeeded,
                $"Разделение каналов для '{originalName}' завершено, создано файлов: {createdFiles.Count}",
                source: Name,
                properties: LogProps
                    .Create("InputName", LogProps.FileName(filePath))
                    .With("Count", createdFiles.Count)
                    .With("Verified", true));

                if (deleteOriginal)
                {
                    await DeleteSourceAsync(filePath, results);
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
            _logService.Write(
                "script.audio_channels.failed",
                LogLevel.Error,
                LogStatus.Failed,
                $"Обработка каналов для '{originalName}' не выполнена",
                ex,
                Name,
                properties: LogProps
                    .Create("InputName", LogProps.FileName(filePath))
                    .With("ErrorCode", "AUDIO_CHANNEL_SPLIT_FAILED")
                    .With("Retryable", true));
            mergeFailures.Clear();
        }

        var existingOutputs = new List<string>();
        foreach (string suffix in ChannelSuffixes)
        {
            string path = $"{basePath}{suffix}";
            if (File.Exists(path))
            {
                existingOutputs.Add(path);
            }
        }

        if (existingOutputs.Count == 0)
        {
            return ExecutionResult.Failed(
                context,
                results,
                errorCode: "output-missing",
                outputFile: null,
                outputExists: false,
                cleanupState: CleanupState.Completed);
        }

        string actualOutput = existingOutputs[0];
        bool actualOutputExists = File.Exists(actualOutput);

        if (mergeFailures.Count > 0)
        {
            return ExecutionResult.PartiallySucceeded(
                context,
                results,
                errorCode: "channel-merge-failed",
                outputFile: actualOutput,
                outputExists: actualOutputExists,
                retryable: true,
                cleanupState: CleanupState.Partial);
        }

        if (deleteOriginal && File.Exists(filePath))
        {
            return ExecutionResult.PartiallySucceeded(
                context,
                results,
                errorCode: "source-cleanup-failed",
                outputFile: actualOutput,
                outputExists: actualOutputExists,
                cleanupState: CleanupState.Failed);
        }

        return ExecutionResult.Succeeded(
            context,
            results,
            outputFile: actualOutput,
            outputExists: actualOutputExists,
            cleanupState: deleteOriginal ? CleanupState.Completed : CleanupState.NotRequired);
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

        _logService.Write(
        "script.audio_channels.stereo_merge_started",
        LogLevel.Debug,
        LogStatus.Running,
        $"Монофайлы '{LogProps.FileName(fileLeft)}' и '{LogProps.FileName(fileRight)}' объединяются в стереопару",
        source: Name,
        properties: LogProps
            .Create("Tool", "ffmpeg")
            .With("InputName", LogProps.FileName(fileLeft))
            .With("AudioChannels", 2));

        var extraArgs = new List<string>
    {
        "-i", $"\"{fileRight}\"",
        "-filter_complex", "join=inputs=2:channel_layout=stereo",
        "-c:a", "pcm_s24le"
    };

        ProcessResult success = await _ffmpegRunner.RunAsync(
            inputPath: fileLeft,
            outputPath: fileOutput,
            extraArgs: extraArgs,
            overwrite: true,
            cancellationToken: cancellationToken);

        if (success.IsSuccess && File.Exists(fileOutput))
        {
            try
            {
                File.Delete(fileLeft);
                File.Delete(fileRight);
                _logService.Write(
                "script.audio_channels.stereo_merge_completed",
                LogLevel.Info,
                LogStatus.Succeeded,
                $"Монофайлы объединены в стереопару '{LogProps.FileName(fileOutput)}', исходные монофайлы удалены",
                source: Name,
                properties: LogProps
                    .Create("OutputName", LogProps.FileName(fileOutput))
                    .With("AudioChannels", 2)
                    .With("Verified", true));
                return true;
            }
            catch (Exception ex)
            {
                _logService.Write(
                "script.audio_channels.stereo_cleanup_failed",
                LogLevel.Warning,
                LogStatus.PartiallySucceeded,
                "Исходные монофайлы после объединения в стереопару не удалены",
                ex,
                Name,
                properties: LogProps
                    .Create("ErrorCode", "MONO_CLEANUP_FAILED")
                    .With("CleanupState", "Failed"));
            }
        }
        else
        {
            _logService.Write(
            "script.audio_channels.stereo_merge_failed",
            LogLevel.Error,
            LogStatus.Failed,
            $"Объединить монофайлы в стереопару '{LogProps.FileName(fileOutput)}' не удалось",
            source: Name,
            properties: LogProps
                .Create("OutputName", LogProps.FileName(fileOutput))
                .With("ErrorCode", "STEREO_MERGE_FAILED")
                .With("Retryable", true));
        }

        return false;
    }

    /// <summary>
    /// Физически удаляет все возможные выходные файлы скрипта с диска.
    /// </summary>
    private void CleanupAllOutputs(string basePath)
    {
        foreach (var suffix in ChannelSuffixes)
        {
            string path = $"{basePath}{suffix}";
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    _logService.Write(
                    "script.audio_channels.output_removed",
                    LogLevel.Debug,
                    LogStatus.Succeeded,
                    $"Временный выходной файл удалён: '{LogProps.FileName(path)}'",
                    source: Name,
                    properties: LogProps
                        .Create("OutputName", LogProps.FileName(path))
                        .With("CleanupState", "Removed"));
                }
            }
            catch (Exception ex)
            {
                _logService.Write(
                "script.audio_channels.cleanup_failed",
                LogLevel.Warning,
                LogStatus.Failed,
                $"Временный файл '{LogProps.FileName(path)}' не удалён при очистке",
                ex,
                Name,
                properties: LogProps
                    .Create("OutputName", LogProps.FileName(path))
                    .With("ErrorCode", "TEMP_CLEANUP_FAILED")
                    .With("CleanupState", "Failed"));
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
                        _logService.Write(
                        "script.audio_channels.unused_mono_removed",
                        LogLevel.Debug,
                        LogStatus.Succeeded,
                        $"Неиспользованный временный монофайл удалён: '{LogProps.FileName(file)}'",
                        source: Name,
                        properties: LogProps
                            .Create("OutputName", LogProps.FileName(file))
                            .With("CleanupState", "Removed"));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logService.Write(
            "script.audio_channels.temp_cleanup_failed",
            LogLevel.Warning,
            LogStatus.Failed,
            $"Очистка временных монофайлов для '{LogProps.FileName(tempBaseName)}' не выполнена",
            ex,
            Name,
            properties: LogProps
                .Create("OutputName", LogProps.FileName(tempBaseName))
                .With("ErrorCode", "TEMP_MONO_CLEANUP_FAILED")
                .With("CleanupState", "Failed"));
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
