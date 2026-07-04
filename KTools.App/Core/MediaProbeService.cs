// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using KTools_App.Infrastructure;
using KTools_App.Services.Contracts;

namespace KTools_App.Core;

/// <summary>
/// Синглтон-сервис для асинхронного сбора метаданных и зондирования структуры
/// медиафайлов (видео, аудио, дорожек субтитров и вложений).
/// Использует mkvmerge для MKV/MKA и ffprobe для остальных контейнеров.
/// Все комментарии и логирование выполнены исключительно на русском языке.
/// </summary>
public sealed class MediaProbeService : IMediaProbeService
{
    private readonly ILogService _logService;
    private readonly IMkvmergeRunner _mkvmergeRunner;
    private readonly IFFmpegRunner _ffmpegRunner;

    /// <summary>
    /// Инициализирует новый экземпляр класса MediaProbeService с внедрением зависимостей.
    /// </summary>
    public MediaProbeService(ILogService logService, IMkvmergeRunner mkvmergeRunner, IFFmpegRunner ffmpegRunner)
    {
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _mkvmergeRunner = mkvmergeRunner ?? throw new ArgumentNullException(nameof(mkvmergeRunner));
        _ffmpegRunner = ffmpegRunner ?? throw new ArgumentNullException(nameof(ffmpegRunner));
    }



    /// <summary>
    /// Асинхронно анализирует медиафайл и возвращает его полную структуру.
    /// Автоматически выбирает оптимальную стратегию в зависимости от контейнера.
    /// </summary>
    /// <param name="filePath">Абсолютный путь к файлу.</param>
    /// <returns>Объект MediaStructure с дорожками и вложениями, или null при сбоях.</returns>
    public async Task<MediaStructure?> ProbeAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            _logService.Error($"Файл не существует или путь пуст: '{filePath}'", "MediaProbeService");
            return null;
        }

        string extension = Path.GetExtension(filePath).ToLowerInvariant();
        _logService.Info($"Начало фонового анализа структуры файла: '{Path.GetFileName(filePath)}'", "MediaProbeService");

        try
        {
            // Определяем, является ли файл MKV-контейнером
            bool isMkv = extension == ".mkv" || extension == ".mka";

            if (isMkv)
            {
                return await ProbeMkvAsync(filePath);
            }
            else
            {
                return await ProbeGenericAsync(filePath);
            }
        }
        catch (Exception ex)
        {
            _logService.Exception(ex, $"Непредвиденная ошибка при зондировании файла '{filePath}'", "MediaProbeService");
            return null;
        }
    }

    /// <summary>
    /// Анализирует MKV/MKA-контейнеры через mkvmerge --identify с обогащением пустых заголовков через ffprobe.
    /// </summary>
    private async Task<MediaStructure?> ProbeMkvAsync(string filePath)
    {
        using var jsonDoc = await _mkvmergeRunner.IdentifyAsync(filePath);
        if (jsonDoc == null)
        {
            _logService.Error($"Не удалось выполнить mkvmerge --identify для '{filePath}'", "MediaProbeService");
            return null;
        }

        var structure = new MediaStructure { FilePath = filePath };
        var root = jsonDoc.RootElement;

        // 1. Извлекаем длительность контейнера (в наносекундах)
        if (root.TryGetProperty("container", out var containerProp) &&
            containerProp.TryGetProperty("properties", out var containerPropsProp) &&
            containerPropsProp.TryGetProperty("duration", out var durationProp))
        {
            // Переводим наносекунды в секунды
            structure.Duration = durationProp.GetInt64() / 1_000_000_000.0;
        }

        // 2. Парсим дорожки
        if (root.TryGetProperty("tracks", out var tracksProp) && tracksProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var rawTrack in tracksProp.EnumerateArray())
            {
                var track = new MediaTrack();
                
                if (rawTrack.TryGetProperty("id", out var idProp))
                {
                    track.TrackId = idProp.GetInt32();
                }

                if (rawTrack.TryGetProperty("type", out var typeProp))
                {
                    track.TrackType = typeProp.GetString() ?? string.Empty;
                }

                if (rawTrack.TryGetProperty("codec", out var codecProp))
                {
                    track.Codec = codecProp.GetString() ?? string.Empty;
                }

                if (rawTrack.TryGetProperty("properties", out var props))
                {
                    // Определяем и нормализуем язык
                    string langRaw = "und";
                    if (props.TryGetProperty("language_ietf", out var langIetf))
                    {
                        langRaw = langIetf.GetString() ?? "und";
                    }
                    else if (props.TryGetProperty("language", out var langIso))
                    {
                        langRaw = langIso.GetString() ?? "und";
                    }
                    track.Language = AppConstants.NormalizeLanguage(langRaw);

                    // Извлекаем заголовок
                    if (props.TryGetProperty("track_name", out var nameProp))
                    {
                        track.Name = nameProp.GetString() ?? string.Empty;
                    }

                    // Разрешение видео
                    string resolution = string.Empty;
                    if (props.TryGetProperty("display_dimensions", out var dispDim))
                    {
                        resolution = dispDim.GetString() ?? string.Empty;
                    }
                    else if (props.TryGetProperty("pixel_dimensions", out var pixDim))
                    {
                        resolution = pixDim.GetString() ?? string.Empty;
                    }
                    track.Resolution = resolution;

                    // Аудиоканалы
                    if (props.TryGetProperty("audio_channels", out var chanProp))
                    {
                        track.Channels = chanProp.GetInt32();
                    }

                    // Флаги disposition
                    if (props.TryGetProperty("default_track", out var defProp))
                    {
                        track.IsDefault = defProp.GetBoolean();
                    }
                    if (props.TryGetProperty("forced_track", out var forceProp))
                    {
                        track.IsForced = forceProp.GetBoolean();
                    }
                    if (props.TryGetProperty("hearing_impaired_track", out var hearProp))
                    {
                        track.IsHearingImpaired = hearProp.GetBoolean();
                    }
                    if (props.TryGetProperty("commentary_track", out var commProp))
                    {
                        track.IsCommentary = commProp.GetBoolean();
                    }
                    if (props.TryGetProperty("flag_original", out var origProp))
                    {
                        track.IsOriginal = origProp.GetBoolean();
                    }
                    if (props.TryGetProperty("visual_impaired_track", out var visProp))
                    {
                        track.IsVisualImpaired = visProp.GetBoolean();
                    }
                }

                structure.Tracks.Add(track);
            }
        }

        // 3. Парсим встроенные вложения (attachments)
        if (root.TryGetProperty("attachments", out var attachsProp) && attachsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var rawAttach in attachsProp.EnumerateArray())
            {
                var attach = new MediaAttachment();

                if (rawAttach.TryGetProperty("id", out var idProp))
                {
                    attach.AttachmentId = idProp.GetInt32();
                }

                if (rawAttach.TryGetProperty("file_name", out var nameProp))
                {
                    attach.FileName = nameProp.GetString() ?? string.Empty;
                }

                if (rawAttach.TryGetProperty("content_type", out var typeProp))
                {
                    attach.MimeType = typeProp.GetString() ?? string.Empty;
                }

                if (rawAttach.TryGetProperty("size", out var sizeProp))
                {
                    attach.Size = sizeProp.GetInt64();
                }

                structure.Attachments.Add(attach);
            }
        }

        // 4. Обогащаем пустые заголовки дорожек через ffprobe
        bool hasNameless = structure.Tracks.Any(t => string.IsNullOrWhiteSpace(t.Name));
        if (hasNameless)
        {
            await EnrichTrackNamesAsync(structure);
        }

        _logService.Info($"Анализ MKV завершен: '{Path.GetFileName(filePath)}' (Дорожек: {structure.Tracks.Count}, Вложений: {structure.Attachments.Count})", "MediaProbeService");
        return structure;
    }

    /// <summary>
    /// Анализирует MP4/M2TS/TS/другие контейнеры через ffprobe.
    /// </summary>
    private async Task<MediaStructure?> ProbeGenericAsync(string filePath)
    {
        using var jsonDoc = await _ffmpegRunner.GetVideoInfoAsync(filePath);
        if (jsonDoc == null)
        {
            _logService.Error($"Не удалось выполнить ffprobe для '{filePath}'", "MediaProbeService");
            return null;
        }

        var structure = new MediaStructure { FilePath = filePath };
        var root = jsonDoc.RootElement;

        // 1. Извлекаем длительность из формата
        if (root.TryGetProperty("format", out var formatProp) &&
            formatProp.TryGetProperty("duration", out var durationProp))
        {
            string durStr = durationProp.GetString() ?? "0";
            if (double.TryParse(durStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double durVal))
            {
                structure.Duration = durVal;
            }
        }

        // 2. Парсим дорожки и вложения
        if (root.TryGetProperty("streams", out var streamsProp) && streamsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var stream in streamsProp.EnumerateArray())
            {
                string codecType = string.Empty;
                if (stream.TryGetProperty("codec_type", out var codecTypeProp))
                {
                    codecType = codecTypeProp.GetString() ?? string.Empty;
                }

                // Вложения в ffprobe идут как отдельный тип потока "attachment"
                if (codecType.Equals("attachment", StringComparison.OrdinalIgnoreCase))
                {
                    var attach = new MediaAttachment();
                    if (stream.TryGetProperty("index", out var idxProp))
                    {
                        attach.AttachmentId = idxProp.GetInt32();
                    }

                    if (stream.TryGetProperty("tags", out var tags))
                    {
                        if (tags.TryGetProperty("filename", out var fileProp))
                        {
                            attach.FileName = fileProp.GetString() ?? string.Empty;
                        }
                        if (tags.TryGetProperty("mimetype", out var mimeProp))
                        {
                            attach.MimeType = mimeProp.GetString() ?? string.Empty;
                        }
                    }

                    structure.Attachments.Add(attach);
                    continue;
                }

                // Преобразуем codec_type ffprobe во внутренний формат ("subtitle" -> "subtitles")
                string trackType = codecType.ToLowerInvariant();
                if (trackType == "subtitle")
                {
                    trackType = "subtitles";
                }

                // Парсим стандартную аудио/видео/субтитр дорожку
                if (trackType == "video" || trackType == "audio" || trackType == "subtitles")
                {
                    var track = new MediaTrack { TrackType = trackType };

                    if (stream.TryGetProperty("index", out var indexProp))
                    {
                        track.TrackId = indexProp.GetInt32();
                    }

                    if (stream.TryGetProperty("codec_name", out var codecProp))
                    {
                        track.Codec = codecProp.GetString() ?? string.Empty;
                    }

                    // Разрешение для видео
                    if (trackType == "video")
                    {
                        int w = 0, h = 0;
                        if (stream.TryGetProperty("width", out var wProp)) w = wProp.GetInt32();
                        if (stream.TryGetProperty("height", out var hProp)) h = hProp.GetInt32();
                        if (w > 0 && h > 0)
                        {
                            track.Resolution = $"{w}x{h}";
                        }
                    }

                    // Каналы для аудио
                    if (trackType == "audio" && stream.TryGetProperty("channels", out var chanProp))
                    {
                        track.Channels = chanProp.GetInt32();
                    }

                    // Парсим тэги (язык и название)
                    if (stream.TryGetProperty("tags", out var tags))
                    {
                        string langRaw = "und";
                        if (tags.TryGetProperty("language", out var langProp))
                        {
                            langRaw = langProp.GetString() ?? "und";
                        }
                        track.Language = AppConstants.NormalizeLanguage(langRaw);

                        if (tags.TryGetProperty("title", out var titleProp))
                        {
                            track.Name = titleProp.GetString() ?? string.Empty;
                        }
                    }

                    // Парсим disposition
                    if (stream.TryGetProperty("disposition", out var disp))
                    {
                        if (disp.TryGetProperty("default", out var defProp))
                        {
                            track.IsDefault = defProp.GetInt32() == 1;
                        }
                        if (disp.TryGetProperty("forced", out var forceProp))
                        {
                            track.IsForced = forceProp.GetInt32() == 1;
                        }
                        if (disp.TryGetProperty("hearing_impaired", out var hearProp))
                        {
                            track.IsHearingImpaired = hearProp.GetInt32() == 1;
                        }
                        if (disp.TryGetProperty("comment", out var commProp))
                        {
                            track.IsCommentary = commProp.GetInt32() == 1;
                        }
                        if (disp.TryGetProperty("original", out var origProp))
                        {
                            track.IsOriginal = origProp.GetInt32() == 1;
                        }
                    }

                    structure.Tracks.Add(track);
                }
            }
        }

        _logService.Info($"Анализ ffprobe завершен: '{Path.GetFileName(filePath)}' (Дорожек: {structure.Tracks.Count}, Вложений: {structure.Attachments.Count})", "MediaProbeService");
        return structure;
    }

    /// <summary>
    /// Обогащает пустые заголовки дорожек MKV данными из ffprobe.
    /// Сопоставление дорожек производится по типу и относительному индексу.
    /// </summary>
    private async Task EnrichTrackNamesAsync(MediaStructure structure)
    {
        try
        {
            using var probeDoc = await _ffmpegRunner.GetVideoInfoAsync(structure.FilePath);
            if (probeDoc == null) return;

            var streams = probeDoc.RootElement.GetProperty("streams");
            if (streams.ValueKind != JsonValueKind.Array) return;

            // Группируем дорожки ffprobe по типам
            var probeAudio = new List<string>();
            var probeSubs = new List<string>();
            var probeVideo = new List<string>();

            foreach (var stream in streams.EnumerateArray())
            {
                string codecType = stream.GetProperty("codec_type").GetString()?.ToLowerInvariant() ?? string.Empty;
                string title = string.Empty;

                if (stream.TryGetProperty("tags", out var tags) && tags.TryGetProperty("title", out var titleProp))
                {
                    title = titleProp.GetString() ?? string.Empty;
                }

                if (codecType == "audio") probeAudio.Add(title);
                else if (codecType == "subtitle") probeSubs.Add(title);
                else if (codecType == "video") probeVideo.Add(title);
            }

            // Группируем дорожки структуры mkvmerge
            var mkvAudio = structure.Tracks.Where(t => t.TrackType == "audio").ToList();
            var mkvSubs = structure.Tracks.Where(t => t.TrackType == "subtitles").ToList();
            var mkvVideo = structure.Tracks.Where(t => t.TrackType == "video").ToList();

            // Сопоставляем аудио
            for (int i = 0; i < mkvAudio.Count && i < probeAudio.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(mkvAudio[i].Name) && !string.IsNullOrWhiteSpace(probeAudio[i]))
                {
                    mkvAudio[i].Name = probeAudio[i];
                    _logService.DebugLog($"Дорожка аудио #{mkvAudio[i].TrackId} обогащена заголовком: '{probeAudio[i]}'", "MediaProbeService");
                }
            }

            // Сопоставляем субтитры
            for (int i = 0; i < mkvSubs.Count && i < probeSubs.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(mkvSubs[i].Name) && !string.IsNullOrWhiteSpace(probeSubs[i]))
                {
                    mkvSubs[i].Name = probeSubs[i];
                    _logService.DebugLog($"Дорожка субтитров #{mkvSubs[i].TrackId} обогащена заголовком: '{probeSubs[i]}'", "MediaProbeService");
                }
            }

            // Сопоставляем видео
            for (int i = 0; i < mkvVideo.Count && i < probeVideo.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(mkvVideo[i].Name) && !string.IsNullOrWhiteSpace(probeVideo[i]))
                {
                    mkvVideo[i].Name = probeVideo[i];
                    _logService.DebugLog($"Дорожка видео #{mkvVideo[i].TrackId} обогащена заголовком: '{probeVideo[i]}'", "MediaProbeService");
                }
            }
        }
        catch (Exception ex)
        {
            _logService.Exception(ex, "Ошибка обогащения заголовков дорожек через ffprobe", "MediaProbeService");
        }
    }
}
