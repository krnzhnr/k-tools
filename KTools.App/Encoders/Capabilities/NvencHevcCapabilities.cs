// -*- coding: utf-8 -*-
using System.Collections.Generic;
using KTools_App.Core;

namespace KTools_App.Encoders.Capabilities;

/// <summary>
/// Специфичные возможности для HEVC / H.265 NVENC (hevc_nvenc).
/// </summary>
public class NvencHevcCapabilities : BaseNvencCapabilities
{
    // === Конфигурация вариантов HEVC в начале файла ===
    private static readonly List<string> BRefOptions = new() { "disabled", "each", "middle" };
    private const string BRefComment = "Использование B-кадров как опорных (middle - B-pyramid, each - каждый, disabled - выключено)";

    public override string CodecName => "HEVC / H.265";
    public override string FfmpegCodecName => "hevc_nvenc";
    public override bool Supports10Bit => true;

    protected override List<SettingField> GetCodecSpecificSettings(Dictionary<string, object>? currentSettings)
    {
        return new List<SettingField>
        {
            new SettingField(
                "nv_b_ref_mode",
                "Опорные B-кадры",
                SettingType.Combo,
                "middle",
                "Видео:Расширенные параметры",
                options: BRefOptions,
                comment: BRefComment,
                column: 1,
                colSpan: 1
            )
        };
    }

    protected override void AppendCodecSpecificArguments(Dictionary<string, object> settings, EncoderSharedContext context, List<string> args)
    {
        string bRefMode = GetSettingValue(settings, "nv_b_ref_mode", "middle");
        if (!string.IsNullOrEmpty(bRefMode))
        {
            args.AddRange(new[] { "-b_ref_mode", bRefMode });
        }
    }

    public override string? GetContainerTag(Dictionary<string, object> settings, EncoderSharedContext context)
    {
        if (context.ContainerExtension.EndsWith("mp4", System.StringComparison.OrdinalIgnoreCase))
        {
            return "hvc1";
        }
        return null;
    }
}
