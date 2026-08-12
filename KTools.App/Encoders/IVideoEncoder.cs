// -*- coding: utf-8 -*-
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KTools_App.Core;
using KTools_App.Models;

namespace KTools_App.Encoders;

/// <summary>
/// Общий контракт для всех видео-энкодеров (NVENC, x265, и др.).
/// Реализации должны быть потокобезопасными (stateless Singleton),
/// получая состояние только через передаваемые параметры.
/// </summary>
public interface IVideoEncoder
{
    /// <summary>
    /// Стабильный идентификатор для сохранения в конфигах (например, "nvenc", "x265").
    /// </summary>
    string StableId { get; }

    /// <summary>
    /// Отображаемое имя энкодера для пользовательского интерфейса (например, "NVENC (GPU)").
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Возвращает имя кодека FFmpeg на основе настроек (например, "hevc_nvenc", "h264_nvenc", "libx265").
    /// </summary>
    string GetFfmpegCodecName(Dictionary<string, object> settings);

    /// <summary>
    /// Возвращает формат пикселей (pix_fmt) с учётом переданного контекста.
    /// </summary>
    string GetPixelFormat(EncoderSharedContext context);

    /// <summary>
    /// Возвращает декларативную схему настроек, специфичных только для данного энкодера.
    /// Поддерживает динамическую генерацию опций и доступных полей на основе текущих настроек (например, выбранного кодека).
    /// </summary>
    List<SettingField> GetEncoderSettings(Dictionary<string, object>? currentSettings = null);

    /// <summary>
    /// Формирует аргументы кодирования FFmpeg (-c:v ... -preset ... -b:v ...).
    /// </summary>
    List<string> BuildEncoderArguments(Dictionary<string, object> settings, EncoderSharedContext context);

    /// <summary>
    /// Асинхронно формирует входные аргументы FFmpeg (например, аппаратное декодирование -hwaccel).
    /// </summary>
    Task<List<string>> BuildInputArgumentsAsync(MediaStructure mediaStructure, CancellationToken ct);

    /// <summary>
    /// Возвращает тег контейнера (например, "hvc1"). Возвращает null, если тег не требуется.
    /// </summary>
    string? GetContainerTag(Dictionary<string, object> settings, EncoderSharedContext context);
}
