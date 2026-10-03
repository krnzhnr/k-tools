// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
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
/// Скрипт для сдвига аудиопотока (задержки или опережения) с сохранением в Lossless-форматы WAV/FLAC.
/// Все комментарии, логи и документация выполнены на русском языке в соответствии с регламентом.
/// </summary>
public sealed class AudioShiftScript : AbstractScript
{
    private readonly IFFmpegRunner _ffmpegRunner;
    private readonly IEac3toRunner _eac3toRunner;
    private readonly IMediaProbeService _mediaProbeService;

    public AudioShiftScript(
        ILogService logService,
        ISettingsManager settingsManager,
        IPathManager pathManager,
        IFFmpegRunner ffmpegRunner,
        IEac3toRunner eac3toRunner,
        IMediaProbeService mediaProbeService)
        : base(logService, settingsManager, pathManager)
    {
        _ffmpegRunner = ffmpegRunner ?? throw new ArgumentNullException(nameof(ffmpegRunner));
        _eac3toRunner = eac3toRunner ?? throw new ArgumentNullException(nameof(eac3toRunner));
        _mediaProbeService = mediaProbeService ?? throw new ArgumentNullException(nameof(mediaProbeService));
    }

    /// <summary>
    /// Русское название скрипта.
    /// </summary>
    public override string Name => AppConstants.ScriptMetadata.AudioShiftName;

    /// <summary>
    /// Русское описание назначения скрипта для интерфейса.
    /// </summary>
    public override string Description => AppConstants.ScriptMetadata.AudioShiftDesc;

    /// <summary>
    /// Категория медиаобработки.
    /// </summary>
    public override string Category => AppConstants.ScriptCategory.Audio;

    /// <summary>
    /// Имя Fluent-иконки для отображения в меню.
    /// </summary>
    public override string IconName => AppConstants.ScriptIcons.AudioShift;

    /// <summary>
    /// Список поддерживаемых расширений файлов.
    /// </summary>
    public override string[] FileExtensions => AppConstants.AudioStreams
        .Concat(AppConstants.AudioContainers)
        .ToArray();

    /// <summary>
    /// Список внешних зависимостей.
    /// </summary>
    public override string[] RequiredDependencies => new[] { "ffmpeg", "eac3to" };

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
            "ShiftMs",
            "Величина сдвига (мс)",
            SettingType.Int,
            1000,
            "Настройки сдвига"),

        new SettingField(
            "ShiftDirection",
            "Направление сдвига",
            SettingType.Combo,
            "Вперед",
            "Настройки сдвига",
            options: new List<string> { "Вперед", "Назад" }),

        new SettingField(
            "OutputFormat",
            "Формат и режим вывода",
            SettingType.Combo,
            "eac3to Bitstream (Без перекодирования)",
            "Настройки экспорта",
            options: new List<string> { "eac3to Bitstream (Без перекодирования)", "FLAC (FFmpeg Lossless)", "WAV (FFmpeg PCM)" })
    };

    /// <summary>
    /// Выполнение сдвига аудио для одного файла.
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
        string originalName = Path.GetFileName(filePath);

        _logService.Write(
            "script.audio_shift.started",
            LogLevel.Debug,
            LogStatus.Running,
            $"Начат сдвиг аудиодорожки для файла '{originalName}'",
            source: Name,
            properties: LogProps.Create("InputName", LogProps.FileName(filePath)));
        progressCallback(fileIndex, totalCount, "Чтение метаданных длительности...", 0.0);

        int shiftMs = GetSettingValue(settings, "ShiftMs", 1000);
        string direction = GetSettingValue(settings, "ShiftDirection", "Вперед");
        string format = GetSettingValue(settings, "OutputFormat", "eac3to Bitstream (Без перекодирования)");

        _logService.Write(
            "script.audio_shift.parameters",
            LogLevel.Debug,
            LogStatus.Running,
            $"Параметры сдвига: {shiftMs} мс, направление {direction}, режим {LogRedactor.CompactSafeToken(format)}",
            source: Name,
            properties: LogProps
                .Create("InputName", LogProps.FileName(filePath))
                .With("DurationMs", (double)shiftMs)
                .With("Container", LogRedactor.CompactSafeToken(format)));

        // 1. Определение пути к выходному файлу
        string targetDir = string.IsNullOrEmpty(outputPath)
            ? Path.GetDirectoryName(filePath) ?? AppContext.BaseDirectory
            : outputPath;

        bool isPassthrough = format.StartsWith("eac3to", StringComparison.OrdinalIgnoreCase);
        string inputExt = Path.GetExtension(filePath).TrimStart('.');
        string ext = isPassthrough
            ? inputExt
            : (format.Contains("FLAC", StringComparison.OrdinalIgnoreCase) ? "flac" : "wav");

        string outputName = $"{Path.GetFileNameWithoutExtension(filePath)}_shifted.{ext}";
        string outputFilePath = Path.Combine(targetDir, outputName);
        outputFilePath = GetSafeOutputPath(filePath, outputFilePath, settings);

        // Проверка флага перезаписи
        bool overwrite = _settingsManager.GetSetting("General", "OverwriteExisting", false);
        if (File.Exists(outputFilePath) && !overwrite)
        {
            string msg = $"Пропуск (существует): {outputName}";
            progressCallback(fileIndex, totalCount, msg, 100.0);
            results.Add($"⏭ ПРОПУСК (файл существует): {outputName}");
            _logService.Write(
                "script.audio_shift.output_exists",
                LogLevel.Info,
                LogStatus.Skipped,
                $"Файл результата '{LogProps.FileName(outputFilePath)}' уже существует, обработка пропущена",
                source: Name,
                properties: LogProps
                    .Create("InputName", LogProps.FileName(filePath))
                    .With("OutputName", LogProps.FileName(outputFilePath))
                    .With("ArtifactExists", true));
            return ExecutionResult.Skipped(
                context,
                results,
                errorCode: "output-exists",
                outputFile: outputFilePath,
                outputExists: true);
        }

        int signedShiftMs = direction == "Назад" ? -Math.Abs(shiftMs) : Math.Abs(shiftMs);

        // 2. Обработка через eac3to Bitstream (без перекодирования)
        if (isPassthrough)
        {
            progressCallback(fileIndex, totalCount, "Запуск eac3to прямоточного сдвига (Bitstream)...", 10.0);

            string shiftArg = signedShiftMs >= 0 ? $"+{signedShiftMs}ms" : $"{signedShiftMs}ms";
            var eac3toArgs = new List<string>
            {
                $"\"{filePath}\"",
                $"\"{outputFilePath}\"",
                shiftArg,
                "-silence",
                "-progressnumbers",
                "-log=nul"
            };

            using var ctsEac3 = new CancellationTokenSource();
            var eac3Task = _eac3toRunner.RunAsync(
                args: eac3toArgs,
                onProgress: pct =>
                {
                    string text = $"Сдвиг eac3to... {pct:F1}%";
                    progressCallback(fileIndex, totalCount, text, pct);
                },
                cancellationToken: ctsEac3.Token);

            while (!eac3Task.IsCompleted)
            {
                if (IsCancelled)
                {
                    ctsEac3.Cancel();
                    break;
                }
                await Task.Delay(200);
            }

            ProcessResult? eac3Success = null;
            try
            {
                eac3Success = await eac3Task;
            }
            catch (Exception ex)
            {
                _logService.Write(
                "script.audio_shift.eac3to_failed",
                LogLevel.Error,
                LogStatus.Failed,
                $"Сдвиг аудио через eac3to для '{originalName}' не выполнен",
                ex,
                Name,
                properties: LogProps
                    .Create("Tool", "eac3to")
                    .With("InputName", LogProps.FileName(filePath))
                    .With("ErrorCode", "EAC3TO_FAILED")
                    .With("Retryable", true));
            }

            if (IsCancelled || eac3Success?.IsSuccess != true || !File.Exists(outputFilePath))
            {
                await CleanupFailedOutputFileAsync(outputFilePath);
                if (IsCancelled)
                {
                    results.Add($"⚠ Отменено: {outputName}");
                    _logService.Write(
                "script.audio_shift.cancelled",
                LogLevel.Info,
                LogStatus.Cancelled,
                $"Сдвиг аудио для '{originalName}' отменён",
                source: Name,
                properties: LogProps
                    .Create("InputName", LogProps.FileName(filePath))
                    .With("Reason", "UserRequested")
                    .With("CleanupState", "NotStarted"));
                }
                else
                {
                    results.Add($"❌ Ошибка обработки файла для {originalName}");
                    _logService.Write(
                "script.audio_shift.eac3to_failed",
                LogLevel.Error,
                LogStatus.Failed,
                $"Прямоточный сдвиг аудио для '{LogProps.FileName(filePath)}' не выполнен, см. ограниченный хвост вывода eac3to",
                source: Name,
                properties: LogProps
                    .Create("Tool", "eac3to")
                    .With("InputName", LogProps.FileName(filePath))
                    .With("ErrorCode", "EAC3TO_FAILED")
                    .With("Retryable", true));
                }
                progressCallback(fileIndex, totalCount, "Ошибка или отмена", 100.0);
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
                        errorCode: "process-failed",
                        outputFile: outputFilePath,
                        outputExists: File.Exists(outputFilePath));
            }

            _logService.Write(
                "script.audio_shift.eac3to_succeeded",
                LogLevel.Info,
                LogStatus.Succeeded,
                $"Прямоточный сдвиг аудио через eac3to выполнен: '{LogProps.FileName(outputFilePath)}'",
                source: Name,
                properties: LogProps
                    .Create("Tool", "eac3to")
                    .With("OutputName", LogProps.FileName(outputFilePath))
                    .With("ArtifactVerified", true));
            progressCallback(fileIndex, totalCount, "Завершено", 100.0);
            results.Add($"✔ Сдвиг аудио (Bitstream) выполнен успешно: {outputName}");
            return ExecutionResult.Succeeded(context, results, outputFile: outputFilePath, outputExists: File.Exists(outputFilePath));
        }

        // 3. Получение длительности для FFmpeg Lossless
        double duration = 0.0;
        try
        {
            var structure = await _mediaProbeService.ProbeAsync(filePath);
            if (structure != null)
            {
                duration = structure.Duration;
            }
        }
        catch (Exception ex)
        {
            _logService.Write(
                "script.audio_shift.duration_read_failed",
                LogLevel.Warning,
                LogStatus.PartiallySucceeded,
                $"Метаданные длительности для '{originalName}' не прочитаны",
                ex,
                Name,
                properties: LogProps
                    .Create("InputName", LogProps.FileName(filePath))
                    .With("ErrorCode", "DURATION_READ_FAILED")
                    .With("Retryable", false));
        }

        // 4. Формирование аргументов FFmpeg
        var extraArgs = new List<string>();

        if (direction == "Вперед")
        {
            extraArgs.Add("-af");
            extraArgs.Add($"adelay={shiftMs}:all=1");
        }
        else
        {
            double shiftSec = shiftMs / 1000.0;
            string shiftSecStr = shiftSec.ToString("F3", CultureInfo.InvariantCulture);
            extraArgs.Add("-af");
            extraArgs.Add($"atrim=start={shiftSecStr},asetpts=PTS-STARTPTS");
        }

        if (ext == "flac")
        {
            extraArgs.Add("-c:a");
            extraArgs.Add("flac");
        }
        else
        {
            extraArgs.Add("-c:a");
            extraArgs.Add("pcm_s16le");
        }

        progressCallback(fileIndex, totalCount, "Запуск FFmpeg обработки...", 0.0);
        using var cts = new CancellationTokenSource();

        var runTask = _ffmpegRunner.RunAsync(
            inputPath: filePath,
            outputPath: outputFilePath,
            extraArgs: extraArgs,
            overwrite: true,
            totalDuration: duration,
            onProgress: pct =>
            {
                string text = $"Обработка сдвига... {pct.Percent:F1}%";
                progressCallback(fileIndex, totalCount, text, pct.Percent, pct.Fps, pct.Bitrate);
            },
            cancellationToken: cts.Token);

        while (!runTask.IsCompleted)
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
            success = await runTask;
        }
        catch (Exception ex)
        {
            _logService.Write(
                "script.audio_shift.ffmpeg_failed",
                LogLevel.Error,
                LogStatus.Failed,
                $"Сдвиг аудио через FFmpeg для '{originalName}' не выполнен",
                ex,
                Name,
                properties: LogProps
                    .Create("Tool", "ffmpeg")
                    .With("InputName", LogProps.FileName(filePath))
                    .With("ErrorCode", "FFMPEG_FAILED")
                    .With("Retryable", true));
        }

        if (IsCancelled || success?.IsSuccess != true || !File.Exists(outputFilePath))
        {
            await CleanupFailedOutputFileAsync(outputFilePath);
            if (IsCancelled)
            {
                results.Add($"⚠ Отменено: {outputName}");
                _logService.Write(
                "script.audio_shift.cancelled",
                LogLevel.Info,
                LogStatus.Cancelled,
                $"Сдвиг аудио для '{originalName}' отменён",
                source: Name,
                properties: LogProps
                    .Create("InputName", LogProps.FileName(filePath))
                    .With("Reason", "UserRequested")
                    .With("CleanupState", "NotStarted"));
            }
            else
            {
                results.Add($"❌ Ошибка обработки файла для {originalName}");
                _logService.Write(
                "script.audio_shift.ffmpeg_failed",
                LogLevel.Error,
                LogStatus.Failed,
                $"Сдвиг аудио для '{LogProps.FileName(filePath)}' не выполнен, см. ограниченный хвост вывода FFmpeg",
                source: Name,
                properties: LogProps
                    .Create("Tool", "ffmpeg")
                    .With("InputName", LogProps.FileName(filePath))
                    .With("ErrorCode", "FFMPEG_FAILED")
                    .With("Retryable", true));
            }
            progressCallback(fileIndex, totalCount, "Ошибка или отмена", 100.0);
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
                    errorCode: "process-failed",
                    outputFile: outputFilePath,
                    outputExists: File.Exists(outputFilePath));
        }

        _logService.Write(
            "script.audio_shift.completed",
            LogLevel.Info,
            LogStatus.Succeeded,
            $"Сдвиг аудио выполнен, результат: '{LogProps.FileName(outputFilePath)}'",
            source: Name,
            properties: LogProps
                .Create("OutputName", LogProps.FileName(outputFilePath))
                .With("Verified", true));
        progressCallback(fileIndex, totalCount, "Завершено", 100.0);
        results.Add($"✔ Сдвиг аудио выполнен успешно: {outputName}");

        return ExecutionResult.Succeeded(context, results, outputFile: outputFilePath, outputExists: File.Exists(outputFilePath));
    }
}
