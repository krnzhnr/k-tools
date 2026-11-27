// -*- coding: utf-8 -*-
using System.Collections.Generic;

using KTools_App.Core;

namespace KTools_App.Encoders.Capabilities;

/// <summary>
/// Специфичные возможности для HEVC / H.265 NVENC (hevc_nvenc).
/// </summary>
public class NvencHevcCapabilities : BaseNvencCapabilities
{
    public override string CodecName => "HEVC / H.265";
    public override string FfmpegCodecName => "hevc_nvenc";
    public override bool Supports10Bit => true;

    protected override List<SettingField> GetCodecSpecificSettings(Dictionary<string, object>? currentSettings)
    {
        return new List<SettingField>
        {
            BRefModeHelper.CreateSettingField(BRefModeHelper.GetOptions(supportEach: true), BRefModeHelper.DefaultComment)
        };
    }

    protected override void AppendCodecSpecificArguments(Dictionary<string, object> settings, EncoderSharedContext context, List<string> args)
    {
        BRefModeHelper.AppendArguments(settings, context, args, normalizeEachToMiddle: false);
    }

    public override string? GetContainerTag(Dictionary<string, object> settings, EncoderSharedContext context)
    {
        return ContainerTagHelper.GetHevcContainerTag(context.ContainerExtension);
    }
}
