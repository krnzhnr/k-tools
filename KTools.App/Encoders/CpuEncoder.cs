// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using KTools_App.Core;
using KTools_App.Encoders.Capabilities;
using KTools_App.Models;

namespace KTools_App.Encoders;

/// <summary>
/// Программный CPU-энкодер (FFmpeg libx264 / libx265).
/// Зеркалит <see cref="NvencEncoder"/>: выбор кодека через настройку cpu_codec,
/// реализация каждого кодека — отдельным классом <see cref="ISoftwareCodecCapabilities"/>
/// в своём файле, без условного ветвления внутри диспетчера.
/// Доступен всегда (не зависит от оборудования).
/// Все комментарии выполнены исключительно на русском языке.
/// </summary>
public class CpuEncoder : IVideoEncoder
{
    /// <summary>
    /// Ключ настройки выбора программного кодека.
    /// </summary>
    public const string CodecSettingKey = "cpu_codec";

    private readonly Dictionary<string, ISoftwareCodecCapabilities> _capabilitiesMap;
    private readonly ISoftwareCodecCapabilities _defaultCapabilities;

    public CpuEncoder()
    {
        var hevc = new CpuHevcCapabilities();
        var h264 = new CpuH264Capabilities();

        _capabilitiesMap = new Dictionary<string, ISoftwareCodecCapabilities>(StringComparer.OrdinalIgnoreCase)
        {
            { hevc.CodecName, hevc },
            { h264.CodecName, h264 }
        };
        // Дефолт — HEVC: старые конфигурации без ключа cpu_codec
        // продолжают кодироваться прежними настройками x265.
        _defaultCapabilities = hevc;
    }

    public string StableId => "cpu";

    public string DisplayName => "CPU";

    public bool IsSupported(IHardwareCapabilityCache hardwareCache)
    {
        return true;
    }

    public string GetFfmpegCodecName(Dictionary<string, object> settings)
    {
        var cap = GetCapabilityProvider(settings);
        return cap.FfmpegCodecName;
    }

    public string GetPixelFormat(EncoderSharedContext context)
    {
        // Кодек резолвится из контекста тем же правилом, что и остальные методы:
        // пустое имя даёт дефолт HEVC для старых конфигураций без ключа cpu_codec.
        Dictionary<string, object>? settings = string.IsNullOrEmpty(context.CodecName)
            ? null
            : new Dictionary<string, object> { { CodecSettingKey, context.CodecName } };
        var cap = GetCapabilityProvider(settings);
        return cap.GetPixelFormat(context.Force10Bit);
    }

    public List<SettingField> GetEncoderSettings(Dictionary<string, object>? currentSettings = null, IHardwareCapabilityCache? hardwareCache = null)
    {
        var cap = GetCapabilityProvider(currentSettings);
        var fields = new List<SettingField>
        {
            new SettingField(
                CodecSettingKey,
                "Кодек CPU",
                SettingType.Combo,
                cap.CodecName,
                "Видео:Кодирование",
                options: new List<string> { "HEVC / H.265", "AVC / H.264" },
                column: 0,
                colSpan: 1
            )
        };
        fields.AddRange(cap.GetCodecSettings(currentSettings));
        return fields;
    }

    public List<string> BuildEncoderArguments(Dictionary<string, object> settings, EncoderSharedContext context)
    {
        var cap = GetCapabilityProvider(settings);
        return cap.BuildCodecArguments(settings, context);
    }

    public Task<List<string>> BuildInputArgumentsAsync(MediaStructure mediaStructure, CancellationToken ct)
    {
        return _defaultCapabilities.BuildInputArgumentsAsync(mediaStructure, ct);
    }

    public string? GetContainerTag(Dictionary<string, object> settings, EncoderSharedContext context)
    {
        var cap = GetCapabilityProvider(settings);
        return cap.GetContainerTag(settings, context);
    }

    private ISoftwareCodecCapabilities GetCapabilityProvider(Dictionary<string, object>? settings)
    {
        if (settings != null && settings.TryGetValue(CodecSettingKey, out object? codecObj) && codecObj != null)
        {
            string codecStr = KTools_App.Core.SettingValue.AsString(codecObj, "");
            if (_capabilitiesMap.TryGetValue(codecStr, out var cap))
            {
                return cap;
            }
        }
        return _defaultCapabilities;
    }
}
