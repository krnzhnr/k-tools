// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Services.Contracts;

namespace KTools_App.Infrastructure;

/// <summary>
/// Синглтон-обертка для запуска утилиты FFmpeg и зонда FFprobe.
/// Предоставляет методы для выполнения транскодирования, извлечения потоков/вложений,
/// анализа структуры медиафайлов и детекции графического оборудования NVIDIA NVENC.
/// Наследует AbstractProcessRunner и реализует все требования русскоязычной локализации.
/// </summary>
public sealed class FFmpegRunner : AbstractProcessRunner, IFFmpegRunner
{
    private const string SourceName = nameof(FFmpegRunner);

    /// <summary>
    /// Инициализирует новый экземпляр FFmpegRunner с внедрением зависимостей.
    /// </summary>
    /// <param name="logService">Сервис логирования.</param>
    public FFmpegRunner(ILogService logService, IPathManager pathManager)
        : base(logService, pathManager)
    {
    }



    /// <summary>
    /// Запустить процесс обработки медиа через FFmpeg с отслеживанием прогресса в реальном времени.
    /// </summary>
    /// <param name="inputPath">Абсолютный путь к входному медиафайлу.</param>
    /// <param name="outputPath">Абсолютный путь к выходному медиафайлу.</param>
    /// <param name="extraArgs">Список дополнительных аргументов вывода (например, кодеки, битрейты).</param>
    /// <param name="inputArgs">Список входных параметров, указываемых перед флагом "-i".</param>
    /// <param name="overwrite">Флаг принудительной перезаписи существующего выходного файла.</param>
    /// <param name="totalDuration">Общая длительность медиафайла в секундах для расчета процента прогресса.</param>
    /// <param name="onProgress">Делегат обратного вызова для передачи информации о прогрессе.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    public async Task<ProcessResult> RunAsync(
        string inputPath,
        string? outputPath = null,
        List<string>? extraArgs = null,
        List<string>? inputArgs = null,
        bool overwrite = false,
        double totalDuration = 0.0,
        Action<ProgressInfo>? onProgress = null,
        CancellationToken cancellationToken = default,
        ProcessExecutionContext? context = null)
    {
        string overwriteFlag = overwrite ? "-y" : "-n";

        // Формируем командную строку FFmpeg
        var argsList = new List<string>
        {
            "-hide_banner",
            "-loglevel", "info",
            "-stats",
            "-stats_period", "0.1",
            overwriteFlag
        };

        if (inputArgs != null)
        {
            argsList.AddRange(inputArgs);
        }

        argsList.Add("-i");
        argsList.Add($"\"{inputPath}\"");

        if (extraArgs != null)
        {
            argsList.AddRange(extraArgs);
        }

        if (!string.IsNullOrEmpty(outputPath))
        {
            argsList.Add($"\"{outputPath}\"");
        }

        string arguments = string.Join(" ", argsList);
        string inputName = Path.GetFileName(inputPath);
        ProcessExecutionContext executionContext = (context ?? ProcessExecutionContext.NewOperation("ffmpeg"))
            .WithExpectedArtifact(outputPath);

        double effectiveDuration = totalDuration;

        var result = await RunProcessAsync(
            "ffmpeg",
            arguments,
            onOutputLine: null,
            onErrorLine: line =>
            {
                // Автоматическое обнаружение точной длительности из заголовочного вывода FFmpeg (Duration: HH:MM:SS.ms)
                if (onProgress != null && effectiveDuration <= 0)
                {
                    double parsedHeaderDuration = FFmpegOutputParser.ParseHeaderDuration(line, Log);
                    if (parsedHeaderDuration > 0)
                    {
                        effectiveDuration = parsedHeaderDuration;
                        Log.Write(
                        "media.duration.header_override",
                        LogLevel.Debug,
                        LogStatus.Succeeded,
                        $"Длительность для '{LogProps.FileName(inputName)}' уточнена по заголовку FFmpeg",
                        source: SourceName,
                        properties: LogProps
                            .Create("InputName", LogProps.FileName(inputName))
                            .With("Tool", "ffmpeg")
                            .With("DurationMs", parsedHeaderDuration * 1000d));
                    }
                }

                // Парсинг прогресса из вывода (вызываем при успешном считывании строки прогресса)
                if (onProgress != null)
                {
                    var progress = FFmpegOutputParser.ParseLine(line, effectiveDuration, Log);
                    if (progress != null)
                    {
                        onProgress(progress);
                    }
                }
            },
            cancellationToken,
            context: executionContext,
            expectedArtifact: outputPath
        );

        if (result.IsSuccess)
        {
            return result;
        }

        if (!result.IsCancelled && result.ErrorCode != ProcessResult.ErrorArtifactMissing)
        {
            Log.Write(
            ProcessEventIds.Exit,
            LogLevel.Error,
            LogStatus.Failed,
            $"FFmpeg завершился с ошибкой для '{LogProps.FileName(inputName)}'; ограниченный хвост вывода сохранён в журнале выполнения",
            source: SourceName,
            context: executionContext?.ToLogContext(),
            properties: LogProps
                .Create("Tool", "ffmpeg")
                .With("InputName", LogProps.FileName(inputName))
                .With("ExitCode", result.ExitCode)
                .With("ErrorCode", "FFMPEG_EXIT_NONZERO"));
        }

        if (!string.IsNullOrEmpty(outputPath) && !result.IsCancelled)
        {
            await DeleteDamagedOutputAsync(outputPath, executionContext ?? ProcessExecutionContext.Create("ffmpeg"));
        }

        return result;
    }

    private async Task DeleteDamagedOutputAsync(string outputPath, ProcessExecutionContext executionContext)
    {
        for (int attempt = 0; attempt < 6; attempt++)
        {
            if (!File.Exists(outputPath))
            {
                return;
            }

            try
            {
                File.Delete(outputPath);
                Log.Write(
                ProcessEventIds.ArtifactMissing,
                LogLevel.Debug,
                LogStatus.Succeeded,
                "Повреждённый выходной файл удалён после остановки FFmpeg",
                source: SourceName,
                context: executionContext?.ToLogContext(),
                properties: LogProps
                    .Create("Tool", "ffmpeg")
                    .With("OutputName", LogProps.FileName(outputPath))
                    .With("CleanupState", "Removed"));
                return;
            }
            catch (Exception deleteEx)
            {
                if (attempt < 5)
                {
                    await Task.Delay(150);
                    continue;
                }

                Log.Write(
                    ProcessEventIds.ArtifactMissing,
                    LogLevel.Warning,
                    LogStatus.PartiallySucceeded,
                    $"Не удалось удалить повреждённый выходной файл '{Path.GetFileName(outputPath)}' после остановки FFmpeg",
                    deleteEx,
                    "FFmpegRunner",
                    context: executionContext.ToLogContext(),
                    properties: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["Stage"] = "artifact_cleanup",
                        ["ErrorCode"] = ProcessResult.ErrorArtifactMissing
                    });
                return;
            }
        }
    }

    /// <summary>
    /// Получить подробную техническую информацию о структуре медиафайла в формате JSON с помощью FFprobe.
    /// </summary>
    /// <param name="filePath">Абсолютный путь к исследуемому файлу.</param>
    /// <returns>Документ JsonDocument со свойствами потоков, или null при сбоях.</returns>
    public async Task<JsonDocument?> GetVideoInfoAsync(string filePath, ProcessExecutionContext? context = null)
    {
        string arguments = $"-v error -analyzeduration 100M -probesize 100M -show_entries format=duration,bit_rate:stream=index,codec_name,codec_type,duration,bit_rate,disposition,pix_fmt,width,height,channels:stream_tags -of json \"{filePath}\"";
        string fileName = Path.GetFileName(filePath);
        var outputLines = new List<string>();

        var result = await RunProcessAsync(
            "ffprobe",
            arguments,
            onOutputLine: line => outputLines.Add(line),
            onErrorLine: null,
            CancellationToken.None,
            context: context ?? ProcessExecutionContext.NewOperation("ffprobe"),
            verifyParseResult: () => outputLines.Count > 0);

        if (!result.IsSuccess)
        {
            Dictionary<string, object?> probeProperties = result.ToLogProperties();
            Log.Write(
                ProcessEventIds.PostconditionFailed,
                LogLevel.Error,
                LogStatus.Failed,
                $"Ошибка вызова ffprobe для файла '{fileName}'",
                null,
                "FFmpegRunner",
                result.Context.ToLogContext().WithProcess(result.ProcessId),
                probeProperties.With("ErrorCode", result.ErrorCode ?? "FFPROBE_INVOKE_FAILED"));
            return null;
        }

        string fullOutput = string.Join("", outputLines);
        if (string.IsNullOrWhiteSpace(fullOutput))
        {
            Log.Write(
                ProcessEventIds.PostconditionFailed,
                LogLevel.Error,
                LogStatus.Failed,
                $"ffprobe вернул пустой вывод для файла '{fileName}'",
                null,
                "FFmpegRunner",
                result.Context.ToLogContext().WithProcess(result.ProcessId),
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["ErrorCode"] = ProcessResult.ErrorOutputEmpty,
                    ["OutputName"] = fileName
                });
            return null;
        }

        try
        {
            return JsonDocument.Parse(fullOutput);
        }
        catch (JsonException ex)
        {
            Log.Write(
                ProcessEventIds.PostconditionFailed,
                LogLevel.Error,
                LogStatus.Failed,
                $"Ошибка парсинга JSON от ffprobe для файла '{fileName}'",
                ex,
                "FFmpegRunner",
                result.Context.ToLogContext().WithProcess(result.ProcessId),
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["ErrorCode"] = ProcessResult.ErrorParseInvalid,
                    ["OutputName"] = fileName
                });
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<double> ProbeDurationViaFfmpegAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return 0.0;
        }

        double duration = 0.0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        ProcessExecutionContext executionContext = ProcessExecutionContext.NewOperation("ffmpeg");

        try
        {
            // Запускаем FFmpeg без декодирования данных, опрашивая заголовочную информацию файла
            string arguments = $"-hide_banner -loglevel info -i \"{filePath}\" -f null -";
            await RunProcessAsync(
                "ffmpeg",
                arguments,
                onOutputLine: null,
                onErrorLine: line =>
                {
                    if (duration <= 0)
                    {
                        double parsed = FFmpegOutputParser.ParseHeaderDuration(line, Log);
                        if (parsed > 0)
                        {
                            duration = parsed;
                        }
                    }
                },
                cts.Token,
                context: executionContext
            );
        }
        catch (Exception ex)
        {
            Log.Write(
                ProcessEventIds.PostconditionFailed,
                LogLevel.Debug,
                LogStatus.Failed,
                $"Исключение при зондировании длительности через FFmpeg для '{Path.GetFileName(filePath)}'",
                ex,
                "FFmpegRunner",
                context: executionContext.ToLogContext(),
                properties: executionContext.ToLogProperties()
                    .With("ErrorCode", ProcessResult.ErrorReadFailed)
                    .With("InputName", LogProps.FileName(filePath)));
        }

        return duration;
    }

    /// <summary>
    /// Извлечь выбранную дорожку субтитров и перекодировать в формат ASS.
    /// </summary>
    public async Task<ProcessResult> ExtractSubtitleAsync(
        string inputFile,
        int streamIndex,
        string outputPath,
        bool relative = false,
        CancellationToken cancellationToken = default,
        ProcessExecutionContext? context = null)
    {
        string mapVal = relative ? $"0:s:{streamIndex}" : $"0:{streamIndex}";
        string arguments = $"-y -hide_banner -loglevel error -i \"{inputFile}\" -map {mapVal} -c:s ass \"{outputPath}\"";

        return await RunProcessAsync(
            "ffmpeg",
            arguments,
            null,
            null,
            cancellationToken,
            context: context ?? ProcessExecutionContext.NewOperation("ffmpeg"),
            expectedArtifact: outputPath);
    }

    /// <summary>
    /// Извлечь встроенное вложение (например, файл шрифта) из видеофайла.
    /// </summary>
    public async Task<ProcessResult> ExtractAttachmentAsync(
        string inputFile,
        int streamIndex,
        string outputPath,
        CancellationToken cancellationToken = default,
        ProcessExecutionContext? context = null)
    {
        // Папка вывода должна существовать
        string? dir = Path.GetDirectoryName(outputPath);
        if (dir != null && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string arguments = $"-y -hide_banner -loglevel error -dump_attachment:{streamIndex} \"{outputPath}\" -i \"{inputFile}\" -t 0 -f null -";

        return await RunProcessAsync(
            "ffmpeg",
            arguments,
            null,
            null,
            cancellationToken,
            context: context ?? ProcessExecutionContext.NewOperation("ffmpeg"),
            expectedArtifact: outputPath);
    }

    /// <summary>
    /// Извлечь несколько вложений одним запуском FFmpeg (все флаги -dump_attachment передаются за раз).
    /// Возвращает список путей вложений, которые удалось извлечь.
    /// При сбое пакетного запуска возвращенный список может быть неполным —
    /// вызывающий код может дозапросить недостающие вложения через ExtractAttachmentAsync.
    /// </summary>
    public async Task<List<string>> ExtractAttachmentsBatchAsync(
        string inputFile,
        List<(int StreamIndex, string OutputPath)> attachments,
        CancellationToken cancellationToken = default,
        ProcessExecutionContext? context = null)
    {
        var extracted = new List<string>();
        if (attachments.Count == 0)
        {
            return extracted;
        }

        foreach (var attachment in attachments)
        {
            string? dir = Path.GetDirectoryName(attachment.OutputPath);
            if (dir != null && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }

        var argsList = BuildAttachmentDumpArguments(inputFile, attachments);

        var result = await RunProcessAsync(
            "ffmpeg",
            string.Join(" ", argsList),
            null,
            null,
            cancellationToken,
            context: context ?? ProcessExecutionContext.NewOperation("ffmpeg"),
            maxSuccessExitCode: 1);

        if (!result.IsSuccess)
        {
            return extracted;
        }

        foreach (var attachment in attachments)
        {
            if (File.Exists(attachment.OutputPath) && new FileInfo(attachment.OutputPath).Length > 0)
            {
                extracted.Add(attachment.OutputPath);
            }
        }

        return extracted;
    }

    /// <summary>
    /// Формирует аргументы командной строки для извлечения вложений одним запуском FFmpeg.
    /// Обязательный пустой вывод "-f null -" без указания выходного файла приводит к коду
    /// возврата 1 ("At least one output file must be specified"), хотя извлечение уже выполнено.
    /// </summary>
    internal static List<string> BuildAttachmentDumpArguments(
        string inputFile,
        List<(int StreamIndex, string OutputPath)> attachments)
    {
        var argsList = new List<string>
        {
            "-y",
            "-hide_banner",
            "-loglevel",
            "error"
        };

        foreach (var attachment in attachments)
        {
            argsList.Add($"-dump_attachment:{attachment.StreamIndex}");
            argsList.Add($"\"{attachment.OutputPath}\"");
        }

        argsList.Add("-i");
        argsList.Add($"\"{inputFile}\"");

        argsList.Add("-t");
        argsList.Add("0");

        argsList.Add("-f");
        argsList.Add("null");
        argsList.Add("-");

        return argsList;
    }

    /// <summary>
    /// Проверить поддержку аппаратного кодирования NVENC со стороны FFmpeg и видеокарты NVIDIA.
    /// </summary>
    public async Task<bool> CheckNvencSupportAsync()
    {
        // 1. Проверяем наличие кодировщика hevc_nvenc в FFmpeg
        bool ffmpegSupports = false;
        var result = await RunProcessAsync(
            "ffmpeg",
            "-encoders",
            onOutputLine: line =>
            {
                if (line.Contains("hevc_nvenc", StringComparison.OrdinalIgnoreCase))
                {
                    ffmpegSupports = true;
                }
            },
            onErrorLine: null,
            CancellationToken.None
        );

        if (!result.IsSuccess || !ffmpegSupports)
        {
            Log.Write(
                ProcessEventIds.PostconditionFailed,
                LogLevel.Warning,
                LogStatus.Skipped,
                "Аппаратный энкодер 'hevc_nvenc' не поддерживается сборкой FFmpeg",
                null,
                "FFmpegRunner",
                result.Context.ToLogContext().WithProcess(result.ProcessId),
                result.ToLogProperties());
            return false;
        }

        // 2. Проверяем наличие видеокарты NVIDIA через nvidia-smi
        string winPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");
        string smiName = File.Exists(winPath) ? winPath : "nvidia-smi";

        bool hasNvidia = false;
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = smiName,
                    Arguments = "-L",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = ProcessOutputPolicy.ResolveEncoding()
                }
            };
            process.Start();
            string output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            hasNvidia = process.ExitCode == 0 && output.Contains("GPU", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Write(
                ProcessEventIds.StartFailed,
                LogLevel.Debug,
                LogStatus.Skipped,
                "Утилита nvidia-smi недоступна, проверка NVENC невозможна",
                ex,
                "FFmpegRunner",
                context: ProcessExecutionContext.NewOperation("nvidia-smi").ToLogContext());
        }

        if (hasNvidia)
        {
            Log.Write(
                "encoder.nvenc.detected",
                LogLevel.Info,
                LogStatus.Succeeded,
                "Обнаружена видеокарта NVIDIA с поддержкой NVENC",
                source: SourceName,
                properties: LogProps
                    .Create("Tool", "nvidia-smi")
                    .With("Codec", "h264_nvenc")
                    .With("Verified", true));
            return true;
        }

        Log.Write("encoder.nvenc.not_detected", LogLevel.Warning, LogStatus.Skipped, "Видеокарта NVIDIA не обнаружена через утилиту nvidia-smi, будет использовано программное кодирование", source: SourceName, properties: LogProps.Create("Tool", "nvidia-smi").With("Codec", "h264_nvenc").With("Verified", false).With("ErrorCode", "NVENC_NOT_AVAILABLE"));
        return false;
    }

    /// <summary>
    /// Проверить поддержку параметра Temporal AQ для NVENC через вывод помощи FFmpeg.
    /// </summary>
    public async Task<bool> CheckNvencTemporalAqSupportAsync()
    {
        bool supported = false;
        var result = await RunProcessAsync(
            "ffmpeg",
            "-h encoder=hevc_nvenc",
            onOutputLine: line =>
            {
                if (line.Contains("-temporal-aq", StringComparison.OrdinalIgnoreCase))
                {
                    supported = true;
                }
            },
            onErrorLine: null,
            CancellationToken.None
        );

        return result.IsSuccess && supported;
    }

    /// <summary>
    /// Выполнить зондирование и автоматическое определение обрезки черных полос (cropdetect).
    /// </summary>
    public async Task<string?> DetectCropAsync(
        string filePath,
        double skipSeconds = 0,
        int probeFrames = 25,
        double limit = 0.0941176,
        int round = 16,
        int skip = 2,
        int reset = 0,
        string mode = "black",
        CancellationToken cancellationToken = default)
    {
        int safeSkip = skip;
        if (safeSkip >= probeFrames)
        {
            safeSkip = Math.Max(0, Math.Min(2, probeFrames - 1));
        }

        string limitStr = limit.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
        string cropdetectFilter = $"cropdetect=limit={limitStr}:round={round}:skip={safeSkip}:reset={reset}:mode={mode}";

        var argsList = new List<string>
        {
            "-hide_banner",
            "-nostats"
        };

        if (skipSeconds > 0)
        {
            argsList.Add("-ss");
            argsList.Add(skipSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        }

        argsList.AddRange(new[]
        {
            "-i", $"\"{filePath}\"",
            "-vframes", probeFrames.ToString(),
            "-vf", cropdetectFilter,
            "-f", "null",
            "-"
        });

        string arguments = string.Join(" ", argsList);
        string? lastDetectedCrop = null;
        var cropRegex = new System.Text.RegularExpressions.Regex(@"crop=([0-9]+:[0-9]+:[0-9]+:[0-9]+)", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var result = await RunProcessAsync(
            "ffmpeg",
            arguments,
            onOutputLine: line =>
            {
                var match = cropRegex.Match(line);
                if (match.Success)
                {
                    lastDetectedCrop = match.Groups[1].Value;
                }
            },
            onErrorLine: line =>
            {
                var match = cropRegex.Match(line);
                if (match.Success)
                {
                    lastDetectedCrop = match.Groups[1].Value;
                }
            },
            cancellationToken
        );

        if (!result.IsSuccess && lastDetectedCrop == null)
        {
            Log.Write(
                ProcessEventIds.PostconditionFailed,
                LogLevel.Debug,
                LogStatus.Skipped,
                $"cropdetect не смог определить параметры обрезки для '{Path.GetFileName(filePath)}'",
                null,
                "FFmpegRunner",
                result.Context.ToLogContext().WithProcess(result.ProcessId),
                result.ToLogProperties());
            return null;
        }

        return lastDetectedCrop;
    }
}
