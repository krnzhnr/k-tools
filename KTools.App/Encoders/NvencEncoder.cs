// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KTools_App.Core;
using KTools_App.Encoders.Capabilities;
using KTools_App.Models;

namespace KTools_App.Encoders;

public class NvencEncoder : IVideoEncoder
{
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
        return context.Force10Bit ? "p010le" : "yuv420p";
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
        if (settings != null && settings.TryGetValue("nvenc_codec", out object? codecObj) && codecObj != null)
        {
            string codecStr = codecObj.ToString() ?? "";
            if (_capabilitiesMap.TryGetValue(codecStr, out var cap))
            {
                return cap;
            }
        }
        return _defaultCapabilities;
    }
}
