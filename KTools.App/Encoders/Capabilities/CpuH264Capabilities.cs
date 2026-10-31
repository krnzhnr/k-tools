// -*- coding: utf-8 -*-
using System.Collections.Generic;

using KTools_App.Core;

namespace KTools_App.Encoders.Capabilities;

/// <summary>
/// Программный кодек AVC / H.264 (libx264, CPU).
/// Самый совместимый кодек: играет везде, кодирует заметно быстрее libx265.
/// Используются только проверенные флаги libx264 (preset, crf/qp, bitrate, tune, profile).
/// 10-бит осознанно не поддерживается (Hi10P почти нигде не воспроизводится) —
/// формат пикселей всегда yuv420p.
/// Все комментарии выполнены исключительно на русском языке.
/// </summary>
public class CpuH264Capabilities : BaseSoftwareCapabilities
{
    // === Конфигурация статических вариантов x264 в начале файла ===
    private static readonly List<string> PresetOptions = new() { "ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow", "placebo" };
    private static readonly List<string> RateControlOptions = new() { "CRF", "Битрейт (ABR)" };
    private static readonly List<string> TuneOptions = new() { "Нет", "film", "animation", "grain", "stillimage", "fastdecode", "zerolatency", "psnr", "ssim" };
    private static readonly List<string> ProfileOptions = new() { "Нет", "high", "main", "baseline" };

    public override string CodecName => "AVC / H.264";

    public override string FfmpegCodecName => "libx264";

    public override bool Supports10Bit => false;

    public override List<SettingField> GetCodecSettings(Dictionary<string, object>? currentSettings = null)
    {
        bool isLossless = IsLosslessSetting(currentSettings);

        return new List<SettingField>
        {
            new SettingField(
                "x264_preset",
                "Пресет",
                SettingType.Combo,
                isLossless ? "ultrafast" : "medium",
                "Видео:Кодирование",
                options: PresetOptions,
                comment: "Баланс между скоростью кодирования и сжатием/качеством",
                column: 1,
                colSpan: 1
            ),
            new SettingField(
                "x264_rc",
                "Режим качества",
                SettingType.Combo,
                "CRF",
                "Видео:Битрейт",
                options: RateControlOptions,
                column: 0,
                colSpan: 1,
                disableConditions: new List<SettingDisableCondition>
                {
                    new("lossless", "True")
                }
            ),
            new SettingField(
                "x264_auto_bitrate",
                "Автовычисление границ битрейтов",
                SettingType.Checkbox,
                true,
                "Видео:Битрейт",
                comment: "Автоматически задает мин/макс битрейт и размер буфера на основе основного битрейта",
                column: 0,
                colSpan: 2,
                visibilityConditions: new List<SettingVisibilityCondition>
                {
                    new("x264_rc", "Битрейт (ABR)")
                },
                disableConditions: new List<SettingDisableCondition>
                {
                    new("lossless", "True")
                }
            ),
            new SettingField(
                "x264_crf",
                "CRF / Качество",
                SettingType.Int,
                23,
                "Видео:Битрейт",
                comment: "Коэффициент постоянного качества (0-51). Меньше = лучше",
                column: 1,
                colSpan: 1,
                visibilityConditions: new List<SettingVisibilityCondition>
                {
                    new("x264_rc", "CRF")
                },
                disableConditions: new List<SettingDisableCondition>
                {
                    new("lossless", "True")
                },
                minimum: 0,
                maximum: 51
            ),
            new SettingField(
                "x264_v_bitrate",
                "Битрейт видео (кбит/с)",
                SettingType.Int,
                4000,
                "Видео:Битрейт",
                comment: "Целевой битрейт видеопотока",
                column: 1,
                colSpan: 1,
                visibilityConditions: new List<SettingVisibilityCondition>
                {
                    new("x264_rc", "Битрейт (ABR)")
                },
                disableConditions: new List<SettingDisableCondition>
                {
                    new("lossless", "True")
                },
                minimum: 100,
                maximum: 500000
            ),
            new SettingField(
                "x264_min_bitrate",
                "Мин. битрейт (кбит/с)",
                SettingType.Int,
                4000,
                "Видео:Битрейт",
                comment: "Минимальный битрейт",
                column: 0,
                colSpan: 1,
                visibilityConditions: new List<SettingVisibilityCondition>
                {
                    new("x264_rc", "Битрейт (ABR)")
                },
                disableConditions: new List<SettingDisableCondition>
                {
                    new("x264_auto_bitrate", "True"),
                    new("lossless", "True")
                },
                minimum: 0,
                maximum: 500000
            ),
            new SettingField(
                "x264_max_bitrate",
                "Макс. битрейт (кбит/с)",
                SettingType.Int,
                8000,
                "Видео:Битрейт",
                comment: "Максимальный битрейт",
                column: 1,
                colSpan: 1,
                visibilityConditions: new List<SettingVisibilityCondition>
                {
                    new("x264_rc", "Битрейт (ABR)")
                },
                disableConditions: new List<SettingDisableCondition>
                {
                    new("x264_auto_bitrate", "True"),
                    new("lossless", "True")
                },
                minimum: 0,
                maximum: 500000
            ),
            new SettingField(
                "x264_bufsize",
                "Размер буфера VBV (кбит)",
                SettingType.Int,
                16000,
                "Видео:Битрейт",
                comment: "Размер VBV буфера",
                column: 0,
                colSpan: 2,
                visibilityConditions: new List<SettingVisibilityCondition>
                {
                    new("x264_rc", "Битрейт (ABR)")
                },
                disableConditions: new List<SettingDisableCondition>
                {
                    new("x264_auto_bitrate", "True"),
                    new("lossless", "True")
                },
                minimum: 0,
                maximum: 1000000
            ),
            new SettingField(
                "x264_advanced_options",
                "Расширенные параметры",
                SettingType.Expander,
                false,
                "Видео:Расширенные параметры",
                comment: "Тонкая настройка x264. Тумблер выключен — кодирование идёт с полными параметрами по умолчанию FFmpeg/x264, без навязанных приложением значений.",
                headerIconGlyph: "\uE713",
                childFields: new List<SettingField>
                {
                    new SettingField(
                        "x264_tune",
                        "Tune",
                        SettingType.Combo,
                        "Нет",
                        "Видео:Расширенные параметры",
                        options: TuneOptions,
                        comment: "Оптимизация под тип контента (film — кино, animation — мультфильмы, grain — зерно, zerolatency — стриминг)",
                        column: 0,
                        colSpan: 1
                    ),
                    new SettingField(
                        "x264_profile",
                        "Профиль",
                        SettingType.Combo,
                        "Нет",
                        "Видео:Расширенные параметры",
                        options: ProfileOptions,
                        comment: "Ограничение совместимости (baseline — старые устройства, main — широкая совместимость, high — максимум качества). «Нет» — автовыбор кодировщика.",
                        column: 1,
                        colSpan: 1
                    )
                })
        };
    }

    public override List<string> BuildCodecArguments(Dictionary<string, object> settings, EncoderSharedContext context)
    {
        var args = new List<string>();
        args.AddRange(new[] { "-c:v", FfmpegCodecName, "-pix_fmt", GetPixelFormat(context.Force10Bit) });
        args.AddRange(new[] { "-preset", GetSettingValue(settings, "x264_preset", "medium") });

        if (context.IsLossless)
        {
            // Проверенный математический Lossless для libx264 — квантователь 0.
            args.AddRange(new[] { "-qp", "0" });
        }
        else
        {
            string rc = GetSettingValue(settings, "x264_rc", "CRF");
            if (rc == "CRF")
            {
                int crf = GetSettingValue(settings, "x264_crf", 23);
                args.AddRange(new[] { "-crf", crf.ToString() });
            }
            else
            {
                int vBr = GetSettingValue(settings, "x264_v_bitrate", 4000);
                bool autoBitrate = GetSettingValue(settings, "x264_auto_bitrate", true);
                int minBr = autoBitrate ? vBr : GetSettingValue(settings, "x264_min_bitrate", 4000);
                int maxBr = autoBitrate ? vBr * 2 : GetSettingValue(settings, "x264_max_bitrate", 8000);
                int bufSize = autoBitrate ? maxBr * 2 : GetSettingValue(settings, "x264_bufsize", 16000);

                args.AddRange(new[] { "-b:v", $"{vBr}k" });
                if (minBr > 0)
                {
                    args.AddRange(new[] { "-minrate", $"{minBr}k" });
                }
                if (maxBr > 0)
                {
                    args.AddRange(new[] { "-maxrate", $"{maxBr}k" });
                }
                if (bufSize > 0)
                {
                    args.AddRange(new[] { "-bufsize", $"{bufSize}k" });
                }
            }
        }

        // Расширенные параметры применяются только при включённом тумблере экспандера
        // «x264_advanced_options». Выключенный тумблер означает полный дефолт FFmpeg/x264.
        bool advancedEnabled = GetSettingValue(settings, "x264_advanced_options", false);

        if (advancedEnabled)
        {
            string tune = GetSettingValue(settings, "x264_tune", "Нет");
            if (tune != "Нет")
            {
                args.AddRange(new[] { "-tune", tune });
            }

            string profile = GetSettingValue(settings, "x264_profile", "Нет");
            if (profile != "Нет")
            {
                args.AddRange(new[] { "-profile:v", profile });
            }
        }

        return args;
    }
}
