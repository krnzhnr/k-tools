// -*- coding: utf-8 -*-
using System.Collections.Generic;

using KTools_App.Core;

namespace KTools_App.Encoders.Capabilities;

/// <summary>
/// Специфичные возможности для H.264 NVENC (h264_nvenc).
/// </summary>
public class NvencH264Capabilities : BaseNvencCapabilities
{
    public override string CodecName => "AVC / H.264";
    public override string FfmpegCodecName => "h264_nvenc";
    public override bool Supports10Bit => false; // NVENC H.264 не поддерживает 10-бит (Main10)

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
}
