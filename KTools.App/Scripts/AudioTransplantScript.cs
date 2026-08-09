// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KTools_App.Core;
using KTools_App.Infrastructure;
using KTools_App.Services.Contracts;
using KTools_App.UI;
using Microsoft.UI.Dispatching;

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
    public override async Task<List<string>> ExecuteSingleAsync(
        string filePath,
        Dictionary<string, object> settings,
        string? outputPath,
        ScriptProgressCallback progressCallback,
        int fileIndex,
        int totalCount)
    {
        var results = new List<string>();
        string destFileName = Path.GetFileName(filePath);

        _logService.Info($"Начало пересадки аудиодорожки в файл: '{destFileName}'", "AudioTransplantScript");

        string sourceFilePath = GetSettingValue(settings, "SourceFile", SourceFilePath);
        if (string.IsNullOrWhiteSpace(sourceFilePath)) sourceFilePath = SourceFilePath;

        if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
        {
            throw new FileNotFoundException($"Файл-источник пересаживаемого аудио не найден: '{sourceFilePath}'");
        }

        int sourceTrackIndex = GetSettingValue(settings, "SourceTrackIndex", SourceTrackIndex);
        int destTrackIndex = GetSettingValue(settings, "DestTrackIndex", DestTrackIndex);
        int userShiftMs = GetSettingValue(settings, "ShiftMs", ShiftMs);

        // 2. Рассчитываем итоговый физический сдвиг с учетом вычитания задержки AAC (21.33 мс)
        double actualOffsetMs = userShiftMs - AacPrimingDelayMs;
        _logService.Info($"Пользовательский сдвиг: {userShiftMs} мс. Математическая компенсация AAC (-21.33 мс). Фактический сдвиг: {actualOffsetMs:F2} мс.", "AudioTransplantScript");

        // 3. Извлечение и прямоточный физический сдвиг аудиопотока через eac3to
        string sourceExt = Path.GetExtension(sourceFilePath).TrimStart('.');
        if (string.IsNullOrEmpty(sourceExt)) sourceExt = "ac3";

        string tempShiftedPath = Path.Combine(Path.GetTempPath(), $"transplant_{Guid.NewGuid():N}.{sourceExt}");

        try
        {
            progressCallback(fileIndex, totalCount, "Извлечение и прямоточный сдвиг аудио (eac3to Bitstream)...", 30.0);

            int roundedOffsetMs = (int)Math.Round(actualOffsetMs);
            string shiftArg = roundedOffsetMs >= 0 ? $"+{roundedOffsetMs}ms" : $"{roundedOffsetMs}ms";

            // eac3to дорожка 1-indexed (если исходный файл моно-дорожка, иначе добавляем 1 к 0-indexed индексу)
            int eac3TrackNum = sourceTrackIndex + 1;
            var eac3toArgs = new List<string>
            {
                $"\"{sourceFilePath}\"",
                $"{eac3TrackNum}:\"{tempShiftedPath}\"",
                shiftArg,
                "-silence",
                "-progressnumbers",
                "-log=nul"
            };

            bool eac3Success = await _eac3toRunner.RunAsync(eac3toArgs);

            // Резервный вариант, если eac3to не смог распарсить контейнер: используем FFmpeg для извлечения сырого потока с прямоточным копированием
            if (!eac3Success || !File.Exists(tempShiftedPath))
            {
                _logService.Warn("Прямой сдвиг трека через eac3to не завершился успешно. Запуск резервной обработки через FFmpeg...", "AudioTransplantScript");

                string tempRawPath = Path.Combine(Path.GetTempPath(), $"transplant_raw_{Guid.NewGuid():N}.{sourceExt}");
                var ffmpegDemuxArgs = new List<string>
                {
                    "-map", $"0:a:{sourceTrackIndex}",
                    "-c:a", "copy"
                };

                bool ffmpegDemuxSuccess = await _ffmpegRunner.RunAsync(
                    inputPath: sourceFilePath,
                    outputPath: tempRawPath,
                    extraArgs: ffmpegDemuxArgs,
                    overwrite: true);

                if (ffmpegDemuxSuccess && File.Exists(tempRawPath))
                {
                    var fallbackEacArgs = new List<string>
                    {
                        $"\"{tempRawPath}\"",
                        $"\"{tempShiftedPath}\"",
                        shiftArg,
                        "-silence",
                        "-progressnumbers",
                        "-log=nul"
                    };

                    eac3Success = await _eac3toRunner.RunAsync(fallbackEacArgs);
                    if (File.Exists(tempRawPath)) File.Delete(tempRawPath);
                }
            }

            if (!eac3Success || !File.Exists(tempShiftedPath))
            {
                throw new InvalidOperationException("Не удалось выполнить прямоточный физический сдвиг пересаживаемой аудиодорожки через eac3to.");
            }

            progressCallback(fileIndex, totalCount, "Мультиплексирование в MKV (mkvmerge)...", 70.0);

            // 4. Формирование итогового файла MKV
            string targetDir = string.IsNullOrWhiteSpace(outputPath) ? Path.GetDirectoryName(filePath)! : outputPath;
            string targetFileName = $"[DubSwap] {Path.GetFileNameWithoutExtension(filePath)}.mkv";
            string finalOutputPath = Path.Combine(targetDir, targetFileName);

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
                _logService.DebugLog($"Не удалось автоматически проанализировать флаги целевого видео: {ex.Message}", "AudioTransplantScript");
            }

            mkvInputs.Add(new MkvInputSource(filePath, destExtraArgs));

            bool mkvSuccess = await _mkvmergeRunner.RunAsync(finalOutputPath, mkvInputs);

            if (mkvSuccess && File.Exists(finalOutputPath))
            {
                _logService.Info($"Пересадка аудио завершена успешно! Выходной файл: '{finalOutputPath}'", "AudioTransplantScript");
                results.Add(finalOutputPath);
                progressCallback(fileIndex, totalCount, "Завершено успешно.", 100.0);
            }
            else
            {
                throw new InvalidOperationException("mkvmerge вернул ошибку при сборке итогового MKV файла.");
            }

            return results;
        }
        catch (Exception ex)
        {
            _logService.Exception(ex, $"Ошибка во время пересадки аудио для '{destFileName}': {ex.Message}", "AudioTransplantScript");
            throw;
        }
        finally
        {
            // Очистка временного прямоточного аудиофайла
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
