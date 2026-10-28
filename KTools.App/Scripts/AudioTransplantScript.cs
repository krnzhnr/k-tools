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
        // eac3to не поддерживает контейнеры MP4/M4A и кодеки Opus/Vorbis,
        // поэтому для неподдерживаемых кодеков сдвиг выполняется через FFmpeg с перекодированием.
        string rawExt = "ac3";
        string sourceCodec = string.Empty;
        try
        {
            var sourceStructure = await _mediaProbeService.ProbeAsync(sourceFilePath);
            var audioTracks = sourceStructure?.GetAudioTracks();
            if (audioTracks != null && sourceTrackIndex < audioTracks.Count)
            {
                string codec = audioTracks[sourceTrackIndex].Codec.ToLowerInvariant();
                sourceCodec = codec;
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

        // eac3to не декодирует Opus/Vorbis и ряд других кодеков — для них сдвиг выполняется через FFmpeg.
        bool useEac3to = IsEac3toSupportedCodec(sourceCodec);
        string shiftedExt = useEac3to ? rawExt : ResolveShiftedExtension(ResolveFfmpegAudioEncoder(sourceCodec, rawExt));

        string tempRawAudioPath = Path.Combine(Path.GetTempPath(), $"transplant_raw_{Guid.NewGuid():N}.{rawExt}");
        string tempShiftedPath = Path.Combine(Path.GetTempPath(), $"transplant_{Guid.NewGuid():N}.{shiftedExt}");
        string? finalOutputPath = null;

        try
        {
            if (useEac3to)
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
            }

            int roundedOffsetMs = (int)Math.Round(actualOffsetMs);
            string shiftArg = roundedOffsetMs >= 0 ? $"+{roundedOffsetMs}ms" : $"{roundedOffsetMs}ms";

            if (!useEac3to)
            {
                progressCallback(fileIndex, totalCount, "Сдвиг аудио через FFmpeg (перекодирование)...", 45.0);

                _logService.Write(
                    "script.audio_transplant.eac3to_unsupported",
                    LogLevel.Info,
                    LogStatus.Running,
                    $"Кодек '{LogRedactor.CompactSafeToken(string.IsNullOrEmpty(sourceCodec) ? rawExt : sourceCodec)}' не поддерживается eac3to, сдвиг выполняется через FFmpeg с перекодированием",
                    source: Name,
                    properties: LogProps
                        .Create("Tool", "ffmpeg")
                        .With("Codec", LogRedactor.CompactSafeToken(string.IsNullOrEmpty(sourceCodec) ? rawExt : sourceCodec))
                        .With("DurationMs", userShiftMs));

                bool fallbackOk = await ShiftAudioViaFfmpegAsync(sourceFilePath, sourceTrackIndex, tempShiftedPath, roundedOffsetMs, sourceCodec, rawExt);
                if (!fallbackOk || !File.Exists(tempShiftedPath))
                {
                    throw new InvalidOperationException("Не удалось выполнить сдвиг пересаживаемой аудиодорожки через FFmpeg.");
                }
            }
            else
            {
                progressCallback(fileIndex, totalCount, "Прямоточный физический сдвиг аудио (eac3to Bitstream)...", 45.0);

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
                    _logService.Write(
                        "script.audio_transplant.eac3to_fallback",
                        LogLevel.Warning,
                        LogStatus.Running,
                        "Прямоточный сдвиг через eac3to не выполнен, применяется резервный сдвиг через FFmpeg с перекодированием",
                        source: Name,
                        properties: LogProps
                            .Create("Tool", "ffmpeg")
                            .With("Codec", LogRedactor.CompactSafeToken(string.IsNullOrEmpty(sourceCodec) ? rawExt : sourceCodec))
                            .With("ErrorCode", eac3Result.ErrorCode ?? "EAC3TO_FAILED"));

                    bool fallbackOk = await ShiftAudioViaFfmpegAsync(sourceFilePath, sourceTrackIndex, tempShiftedPath, roundedOffsetMs, sourceCodec, rawExt);
                    if (!fallbackOk || !File.Exists(tempShiftedPath))
                    {
                        throw new InvalidOperationException("Не удалось выполнить сдвиг пересаживаемой аудиодорожки через eac3to и резервный сдвиг через FFmpeg.");
                    }
                }
            }

            progressCallback(fileIndex, totalCount, "Мультиплексирование в MKV (mkvmerge)...", 70.0);

            // 4. Формирование итогового файла MKV
            string targetDir = string.IsNullOrWhiteSpace(outputPath) ? Path.GetDirectoryName(filePath)! : outputPath;
            string targetFileName = $"{Path.GetFileNameWithoutExtension(filePath)}.mkv";
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

    /// <summary>
    /// Проверяет, способен ли eac3to обработать аудиокодек без перекодирования.
    /// Поддержаны только кодеки из официальной документации eac3to (AAC, AC3/E-AC3,
    /// DTS, TrueHD/MLP, FLAC, MPEG-аудио, PCM); Opus, Vorbis и прочие отсутствуют.
    /// </summary>
    /// <param name="codec">Название кодека из зонда (в любом регистре).</param>
    /// <returns>Признак поддержки кодека утилитой eac3to.</returns>
    public static bool IsEac3toSupportedCodec(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec))
        {
            return false;
        }

        string normalized = codec.Trim().ToLowerInvariant()
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("/", string.Empty, StringComparison.Ordinal);

        if (normalized.Contains("opus", StringComparison.Ordinal)
            || normalized.Contains("vorbis", StringComparison.Ordinal)
            || normalized.Contains("wavpack", StringComparison.Ordinal)
            || normalized.Contains("alac", StringComparison.Ordinal)
            || normalized.Contains("ogg", StringComparison.Ordinal))
        {
            return false;
        }

        if (normalized.Contains("aac", StringComparison.Ordinal))
        {
            return true;
        }

        if (normalized.Contains("eac3", StringComparison.Ordinal)
            || normalized.Contains("ec3", StringComparison.Ordinal)
            || normalized.Contains("ddp", StringComparison.Ordinal))
        {
            return true;
        }

        if (normalized.Contains("ac3", StringComparison.Ordinal)
            || normalized.Contains("a52", StringComparison.Ordinal))
        {
            return true;
        }

        if (normalized.Contains("dts", StringComparison.Ordinal))
        {
            return true;
        }

        if (normalized.Contains("truehd", StringComparison.Ordinal)
            || normalized.Contains("thd", StringComparison.Ordinal)
            || normalized.Contains("mlp", StringComparison.Ordinal))
        {
            return true;
        }

        if (normalized.Contains("flac", StringComparison.Ordinal))
        {
            return true;
        }

        if (normalized is "mp1" or "mp2" or "mp3"
            || normalized.Contains("mpegaudio", StringComparison.Ordinal)
            || normalized.Contains("layer", StringComparison.Ordinal))
        {
            return true;
        }

        if (normalized.Contains("pcm", StringComparison.Ordinal)
            || normalized.Contains("wav", StringComparison.Ordinal)
            || normalized.Contains("w64", StringComparison.Ordinal)
            || normalized.Contains("rf64", StringComparison.Ordinal)
            || normalized.Contains("lpcm", StringComparison.Ordinal)
            || normalized == "raw")
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Подбирает кодировщик FFmpeg для резервного сдвига с перекодированием.
    /// Для неизвестных кодеков возвращается FLAC как безопасный формат без потерь,
    /// совместимый с итоговым контейнером MKV.
    /// </summary>
    /// <param name="codec">Название исходного кодека из зонда.</param>
    /// <param name="rawExt">Сырое расширение, определённое при зондировании.</param>
    /// <returns>Имя кодировщика FFmpeg.</returns>
    public static string ResolveFfmpegAudioEncoder(string? codec, string? rawExt)
    {
        string normalized = (codec ?? string.Empty).Trim().ToLowerInvariant()
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("/", string.Empty, StringComparison.Ordinal);
        string ext = (rawExt ?? string.Empty).Trim().ToLowerInvariant().TrimStart('.');

        if (normalized.Contains("opus", StringComparison.Ordinal) || ext == "opus")
        {
            return "libopus";
        }

        if (normalized.Contains("vorbis", StringComparison.Ordinal) || ext is "ogg" or "oga")
        {
            return "libvorbis";
        }

        if (normalized.Contains("mp3", StringComparison.Ordinal)
            || normalized.Contains("mpegaudio", StringComparison.Ordinal)
            || ext == "mp3")
        {
            return "libmp3lame";
        }

        if (normalized.Contains("eac3", StringComparison.Ordinal)
            || normalized.Contains("ec3", StringComparison.Ordinal)
            || ext == "eac3")
        {
            return "eac3";
        }

        if (normalized.Contains("ac3", StringComparison.Ordinal)
            || normalized.Contains("a52", StringComparison.Ordinal)
            || ext == "ac3")
        {
            return "ac3";
        }

        if (normalized.Contains("aac", StringComparison.Ordinal) || ext is "aac" or "m4a")
        {
            return "aac";
        }

        if (normalized.Contains("flac", StringComparison.Ordinal) || ext == "flac")
        {
            return "flac";
        }

        if (normalized.Contains("alac", StringComparison.Ordinal))
        {
            return "alac";
        }

        if (normalized.Contains("dts", StringComparison.Ordinal) || ext == "dts")
        {
            return "dca";
        }

        if (normalized.Contains("truehd", StringComparison.Ordinal)
            || normalized.Contains("thd", StringComparison.Ordinal)
            || normalized.Contains("mlp", StringComparison.Ordinal)
            || ext == "thd")
        {
            return "truehd";
        }

        if (normalized.Contains("pcm", StringComparison.Ordinal)
            || normalized.Contains("wav", StringComparison.Ordinal)
            || ext == "wav")
        {
            return "pcm_s16le";
        }

        return "flac";
    }

    /// <summary>
    /// Возвращает расширение временного файла (без точки) для заданного кодировщика FFmpeg.
    /// </summary>
    /// <param name="encoder">Имя кодировщика FFmpeg.</param>
    /// <returns>Расширение файла без точки.</returns>
    public static string ResolveShiftedExtension(string encoder)
    {
        return encoder switch
        {
            "libopus" => "opus",
            "libvorbis" => "ogg",
            "libmp3lame" => "mp3",
            "aac" => "aac",
            "ac3" => "ac3",
            "eac3" => "eac3",
            "alac" => "m4a",
            "dca" => "dts",
            "truehd" => "thd",
            "pcm_s16le" => "wav",
            _ => "flac",
        };
    }

    /// <summary>
    /// Выполняет физический сдвиг аудиодорожки через FFmpeg с перекодированием.
    /// Положительный сдвиг добавляет тишину в начало (adelay), отрицательный
    /// обрезает начало (atrim); нулевой сдвиг копируется без перекодирования.
    /// </summary>
    /// <param name="inputPath">Путь к исходному медиафайлу.</param>
    /// <param name="audioIndex">Порядковый индекс аудиодорожки среди аудио (0:a:N).</param>
    /// <param name="outputPath">Путь к результирующему сдвинутому файлу.</param>
    /// <param name="roundedOffsetMs">Округлённый сдвиг в миллисекундах.</param>
    /// <param name="sourceCodec">Исходный кодек для подбора кодировщика.</param>
    /// <param name="rawExt">Сырое расширение для подбора кодировщика.</param>
    /// <returns>Признак успешного создания выходного файла.</returns>
    private async Task<bool> ShiftAudioViaFfmpegAsync(
        string inputPath,
        int audioIndex,
        string outputPath,
        int roundedOffsetMs,
        string sourceCodec,
        string rawExt)
    {
        var shiftArgs = new List<string>
        {
            "-map", $"0:a:{audioIndex.ToString(CultureInfo.InvariantCulture)}"
        };

        if (roundedOffsetMs == 0)
        {
            shiftArgs.Add("-c:a");
            shiftArgs.Add("copy");
        }
        else
        {
            string encoder = ResolveFfmpegAudioEncoder(sourceCodec, rawExt);

            if (roundedOffsetMs > 0)
            {
                shiftArgs.Add("-af");
                shiftArgs.Add($"adelay={roundedOffsetMs.ToString(CultureInfo.InvariantCulture)}:all=1");
            }
            else
            {
                double shiftSec = Math.Abs(roundedOffsetMs) / 1000.0;
                shiftArgs.Add("-af");
                shiftArgs.Add($"atrim=start={shiftSec.ToString("F3", CultureInfo.InvariantCulture)},asetpts=PTS-STARTPTS");
            }

            shiftArgs.Add("-c:a");
            shiftArgs.Add(encoder);

            bool isLossless = encoder is "flac" or "pcm_s16le" or "alac" or "truehd";
            if (!isLossless)
            {
                string? bitrate = await TryResolveAudioBitrateAsync(inputPath, audioIndex);
                if (!string.IsNullOrWhiteSpace(bitrate))
                {
                    shiftArgs.Add("-b:a");
                    shiftArgs.Add(bitrate);
                }
            }
        }

        ProcessResult shiftResult = await _ffmpegRunner.RunAsync(
            inputPath: inputPath,
            outputPath: outputPath,
            extraArgs: shiftArgs,
            overwrite: true,
            cancellationToken: CancellationToken);

        return shiftResult.IsSuccess && File.Exists(outputPath);
    }

    /// <summary>
    /// Извлекает битрейт выбранной аудиодорожки из ffprobe для сохранения
    /// качества при резервном перекодировании (формат вида "192k").
    /// </summary>
    /// <param name="inputPath">Путь к исходному медиафайлу.</param>
    /// <param name="audioIndex">Порядковый индекс аудиодорожки среди аудио.</param>
    /// <returns>Битрейт для ключа -b:a либо null, если определить не удалось.</returns>
    private async Task<string?> TryResolveAudioBitrateAsync(string inputPath, int audioIndex)
    {
        try
        {
            using JsonDocument? probeDoc = await _ffmpegRunner.GetVideoInfoAsync(inputPath);
            if (probeDoc == null)
            {
                return null;
            }

            JsonElement root = probeDoc.RootElement;
            if (!root.TryGetProperty("streams", out JsonElement streams)
                || streams.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            int seenAudio = -1;
            foreach (JsonElement stream in streams.EnumerateArray())
            {
                if (!stream.TryGetProperty("codec_type", out JsonElement typeProp)
                    || !string.Equals(typeProp.GetString(), "audio", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                seenAudio++;
                if (seenAudio != audioIndex)
                {
                    continue;
                }

                if (!stream.TryGetProperty("bit_rate", out JsonElement bitRateProp))
                {
                    return null;
                }

                string? raw = bitRateProp.ValueKind == JsonValueKind.Number
                    ? bitRateProp.GetRawText()
                    : bitRateProp.GetString();

                if (double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out double bps) && bps > 8000)
                {
                    int kbps = (int)Math.Round(bps / 1000.0);
                    return kbps.ToString(CultureInfo.InvariantCulture) + "k";
                }

                return null;
            }
        }
        catch
        {
        }

        return null;
    }
}
