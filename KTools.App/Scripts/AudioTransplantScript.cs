// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Infrastructure;
using KTools_App.Models;
using KTools_App.Services.Contracts;
using KTools_App.UI;

using Microsoft.UI.Dispatching;

using ExecutionContext = KTools_App.Models.ExecutionContext;

namespace KTools_App.Scripts;

/// <summary>
/// Скрипт для пересадки аудиодорожки из внешнего медиафайла в целевое видео с визуальной синхронизацией
/// и точной математической компенсацией задержки AAC кодера (-21.33 мс).
/// Все комментарии, логи и документация выполнены на русском языке в соответствии с регламентом.
/// </summary>
public sealed class AudioTransplantScript : AbstractScript
{
    private readonly IFFmpegRunner _ffmpegRunner;
    private readonly IMkvmergeRunner _mkvmergeRunner;
    private readonly IEac3toRunner _eac3toRunner;
    private readonly IAudioWaveformService _waveformService;
    private readonly IMediaProbeService _mediaProbeService;

    private const double AacPrimingDelayMs = 21.333333333333332; // 1024 / 48000 * 1000

    /// <summary>Сессионный путь к исходному аудио (в памяти до перезапуска приложения).</summary>
    public string SourceFilePath { get; set; } = string.Empty;

    /// <summary>Сессионный путь к целевому видео (в памяти до перезапуска приложения).</summary>
    public string DestFilePath { get; set; } = string.Empty;

    /// <summary>Сессионный путь к файлу субтитров (в памяти до перезапуска приложения).</summary>
    public string SubtitlesFilePath { get; set; } = string.Empty;

    /// <summary>Сессионный индекс дорожки источника.</summary>
    public int SourceTrackIndex { get; set; } = 0;

    /// <summary>Сессионный индекс целевой дорожки.</summary>
    public int DestTrackIndex { get; set; } = 0;

    /// <summary>Сессионный сдвиг в миллисекундах.</summary>
    public int ShiftMs { get; set; } = 0;

    public AudioTransplantScript(
        ILogService logService,
        ISettingsManager settingsManager,
        IPathManager pathManager,
        IFFmpegRunner ffmpegRunner,
        IMkvmergeRunner mkvmergeRunner,
        IEac3toRunner eac3toRunner,
        IAudioWaveformService waveformService,
        IMediaProbeService mediaProbeService)
        : base(logService, settingsManager, pathManager)
    {
        _ffmpegRunner = ffmpegRunner ?? throw new ArgumentNullException(nameof(ffmpegRunner));
        _mkvmergeRunner = mkvmergeRunner ?? throw new ArgumentNullException(nameof(mkvmergeRunner));
        _eac3toRunner = eac3toRunner ?? throw new ArgumentNullException(nameof(eac3toRunner));
        _waveformService = waveformService ?? throw new ArgumentNullException(nameof(waveformService));
        _mediaProbeService = mediaProbeService ?? throw new ArgumentNullException(nameof(mediaProbeService));
    }

    /// <inheritdoc />
    public override string Name => AppConstants.ScriptMetadata.AudioTransplantName;

    /// <inheritdoc />
    public override string Description => AppConstants.ScriptMetadata.AudioTransplantDesc;

    /// <inheritdoc />
    public override string Category => AppConstants.ScriptCategory.Audio;

    /// <inheritdoc />
    public override string IconName => AppConstants.ScriptIcons.AudioTransplant;

    /// <inheritdoc />
    public override string[] FileExtensions => AppConstants.VideoContainers.ToArray();

    /// <inheritdoc />
    public override string[] RequiredDependencies => new[] { "ffmpeg", "mkvtoolnix", "eac3to" };

    /// <inheritdoc />
    public override bool UseCustomWidget => false;

    /// <inheritdoc />
    public override bool SupportsParallel => false; // Только последовательно для диалогов UI

    /// <inheritdoc />
    public override List<SettingField> SettingsSchema => new();

    /// <inheritdoc />
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
        string destFileName = Path.GetFileName(filePath);

        _logService.Write(
            "script.audio_transplant.started",
            LogLevel.Debug,
            LogStatus.Running,
            $"Начата пересадка аудиодорожки в файл '{destFileName}'",
            source: Name,
            properties: LogProps.Create("OutputName", destFileName));

        string sourceFilePath = GetSettingValue(settings, "SourceFile", SourceFilePath);
        if (string.IsNullOrWhiteSpace(sourceFilePath)) sourceFilePath = SourceFilePath;

        if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
        {
            results.Add("Файл-источник пересаживаемого аудио не найден.");
            return ExecutionResult.Failed(
                context,
                results,
                errorCode: "source-missing",
                retryable: false);
        }

        int sourceTrackIndex = GetSettingValue(settings, "SourceTrackIndex", SourceTrackIndex);
        int destTrackIndex = GetSettingValue(settings, "DestTrackIndex", DestTrackIndex);
        int userShiftMs = GetSettingValue(settings, "ShiftMs", ShiftMs);

        // 2. Рассчитываем итоговый физический сдвиг с учетом вычитания задержки AAC (21.33 мс)
        double actualOffsetMs = userShiftMs - AacPrimingDelayMs;
        _logService.Write(
            "script.audio_transplant.offset_applied",
            LogLevel.Debug,
            LogStatus.Running,
            $"Применён сдвиг аудио: запрошено {userShiftMs:0.###} мс, компенсация кодировки AAC {AacPrimingDelayMs:0.###} мс, фактически {actualOffsetMs:0.###} мс",
            source: Name,
            properties: LogProps
                .Create("DurationMs", actualOffsetMs)
                .With("Codec", "aac"));

        // 3. Предварительное прямоточное извлечение сырого аудиопотока через FFmpeg
        // eac3to не поддерживает контейнеры MP4/M4A и требует сырой элементарный поток (AAC/AC3/DTS и т.д.)
        string rawExt = "ac3";
        try
        {
            var sourceStructure = await _mediaProbeService.ProbeAsync(sourceFilePath);
            var audioTracks = sourceStructure?.GetAudioTracks();
            if (audioTracks != null && sourceTrackIndex < audioTracks.Count)
            {
                string codec = audioTracks[sourceTrackIndex].Codec.ToLowerInvariant();
                rawExt = codec switch
                {
                    "aac" => "aac",
                    "ac3" => "ac3",
                    "eac3" => "eac3",
                    "dts" => "dts",
                    "dtshd" => "dts",
                    "truehd" => "thd",
                    "flac" => "flac",
                    "mp3" => "mp3",
                    "opus" => "opus",
                    _ => Path.GetExtension(sourceFilePath).TrimStart('.').ToLowerInvariant()
                };
            }
            else
            {
                string ext = Path.GetExtension(sourceFilePath).TrimStart('.').ToLowerInvariant();
                rawExt = ext == "m4a" ? "aac" : (string.IsNullOrEmpty(ext) ? "ac3" : ext);
            }
        }
        catch (Exception ex)
        {
            _logService.Write(
                "script.audio_transplant.probe_fallback",
                LogLevel.Warning,
                LogStatus.PartiallySucceeded,
                "Аудиопоток источника заранее не определён, используется определение по расширению файла",
                ex,
                Name,
                properties: LogProps
                    .Create("InputName", LogProps.FileName(filePath))
                    .With("ErrorCode", "SOURCE_PROBE_FALLBACK")
                    .With("Retryable", false));
            string ext = Path.GetExtension(sourceFilePath).TrimStart('.').ToLowerInvariant();
            rawExt = ext == "m4a" ? "aac" : (string.IsNullOrEmpty(ext) ? "ac3" : ext);
        }

        string tempRawAudioPath = Path.Combine(Path.GetTempPath(), $"transplant_raw_{Guid.NewGuid():N}.{rawExt}");
        string tempShiftedPath = Path.Combine(Path.GetTempPath(), $"transplant_{Guid.NewGuid():N}.{rawExt}");
        string? finalOutputPath = null;

        try
        {
            progressCallback(fileIndex, totalCount, "Извлечение сырого аудиопотока (FFmpeg stream copy)...", 25.0);
            _logService.Write(
                "script.audio_transplant.raw_extract_started",
                LogLevel.Debug,
                LogStatus.Running,
                $"Прямоточное извлечение аудиодорожки a:{sourceTrackIndex} в формат {LogRedactor.CompactSafeToken(rawExt)}",
                source: Name,
                properties: LogProps
                    .Create("Channel", "a:" + sourceTrackIndex.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .With("Extension", LogRedactor.CompactSafeToken(rawExt)));

            var ffmpegDemuxArgs = new List<string>
            {
                "-map", $"0:a:{sourceTrackIndex}",
                "-c:a", "copy"
            };

            ProcessResult demuxResult = await _ffmpegRunner.RunAsync(
                inputPath: sourceFilePath,
                outputPath: tempRawAudioPath,
                extraArgs: ffmpegDemuxArgs,
                overwrite: true,
                cancellationToken: CancellationToken);

            if (!demuxResult.IsSuccess || !File.Exists(tempRawAudioPath))
            {
                throw new InvalidOperationException($"Не удалось прямоточно извлечь сырой аудиопоток дорожки {sourceTrackIndex} через FFmpeg.");
            }

            progressCallback(fileIndex, totalCount, "Прямоточный физический сдвиг аудио (eac3to Bitstream)...", 45.0);

            int roundedOffsetMs = (int)Math.Round(actualOffsetMs);
            string shiftArg = roundedOffsetMs >= 0 ? $"+{roundedOffsetMs}ms" : $"{roundedOffsetMs}ms";

            _logService.Write(
                "script.audio_transplant.eac3to_started",
                LogLevel.Debug,
                LogStatus.Running,
                $"Запущен сдвиг аудиодорожки в eac3to со сдвигом {shiftArg} мс",
                source: Name,
                properties: LogProps
                    .Create("Tool", "eac3to")
                    .With("FileName", LogProps.FileName(tempRawAudioPath))
                    .With("DurationMs", userShiftMs));

            // Передаем в eac3to извлеченный элементарный поток
            var eac3toArgs = new List<string>
            {
                $"\"{tempRawAudioPath}\"",
                $"\"{tempShiftedPath}\"",
                shiftArg,
                "-silence",
                "-progressnumbers",
                "-log=nul"
            };

            ProcessResult eac3Result = await _eac3toRunner.RunAsync(
                eac3toArgs,
                cancellationToken: CancellationToken,
                expectedArtifact: tempShiftedPath);

            if (!eac3Result.IsSuccess || !File.Exists(tempShiftedPath))
            {
                throw new InvalidOperationException("Не удалось выполнить прямоточный физический сдвиг пересаживаемой аудиодорожки через eac3to.");
            }

            progressCallback(fileIndex, totalCount, "Мультиплексирование в MKV (mkvmerge)...", 70.0);

            // 4. Формирование итогового файла MKV
            string targetDir = string.IsNullOrWhiteSpace(outputPath) ? Path.GetDirectoryName(filePath)! : outputPath;
            string targetFileName = $"[DubSwap] {Path.GetFileNameWithoutExtension(filePath)}.mkv";
            finalOutputPath = Path.Combine(targetDir, targetFileName);

            var mkvInputs = new List<MkvInputSource>
            {
                new MkvInputSource(tempShiftedPath, new List<string>
                {
                    "--language", "0:rus",
                    "--default-track-flag", "0:yes",
                    "--forced-display-flag", "0:yes"
                })
            };

            // Вшивание внешних субтитров (если указаны)
            string subtitlesFile = GetSettingValue(settings, "SubtitlesFile", SubtitlesFilePath);
            if (string.IsNullOrWhiteSpace(subtitlesFile)) subtitlesFile = SubtitlesFilePath;
            if (!string.IsNullOrWhiteSpace(subtitlesFile) && File.Exists(subtitlesFile))
            {
                mkvInputs.Add(new MkvInputSource(subtitlesFile, new List<string>
                {
                    "--track-name", "0:[Надписи]",
                    "--language", "0:rus",
                    "--default-track-flag", "0:yes",
                    "--forced-display-flag", "0:yes",
                    "--sub-charset", "0:utf-8"
                }));
            }

            // Готовим аргументы снятия флагов по умолчанию с оригинальных аудио и субтитров
            var destExtraArgs = new List<string>();
            try
            {
                var mediaStructure = await _mediaProbeService.ProbeAsync(filePath);
                if (mediaStructure != null)
                {
                    foreach (var track in mediaStructure.Tracks)
                    {
                        if (track.TrackType.Equals("audio", StringComparison.OrdinalIgnoreCase) ||
                            track.TrackType.Equals("subtitles", StringComparison.OrdinalIgnoreCase))
                        {
                            destExtraArgs.Add("--default-track-flag");
                            destExtraArgs.Add($"{track.TrackId}:no");
                            destExtraArgs.Add("--forced-display-flag");
                            destExtraArgs.Add($"{track.TrackId}:no");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logService.Write(
                "script.audio_transplant.flags_probe_failed",
                LogLevel.Debug,
                LogStatus.Skipped,
                "Флаги целевого видео автоматически не проанализированы, применены значения по умолчанию",
                ex,
                Name,
                properties: LogProps
                    .Create("OutputName", destFileName)
                    .With("ErrorCode", "TARGET_FLAGS_PROBE_FAILED"));
            }

            mkvInputs.Add(new MkvInputSource(filePath, destExtraArgs));

            ProcessResult mkvResult = await _mkvmergeRunner.RunAsync(
                finalOutputPath,
                mkvInputs,
                cancellationToken: CancellationToken);

            if (mkvResult.IsSuccess && File.Exists(finalOutputPath))
            {
                _logService.Write(
                "script.audio_transplant.completed",
                LogLevel.Info,
                LogStatus.Succeeded,
                $"Пересадка аудио завершена, выходной файл: '{LogProps.FileName(finalOutputPath)}'",
                source: Name,
                properties: LogProps
                    .Create("OutputName", LogProps.FileName(finalOutputPath))
                    .With("ArtifactVerified", true));
                results.Add(finalOutputPath);
                progressCallback(fileIndex, totalCount, "Завершено успешно.", 100.0);
            }
            else
            {
                throw new InvalidOperationException("mkvmerge вернул ошибку при сборке итогового MKV файла.");
            }

            return ExecutionResult.Succeeded(
                context,
                results,
                outputFile: finalOutputPath,
                outputExists: finalOutputPath is not null && File.Exists(finalOutputPath));
        }
        catch (Exception ex)
        {
            _logService.Write(
                "script.audio_transplant.failed",
                LogLevel.Error,
                LogStatus.Failed,
                $"Пересадка аудио для '{destFileName}' не выполнена",
                ex,
                Name,
                properties: LogProps
                    .Create("OutputName", destFileName)
                    .With("ErrorCode", "AUDIO_TRANSPLANT_FAILED")
                    .With("Retryable", true));
            return ExecutionResult.FromException(
                context,
                ex,
                new[] { "Ошибка во время пересадки аудио." },
                errorCode: "transplant-failed",
                outputFile: finalOutputPath,
                outputExists: finalOutputPath is not null && File.Exists(finalOutputPath),
                cleanupState: CleanupState.Completed);
        }
        finally
        {
            // Очистка временных промежуточных и сдвинутых аудиофайлов
            if (File.Exists(tempRawAudioPath))
            {
                try
                {
                    File.Delete(tempRawAudioPath);
                }
                catch { }
            }

            if (File.Exists(tempShiftedPath))
            {
                try
                {
                    File.Delete(tempShiftedPath);
                }
                catch { }
            }
        }
    }
}
