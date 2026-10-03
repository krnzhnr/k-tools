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
/// Скрипт для изменения скорости и тона аудио (PAL ↔ NTSC) через eac3to.
/// </summary>
public sealed class AudioSpeedScript : AbstractScript
{
    private readonly IDependencyManager _dependencyManager;
    private readonly IFFmpegRunner _ffmpegRunner;
    private readonly IEac3toRunner _eac3toRunner;

    public AudioSpeedScript(ILogService logService, ISettingsManager settingsManager, IPathManager pathManager, IDependencyManager dependencyManager, IFFmpegRunner ffmpegRunner, IEac3toRunner eac3toRunner)
        : base(logService, settingsManager, pathManager)
    {
        _dependencyManager = dependencyManager ?? throw new ArgumentNullException(nameof(dependencyManager));
        _ffmpegRunner = ffmpegRunner ?? throw new ArgumentNullException(nameof(ffmpegRunner));
        _eac3toRunner = eac3toRunner ?? throw new ArgumentNullException(nameof(eac3toRunner));
    }

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
    public override string IconName => AppConstants.ScriptIcons.AudioSpeed;

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
                "Custom (23.976 → 24.000)",
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
        if (!_dependencyManager.IsInstalled("eac3to"))
        {
            string errMsg = "❌ Ошибка: Необходимая утилита 'eac3to' " +
                            "не установлена в системе.";
            results.Add(errMsg);
            _logService.Write("script.audio_speed.failed", LogLevel.Error, LogStatus.Failed, errMsg, source: Name, properties: LogProps.Create("ErrorCode", "AUDIO_SPEED_FAILED").With("InputName", LogProps.FileName(filePath)).With("Retryable", true));
            return ExecutionResult.Failed(
                context,
                results,
                errorCode: "missing-dependency");
        }

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
        else if (mode == "Custom (23.976 → 24.000)")
        {
            options.Add("-23.976");
            options.Add("-changeTo24.000");
            suffix = "_23_to_24";
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
        outputFilePath = GetSafeOutputPath(filePath, outputFilePath, settings);

        // Проверка флага перезаписи существующего файла
        bool overwrite = _settingsManager.GetSetting(
            "General",
            "OverwriteExisting",
            false);

        if (File.Exists(outputFilePath) && !overwrite)
        {
            string msg = $"Пропуск (существует): {outputName}";
            progressCallback(fileIndex, totalCount, msg, 100.0);
            results.Add($"⏭ ПРОПУСК (файл существует): {outputName}");
            _logService.Write("script.audio_speed.output_exists", LogLevel.Info, LogStatus.Skipped, $"Файл результата '{LogProps.FileName(outputFilePath)}' уже существует, обработка пропущена", source: Name, properties: LogProps.Create("OutputName", LogProps.FileName(outputFilePath)).With("ArtifactExists", true));
            return ExecutionResult.Skipped(
                context,
                results,
                errorCode: "output-exists",
                outputFile: outputFilePath,
                outputExists: true);
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
                _logService.Write("script.audio_speed.temp_dir_created", LogLevel.Debug, LogStatus.Succeeded, "Создана временная папка для eac3to", source: Name, properties: LogProps.Create("Tool", "eac3to").With("FileName", LogProps.FileName(tempDir)));
            }
        }
        catch (Exception ex)
        {
            _logService.Write("script.audio_speed.temp_dir_failed", LogLevel.Warning, LogStatus.PartiallySucceeded, "Временная папка для eac3to не создана, используется стандартный рабочий путь", ex, Name, properties: LogProps.Create("ErrorCode", "EAC3TO_TEMP_DIR_FAILED").With("FileName", LogProps.FileName(tempDir)));
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
            _logService.Write("script.audio_speed.predecode_started", LogLevel.Debug, LogStatus.Running, $"Контейнер {LogRedactor.CompactSafeToken(fileExtension)} не поддерживается eac3to напрямую, выполняется предварительное декодирование в WAV", source: Name, properties: LogProps.Create("Tool", "eac3to").With("Extension", LogRedactor.CompactSafeToken(fileExtension)));
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
                _logService.Write("script.audio_speed.predecode_failed", LogLevel.Error, LogStatus.Failed, $"Предварительное декодирование '{originalName}' через FFmpeg не выполнено", ex, Name, properties: LogProps.Create("ErrorCode", "PREDECODE_FAILED").With("Tool", "ffmpeg").With("InputName", LogProps.FileName(filePath)).With("Retryable", true));
            }

            if (IsCancelled || decodeSuccess?.IsSuccess != true || !File.Exists(tempInputWavPath))
            {
                await CleanupFailedOutputFileAsync(tempInputWavPath);
                if (IsCancelled)
                {
                    results.Add($"⚠ Отменено: {outputName}");
                    _logService.Write("script.audio_speed.predecode_cancelled", LogLevel.Info, LogStatus.Cancelled, $"Предварительное декодирование '{originalName}' отменено", source: Name, properties: LogProps.Create("Reason", "UserRequested").With("CleanupState", "NotStarted"));
                }
                else
                {
                    results.Add($"❌ Ошибка декодирования исходного файла для {Path.GetFileName(filePath)}");
                    _logService.Write("script.audio_speed.predecode_failed", LogLevel.Error, LogStatus.Failed, $"Предварительное декодирование в WAV для '{LogProps.FileName(filePath)}' не выполнено", source: Name, properties: LogProps.Create("ErrorCode", "PREDECODE_FAILED").With("InputName", LogProps.FileName(filePath)).With("Retryable", true));
                }
                return IsCancelled
                    ? ExecutionResult.Cancelled(
                        context,
                        results,
                        errorCode: "cancelled",
                        outputFile: outputFilePath,
                        outputExists: File.Exists(outputFilePath),
                        cleanupState: CleanupState.Completed)
                    : ExecutionResult.Failed(
                        context,
                        results,
                        errorCode: "predecode-failed",
                        outputFile: outputFilePath,
                        outputExists: File.Exists(outputFilePath));
            }

            eac3toInputPath = tempInputWavPath;
        }

        string shortInputPath = _pathManager.GetShortPath(eac3toInputPath);
        string tempOutputName = $"temp_speed_{Guid.NewGuid():N}.{ext}";
        string tempOutputFilePath = Path.Combine(tempDir, tempOutputName);

        // Подготовка аргументов для eac3to
        var eac3toArgs = new List<string>
        {
            $"\"{shortInputPath}\"",
            $"\"{tempOutputFilePath}\""
        };
        eac3toArgs.AddRange(options);

        progressCallback(
            fileIndex,
            totalCount,
            "Изменение скорости через eac3to...",
            0.0);

        using var cts = new CancellationTokenSource();
        var eac3toTask = _eac3toRunner.RunAsync(
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

        ProcessResult? success = null;
        try
        {
            success = await eac3toTask;
        }
        catch (Exception ex)
        {
            _logService.Write("script.audio_speed.eac3to_failed", LogLevel.Error, LogStatus.Failed, $"Изменение скорости через eac3to для '{originalName}' не выполнено", ex, Name, properties: LogProps.Create("ErrorCode", "EAC3TO_FAILED").With("Tool", "eac3to").With("InputName", LogProps.FileName(filePath)).With("Retryable", true));
        }
        finally
        {
            if (!string.IsNullOrEmpty(tempInputWavPath))
            {
                await CleanupFailedOutputFileAsync(tempInputWavPath);
                _logService.Write("script.audio_speed.temp_input_removed", LogLevel.Debug, LogStatus.Succeeded, "Временный входной WAV-файл удалён", source: Name, properties: LogProps.Create("FileName", LogProps.FileName(tempInputWavPath)).With("CleanupState", "Removed"));
            }
        }

        if (IsCancelled)
        {
            await CleanupFailedOutputFileAsync(tempOutputFilePath);
            await CleanupFailedOutputFileAsync(outputFilePath);
            results.Add($"⚠ Отменено: {outputName}");
            _logService.Write("script.audio_speed.cancelled", LogLevel.Info, LogStatus.Cancelled, $"Обработка '{originalName}' отменена", source: Name, properties: LogProps.Create("Reason", "UserRequested").With("CleanupState", "NotStarted"));
            return ExecutionResult.Cancelled(
                context,
                results,
                errorCode: "cancelled",
                outputFile: outputFilePath,
                outputExists: File.Exists(outputFilePath),
                cleanupState: CleanupState.Completed);
        }

        if (success?.IsSuccess == true && File.Exists(tempOutputFilePath))
        {
            try
            {
                if (File.Exists(outputFilePath))
                {
                    File.Delete(outputFilePath);
                    _logService.Write("script.audio_speed.output_replaced", LogLevel.Debug, LogStatus.Changed, "Существующий файл результата удалён перед заменой", source: Name, properties: LogProps.Create("OutputName", LogProps.FileName(outputFilePath)));
                }

                MoveFileSafe(tempOutputFilePath, outputFilePath);
                _logService.Write("script.audio_speed.temp_output_moved", LogLevel.Debug, LogStatus.Succeeded, $"Временный файл перемещён: '{LogProps.FileName(tempOutputFilePath)}' -> '{LogProps.FileName(outputFilePath)}'", source: Name, properties: LogProps.Create("OutputName", LogProps.FileName(outputFilePath)));

                progressCallback(
                    fileIndex,
                    totalCount,
                    "Успешно завершено!",
                    100.0);
                results.Add($"✅ Скорость изменена: {outputName}");
                _logService.Write("script.audio_speed.completed", LogLevel.Info, LogStatus.Succeeded, $"Изменение скорости для '{originalName}' завершено, результат: '{LogProps.FileName(outputFilePath)}'", source: Name, properties: LogProps.Create("OutputName", LogProps.FileName(outputFilePath)).With("Verified", true));

                if (deleteOriginal)
                {
                    await DeleteSourceAsync(filePath, results);
                }
            }
            catch (Exception ex)
            {
                string moveErr = $"❌ Ошибка при сохранении итогового файла: {ex.Message}";
                results.Add(moveErr);
                _logService.Write("script.audio_speed.move_failed", LogLevel.Error, LogStatus.Failed, $"Временный файл '{LogProps.FileName(tempOutputFilePath)}' не перемещён в '{LogProps.FileName(outputFilePath)}'", ex, Name, properties: LogProps.Create("ErrorCode", "TEMP_OUTPUT_MOVE_FAILED").With("OutputName", LogProps.FileName(outputFilePath)).With("CleanupState", "Failed"));
                await CleanupFailedOutputFileAsync(tempOutputFilePath);
                await CleanupFailedOutputFileAsync(outputFilePath);
            }
        }
        else
        {
            // Очищаем временный файл и выходной файл, если они остались пустыми или поврежденными
            await CleanupFailedOutputFileAsync(tempOutputFilePath);
            await CleanupFailedOutputFileAsync(outputFilePath);

            string errorMsg = $"❌ Ошибка обработки для " +
                              $"{Path.GetFileName(filePath)}";
            results.Add(errorMsg);
            _logService.Write("script.audio_speed.eac3to_failed", LogLevel.Error, LogStatus.Failed, $"eac3to не выполнил обработку '{LogProps.FileName(filePath)}', выходной файл не создан", source: Name, properties: LogProps.Create("ErrorCode", "EAC3TO_FAILED").With("Tool", "eac3to").With("InputName", LogProps.FileName(filePath)).With("ArtifactVerified", false).With("Retryable", true));
        }

        bool outputReady = File.Exists(outputFilePath);
        if (outputReady && deleteOriginal && File.Exists(filePath))
        {
            return ExecutionResult.PartiallySucceeded(
                context,
                results,
                errorCode: "source-cleanup-failed",
                outputFile: outputFilePath,
                outputExists: true,
                cleanupState: CleanupState.Failed);
        }
        if (outputReady)
        {
            return ExecutionResult.Succeeded(
                context,
                results,
                outputFile: outputFilePath,
                outputExists: true,
                cleanupState: deleteOriginal ? CleanupState.Completed : CleanupState.NotRequired);
        }
        return ExecutionResult.Failed(
            context,
            results,
            errorCode: "output-missing",
            outputFile: outputFilePath,
            outputExists: false);
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

