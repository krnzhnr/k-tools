// -*- coding: utf-8 -*-
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using KTools_App.Core;
using KTools_App.Models;

namespace KTools_App.Encoders.Capabilities;

/// <summary>
/// Интерфейс возможностей для конкретного программного подкодека CPU (H.264, HEVC).
/// Зеркалит <see cref="INvencCodecCapabilities"/>: один кодек — один класс в отдельном файле
/// со своим проверенным набором параметров и аргументов, без условного ветвления.
/// Все комментарии выполнены исключительно на русском языке.
/// </summary>
public interface ISoftwareCodecCapabilities
{
    /// <summary>
    /// Отображаемое имя кодека (например, "HEVC / H.265").
    /// </summary>
    string CodecName { get; }

    /// <summary>
    /// Имя кодека для CLI FFmpeg (например, "libx265", "libx264").
    /// </summary>
    string FfmpegCodecName { get; }

    /// <summary>
    /// Флаг поддержки 10-битного формата пикселей (Main10).
    /// </summary>
    bool Supports10Bit { get; }

    /// <summary>
    /// Возвращает название формата пикселей в зависимости от настройки 10-бит.
    /// </summary>
    string GetPixelFormat(bool force10Bit);

    /// <summary>
    /// Возвращает полный список настроек для данного подкодека.
    /// </summary>
    List<SettingField> GetCodecSettings(Dictionary<string, object>? currentSettings = null);

    /// <summary>
    /// Формирует аргументы командной строки FFmpeg для данного подкодека.
    /// </summary>
    List<string> BuildCodecArguments(Dictionary<string, object> settings, EncoderSharedContext context);

    /// <summary>
    /// Асинхронно формирует входные аргументы FFmpeg (для CPU — пустой список).
    /// </summary>
    Task<List<string>> BuildInputArgumentsAsync(MediaStructure mediaStructure, CancellationToken ct);

    /// <summary>
    /// Возвращает тег контейнера (например, "hvc1" для HEVC в MP4).
    /// </summary>
    string? GetContainerTag(Dictionary<string, object> settings, EncoderSharedContext context);
}
