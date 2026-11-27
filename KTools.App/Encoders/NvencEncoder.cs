// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using KTools_App.Core;
using KTools_App.Encoders.Capabilities;
using KTools_App.Models;

namespace KTools_App.Encoders;

public class NvencEncoder : IVideoEncoder
{
    /// <summary>
    /// Ключ настройки выбора кодека NVENC.
    /// </summary>
    public const string CodecSettingKey = "nvenc_codec";

    private readonly Dictionary<string, INvencCodecCapabilities> _capabilitiesMap;
    private readonly INvencCodecCapabilities _defaultCapabilities;

    public NvencEncoder()
    {
        var hevc = new NvencHevcCapabilities();
        var h264 = new NvencH264Capabilities();
        var av1 = new NvencAv1Capabilities();

        _capabilitiesMap = new Dictionary<string, INvencCodecCapabilities>(StringComparer.OrdinalIgnoreCase)
        {
            { hevc.CodecName, hevc },
            { h264.CodecName, h264 },
            { av1.CodecName, av1 }
        };
        _defaultCapabilities = hevc;
    }

    public string StableId => "nvenc";

    public string DisplayName => "NVENC";

    public bool IsSupported(IHardwareCapabilityCache hardwareCache)
    {
        return hardwareCache.IsNvencSupported;
    }

    public string GetFfmpegCodecName(Dictionary<string, object> settings)
    {
        var cap = GetCapabilityProvider(settings);
        return cap.FfmpegCodecName;
    }

    public string GetPixelFormat(EncoderSharedContext context)
    {
        // Кодек резолвится из контекста тем же правилом, что и остальные методы:
        // пустое имя даёт дефолт HEVC для старых конфигураций без ключа nvenc_codec.
        Dictionary<string, object>? settings = string.IsNullOrEmpty(context.CodecName)
            ? null
            : new Dictionary<string, object> { { CodecSettingKey, context.CodecName } };
        var cap = GetCapabilityProvider(settings);
        return cap.GetPixelFormat(context.Force10Bit);
    }

    public List<SettingField> GetEncoderSettings(Dictionary<string, object>? currentSettings = null, IHardwareCapabilityCache? hardwareCache = null)
    {
        var cap = GetCapabilityProvider(currentSettings);
        return cap.GetCodecSettings(currentSettings, hardwareCache);
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

    private INvencCodecCapabilities GetCapabilityProvider(Dictionary<string, object>? settings)
    {
        return EncoderCapabilitySelector.Resolve(settings, CodecSettingKey, _capabilitiesMap, _defaultCapabilities);
    }
}
