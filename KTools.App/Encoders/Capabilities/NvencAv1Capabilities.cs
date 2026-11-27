// -*- coding: utf-8 -*-
using System.Collections.Generic;

using KTools_App.Core;

namespace KTools_App.Encoders.Capabilities;

/// <summary>
/// Специфичные возможности для AV1 NVENC (av1_nvenc).
/// </summary>
public class NvencAv1Capabilities : BaseNvencCapabilities
{
    public override string CodecName => "AV1";
    public override string FfmpegCodecName => "av1_nvenc";
    public override bool Supports10Bit => true;

    /// <summary>
    /// AV1 NVENC не поддерживает tune lossless: реальный FFmpeg отклоняет такую команду
    /// (ошибка инициализации -542398533). Проверено совместимостными тестами RequiresFfmpeg.
    /// </summary>
    public override bool SupportsLossless => false;

    protected override List<SettingField> GetCodecSpecificSettings(Dictionary<string, object>? currentSettings)
    {
        return new List<SettingField>
        {
            BRefModeHelper.CreateSettingField(BRefModeHelper.GetOptions(supportEach: false), BRefModeHelper.Av1Comment)
        };
    }

    protected override void AppendCodecSpecificArguments(Dictionary<string, object> settings, EncoderSharedContext context, List<string> args)
    {
        // Ветка lossless недостижима: у AV1 SupportsLossless = false, поэтому
        // BuildCodecArguments выбрасывает исключение раньше вызова этого метода.
        BRefModeHelper.AppendArguments(settings, context, args, normalizeEachToMiddle: true);
    }
}
