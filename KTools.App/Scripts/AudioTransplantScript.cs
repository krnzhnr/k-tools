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
    private readonly IAudioWaveformService _waveformService;
    private readonly IMediaProbeService _mediaProbeService;

    private const double AacPrimingDelayMs = 21.333333333333332; // 1024 / 48000 * 1000

    public AudioTransplantScript(
        ILogService logService,
        ISettingsManager settingsManager,
        IPathManager pathManager,
        IFFmpegRunner ffmpegRunner,
        IMkvmergeRunner mkvmergeRunner,
        IAudioWaveformService waveformService,
        IMediaProbeService mediaProbeService)
        : base(logService, settingsManager, pathManager)
    {
        _ffmpegRunner = ffmpegRunner ?? throw new ArgumentNullException(nameof(ffmpegRunner));
        _mkvmergeRunner = mkvmergeRunner ?? throw new ArgumentNullException(nameof(mkvmergeRunner));
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
    public override string[] RequiredDependencies => new[] { "ffmpeg", "mkvtoolnix" };

    /// <inheritdoc />
    public override bool UseCustomWidget => false;

    /// <inheritdoc />
    public override bool SupportsParallel => false; // Только последовательно для диалогов UI

    /// <inheritdoc />
    public override List<SettingField> SettingsSchema => new()
    {
        new SettingField(
            "SourceFile",
            "Исходный файл пересаживаемой аудиодорожки (путь)",
            SettingType.Text,
            "",
            "Источники"),

        new SettingField(
            "SourceTrackIndex",
            "Индекс аудиодорожки источника",
            SettingType.Int,
            0,
            "Источники"),

        new SettingField(
            "DestTrackIndex",
            "Индекс дорожки целевого видео (для выравнивания)",
            SettingType.Int,
            0,
            "Источники"),

        new SettingField(
            "ShiftMs",
            "Пользовательский сдвиг (мс)",
            SettingType.Int,
            0,
            "Синхронизация"),

        new SettingField(
            "UseVisualSync",
            "Запустить окно графической синхронизации (Win2D)",
            SettingType.Checkbox,
            true,
            "Синхронизация")
    };

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

        string sourceFilePath = GetSettingValue(settings, "SourceFile", "");
        if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
        {
            throw new FileNotFoundException($"Файл-источник пересаживаемого аудио не найден: '{sourceFilePath}'");
        }

        int sourceTrackIndex = GetSettingValue(settings, "SourceTrackIndex", 0);
        int destTrackIndex = GetSettingValue(settings, "DestTrackIndex", 0);
        int userShiftMs = GetSettingValue(settings, "ShiftMs", 0);

        // 2. Рассчитываем итоговый физический сдвиг с учетом вычитания задержки AAC (21.33 мс)
        double actualOffsetMs = userShiftMs - AacPrimingDelayMs;
        _logService.Info($"Пользовательский сдвиг: {userShiftMs} мс. Математическая компенсация AAC (-21.33 мс). Фактический сдвиг: {actualOffsetMs:F2} мс.", "AudioTransplantScript");

        // 3. Формируем путь кодированного временного AAC-файла
        string tempAacPath = Path.Combine(Path.GetTempPath(), $"transplant_{Guid.NewGuid():N}.aac");

        try
        {
            progressCallback(fileIndex, totalCount, "Кодирование и сдвиг аудио в AAC 256k...", 30.0);

            // Формируем цепочку фильтров для сдвига
            var filterParts = new List<string> { "aresample=resampler=soxr:out_sample_rate=48000" };

            int roundedOffsetMs = (int)Math.Round(actualOffsetMs);
            if (roundedOffsetMs > 0)
            {
                filterParts.Add($"adelay=delays={roundedOffsetMs}:all=1");
            }
            else if (roundedOffsetMs < 0)
            {
                double startSec = Math.Abs(actualOffsetMs) / 1000.0;
                filterParts.Add($"atrim=start={startSec.ToString("F4", CultureInfo.InvariantCulture)}");
                filterParts.Add("asetpts=PTS-STARTPTS");
            }

            string filterChain = string.Join(",", filterParts);

            var ffmpegExtraArgs = new List<string>
            {
                "-map", $"0:a:{sourceTrackIndex}",
                "-vsync", "cfr",
                "-af", filterChain,
                "-c:a", "aac",
                "-b:a", "256k"
            };

            bool ffmpegSuccess = await _ffmpegRunner.RunAsync(
                inputPath: sourceFilePath,
                outputPath: tempAacPath,
                extraArgs: ffmpegExtraArgs,
                overwrite: true);

            if (!ffmpegSuccess || !File.Exists(tempAacPath))
            {
                throw new InvalidOperationException("Не удалось извлечь и закодировать пересаживаемое аудио в AAC.");
            }

            progressCallback(fileIndex, totalCount, "Мультиплексирование в MKV (mkvmerge)...", 70.0);

            // 4. Формирование итогового файла MKV
            string targetDir = string.IsNullOrWhiteSpace(outputPath) ? Path.GetDirectoryName(filePath)! : outputPath;
            string targetFileName = $"[DubSwap] {Path.GetFileNameWithoutExtension(filePath)}.mkv";
            string finalOutputPath = Path.Combine(targetDir, targetFileName);

            var mkvInputs = new List<MkvInputSource>
            {
                new MkvInputSource(tempAacPath, new List<string>
                {
                    "--language", "0:rus",
                    "--default-track-flag", "0:yes",
                    "--forced-display-flag", "0:yes"
                })
            };

            // Вшивание внешних субтитров (если указаны)
            string subtitlesFile = GetSettingValue(settings, "SubtitlesFile", "");
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
            // Очистка временного AAC файла
            if (File.Exists(tempAacPath))
            {
                try
                {
                    File.Delete(tempAacPath);
                }
                catch { }
            }
        }
    }
}
