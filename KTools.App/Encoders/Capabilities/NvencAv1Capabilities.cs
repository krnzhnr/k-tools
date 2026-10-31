// -*- coding: utf-8 -*-
using System.Collections.Generic;

using KTools_App.Core;

namespace KTools_App.Encoders.Capabilities;

/// <summary>
/// Специфичные возможности для AV1 NVENC (av1_nvenc).
/// </summary>
public class NvencAv1Capabilities : BaseNvencCapabilities
{
    // === Конфигурация вариантов AV1 в начале файла ===
    private static readonly List<string> BRefOptions = new() { "disabled", "middle" };
    private const string BRefComment = "Использование B-кадров как опорных (middle - B-pyramid, disabled - выключено; режим 'each' не поддерживается в AV1 NVENC)";

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
            new SettingField(
                "nv_b_ref_mode",
                "Опорные B-кадры",
                SettingType.Combo,
                "middle",
                "Видео:Расширенные параметры",
                options: BRefOptions,
                comment: BRefComment,
                column: 1,
                colSpan: 1,
                disableConditions: new List<SettingDisableCondition>
                {
                    new("lossless", "True")
                }
            )
        };
    }

    protected override void AppendCodecSpecificArguments(Dictionary<string, object> settings, EncoderSharedContext context, List<string> args)
    {
        // Ветка lossless недостижима: у AV1 SupportsLossless = false, поэтому
        // BuildCodecArguments выбрасывает исключение раньше вызова этого метода.
        string bRefMode = GetSettingValue(settings, "nv_b_ref_mode", "middle");
        if (bRefMode.Equals("each", System.StringComparison.OrdinalIgnoreCase))
        {
            bRefMode = "middle"; // Автоматическая защита от ошибки -542398533
        }

        if (!string.IsNullOrEmpty(bRefMode))
        {
            args.AddRange(new[] { "-b_ref_mode", bRefMode });
        }
    }
}
