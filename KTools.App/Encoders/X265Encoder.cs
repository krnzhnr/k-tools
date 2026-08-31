// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KTools_App.Core;
using KTools_App.Models;

namespace KTools_App.Encoders;

public class X265Encoder : IVideoEncoder
{
    // === Конфигурация статических вариантов x265 в начале файла ===
    private static readonly List<string> PresetOptions = new() { "ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow", "placebo" };
    private static readonly List<string> RateControlOptions = new() { "CRF", "Битрейт (ABR)" };
    private static readonly List<string> TuneOptions = new() { "Нет", "grain", "animation", "fastdecode", "zerolatency", "psnr", "ssim" };
    private static readonly List<string> AqModeOptions = new() { "0", "1", "2", "3", "4" };
    private static readonly List<string> BAdaptOptions = new() { "0", "1", "2" };
    private static readonly List<string> DeblockOptions = new() { "0,0", "-1,-1", "-2,-2", "1,1" };

    public string StableId => "x265";

    public string DisplayName => "x265";

    public bool IsSupported(IHardwareCapabilityCache hardwareCache)
    {
        return true;
    }

    public string GetFfmpegCodecName(Dictionary<string, object> settings)
    {
        return "libx265";
    }

    public string GetPixelFormat(EncoderSharedContext context)
    {
        return context.Force10Bit ? "yuv420p10le" : "yuv420p";
    }

    public List<SettingField> GetEncoderSettings(Dictionary<string, object>? currentSettings = null, IHardwareCapabilityCache? hardwareCache = null)
    {
        bool isLossless = false;
        if (currentSettings != null && currentSettings.TryGetValue("lossless", out var losslessObj))
        {
            isLossless = losslessObj is true || (losslessObj is string s && s.Equals("True", StringComparison.OrdinalIgnoreCase));
        }

        return new List<SettingField>
        {
            new SettingField(
                "x265_preset",
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
                "x265_rc",
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
                "x265_auto_bitrate",
                "Автовычисление границ битрейтов",
                SettingType.Checkbox,
                true,
                "Видео:Битрейт",
                comment: "Автоматически задает мин/макс битрейт и размер буфера на основе основного битрейта",
                column: 0,
                colSpan: 2,
                visibilityConditions: new List<SettingVisibilityCondition>
                {
                    new("x265_rc", "Битрейт (ABR)")
                },
                disableConditions: new List<SettingDisableCondition>
                {
                    new("lossless", "True")
                }
            ),
            new SettingField(
                "x265_crf",
                "CRF / Качество",
                SettingType.Int,
                23,
                "Видео:Битрейт",
                comment: "Коэффициент постоянного качества (0-51). Меньше = лучше",
                column: 1,
                colSpan: 1,
                visibilityConditions: new List<SettingVisibilityCondition>
                {
                    new("x265_rc", "CRF")
                },
                disableConditions: new List<SettingDisableCondition>
                {
                    new("lossless", "True")
                },
                minimum: 0,
                maximum: 51
            ),
            new SettingField(
                "x265_v_bitrate",
                "Битрейт видео (кбит/с)",
                SettingType.Int,
                4000,
                "Видео:Битрейт",
                comment: "Целевой битрейт видеопотока",
                column: 1,
                colSpan: 1,
                visibilityConditions: new List<SettingVisibilityCondition>
                {
                    new("x265_rc", "Битрейт (ABR)")
                },
                disableConditions: new List<SettingDisableCondition>
                {
                    new("lossless", "True")
                },
                minimum: 100,
                maximum: 500000
            ),
            new SettingField(
                "x265_min_bitrate",
                "Мин. битрейт (кбит/с)",
                SettingType.Int,
                4000,
                "Видео:Битрейт",
                comment: "Минимальный битрейт",
                column: 0,
                colSpan: 1,
                visibilityConditions: new List<SettingVisibilityCondition>
                {
                    new("x265_rc", "Битрейт (ABR)")
                },
                disableConditions: new List<SettingDisableCondition>
                {
                    new("x265_auto_bitrate", "True"),
                    new("auto_bitrate", "True"),
                    new("lossless", "True")
                },
                minimum: 0,
                maximum: 500000
            ),
            new SettingField(
                "x265_max_bitrate",
                "Макс. битрейт (кбит/с)",
                SettingType.Int,
                8000,
                "Видео:Битрейт",
                comment: "Максимальный битрейт",
                column: 1,
                colSpan: 1,
                visibilityConditions: new List<SettingVisibilityCondition>
                {
                    new("x265_rc", "Битрейт (ABR)")
                },
                disableConditions: new List<SettingDisableCondition>
                {
                    new("x265_auto_bitrate", "True"),
                    new("auto_bitrate", "True"),
                    new("lossless", "True")
                },
                minimum: 0,
                maximum: 500000
            ),
            new SettingField(
                "x265_bufsize",
                "Размер буфера VBV (кбит)",
                SettingType.Int,
                16000,
                "Видео:Битрейт",
                comment: "Размер VBV буфера",
                column: 0,
                colSpan: 2,
                visibilityConditions: new List<SettingVisibilityCondition>
                {
                    new("x265_rc", "Битрейт (ABR)")
                },
                disableConditions: new List<SettingDisableCondition>
                {
                    new("x265_auto_bitrate", "True"),
                    new("auto_bitrate", "True"),
                    new("lossless", "True")
                },
                minimum: 0,
                maximum: 1000000
            ),
            new SettingField(
                "x265_tune",
                "Tune",
                SettingType.Combo,
                "Нет",
                "Видео:Расширенные параметры",
                options: TuneOptions,
                column: 0,
                colSpan: 1
            ),
            new SettingField(
                "x265_aq_mode",
                "AQ Mode",
                SettingType.Combo,
                "2",
                "Видео:Расширенные параметры",
                options: AqModeOptions,
                comment: "Режим адаптивного квантования (0 - выкл, 1 - обычный, 2 - auto-variance, 3 - темные сцены, 4 - с контурами)",
                column: 1,
                colSpan: 1
            ),
            new SettingField(
                "x265_aq_strength",
                "Сила AQ",
                SettingType.Float,
                1.0f,
                "Видео:Расширенные параметры",
                comment: "Сила перераспределения битрейта (0.0 - 3.0). Увеличение снижает артефакты на градиентах",
                column: 0,
                colSpan: 1,
                minimum: 0.0,
                maximum: 3.0
            ),
            new SettingField(
                "x265_lookahead",
                "RC Lookahead",
                SettingType.Int,
                20,
                "Видео:Расширенные параметры",
                comment: "Количество кадров анализа вперед (0-250). 0 - отключено, по умолчанию: 20",
                column: 1,
                colSpan: 1,
                minimum: 0,
                maximum: 250
            ),
            new SettingField(
                "x265_bframes",
                "B-кадры",
                SettingType.Int,
                4,
                "Видео:Расширенные параметры",
                comment: "Максимальное количество подряд идущих B-кадров (0-16). По умолчанию: 4",
                column: 0,
                colSpan: 1,
                minimum: 0,
                maximum: 16
            ),
            new SettingField(
                "x265_b_adapt",
                "Адаптация B-кадров",
                SettingType.Combo,
                "2",
                "Видео:Расширенные параметры",
                options: BAdaptOptions,
                comment: "Режим планера B-кадров (0 - выкл, 1 - быстрый, 2 - точный full/trellis)",
                column: 1,
                colSpan: 1
            ),
            new SettingField(
                "x265_psy_rd",
                "Psy-RD",
                SettingType.Float,
                2.0f,
                "Видео:Расширенные параметры",
                comment: "Сила психовизуальной оптимизации деталей (0.0 - 5.0). 0 - отключено",
                column: 0,
                colSpan: 1,
                minimum: 0.0,
                maximum: 5.0
            ),
            new SettingField(
                "x265_psy_rdoq",
                "Psy-RDOQ",
                SettingType.Float,
                0.0f,
                "Видео:Расширенные параметры",
                comment: "Сила психовизуальной оптимизации при RDO-квантовании (0.0 - 50.0). Сохраняет шумы и текстуры",
                column: 1,
                colSpan: 1,
                minimum: 0.0,
                maximum: 50.0
            ),
            new SettingField(
                "x265_deblock",
                "Деблокинг",
                SettingType.Combo,
                "0:0",
                "Видео:Расширенные параметры",
                options: DeblockOptions,
                comment: "Смещение деблокирующего фильтра tC:Beta (-1:-1 или -2:-2 для повышения резкости)",
                column: 0,
                colSpan: 1
            ),
            new SettingField(
                "x265_no_sao",
                "Отключить SAO",
                SettingType.Checkbox,
                false,
                "Видео:Расширенные параметры",
                comment: "Отключает сглаживающий фильтр SAO для сохранения точных текстур и зернистости",
                column: 1,
                colSpan: 1
            ),
            new SettingField(
                "x265_no_fast_pskip",
                "Отключить Fast P-Skip",
                SettingType.Checkbox,
                false,
                "Видео:Расширенные параметры",
                comment: "Отключает быстрый пропуск P-кадров для предотвращения мелких смазываний движущихся фонов",
                column: 0,
                colSpan: 1
            )
        };
    }

    public List<string> BuildEncoderArguments(Dictionary<string, object> settings, EncoderSharedContext context)
    {
        var args = new List<string>();
        args.AddRange(new[] { "-c:v", GetFfmpegCodecName(settings), "-pix_fmt", GetPixelFormat(context) });
        args.AddRange(new[] { "-preset", GetSettingValue(settings, "x265_preset", "medium") });

        var x265Params = new List<string>();

        if (context.IsLossless)
        {
            x265Params.Add("lossless=1");
        }
        else
        {
            string x265Rc = GetSettingValue(settings, "x265_rc", "CRF");
            if (x265Rc == "CRF")
            {
                int crf = GetSettingValue(settings, "x265_crf", 23);
                args.AddRange(new[] { "-crf", crf.ToString() });
            }
            else
            {
                int vBr = GetSettingValue(settings, "x265_v_bitrate", 4000);
                bool autoBitrate = GetSettingValue(settings, "x265_auto_bitrate", GetSettingValue(settings, "auto_bitrate", true));
                int minBr = autoBitrate ? vBr : GetSettingValue(settings, "x265_min_bitrate", GetSettingValue(settings, "min_bitrate", 4000));
                int maxBr = autoBitrate ? vBr * 2 : GetSettingValue(settings, "x265_max_bitrate", GetSettingValue(settings, "max_bitrate", 8000));
                int bufSize = autoBitrate ? maxBr * 2 : GetSettingValue(settings, "x265_bufsize", GetSettingValue(settings, "bufsize", 16000));

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

        string tune = GetSettingValue(settings, "x265_tune", "Нет");
        if (tune != "Нет")
        {
            args.AddRange(new[] { "-tune", tune });
        }

        x265Params.Add($"aq-mode={GetSettingValue(settings, "x265_aq_mode", "2")}");

        float aqStrength = GetSettingValue(settings, "x265_aq_strength", 1.0f);
        if (Math.Abs(aqStrength - 1.0f) > 0.01f)
        {
            float clampedAqStr = Math.Clamp(aqStrength, 0.0f, 3.0f);
            x265Params.Add($"aq-strength={clampedAqStr.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}");
        }

        int x265La = GetSettingValue(settings, "x265_lookahead", 20);
        if (x265La != 20)
        {
            int clampedLa = Math.Clamp(x265La, 0, 250);
            x265Params.Add($"rc-lookahead={clampedLa}");
        }

        int bframes = GetSettingValue(settings, "x265_bframes", 4);
        if (bframes != 4)
        {
            int clampedBframes = Math.Clamp(bframes, 0, 16);
            x265Params.Add($"bframes={clampedBframes}");
        }

        string bAdapt = GetSettingValue(settings, "x265_b_adapt", "2");
        if (bAdapt != "2")
        {
            x265Params.Add($"b-adapt={bAdapt}");
        }

        float psyRd = GetSettingValue(settings, "x265_psy_rd", 2.0f);
        if (Math.Abs(psyRd - 2.0f) > 0.01f)
        {
            float clampedPsyRd = Math.Clamp(psyRd, 0.0f, 5.0f);
            x265Params.Add($"psy-rd={clampedPsyRd.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}");
        }

        float psyRdoq = GetSettingValue(settings, "x265_psy_rdoq", 0.0f);
        if (Math.Abs(psyRdoq - 0.0f) > 0.01f)
        {
            float clampedPsyRdoq = Math.Clamp(psyRdoq, 0.0f, 50.0f);
            x265Params.Add($"psy-rdoq={clampedPsyRdoq.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}");
        }

        string deblock = GetSettingValue(settings, "x265_deblock", "0,0").Replace(":", ",");
        if (deblock != "0,0" && !string.IsNullOrWhiteSpace(deblock))
        {
            x265Params.Add($"deblock={deblock}");
        }

        if (GetSettingValue(settings, "x265_no_sao", false))
        {
            x265Params.Add("no-sao=1");
        }

        if (GetSettingValue(settings, "x265_no_fast_pskip", false))
        {
            x265Params.Add("no-fast-pskip=1");
        }

        if (x265Params.Count > 0)
        {
            args.Add("-x265-params");
            args.Add(string.Join(":", x265Params));
        }

        return args;
    }

    public Task<List<string>> BuildInputArgumentsAsync(MediaStructure mediaStructure, CancellationToken ct)
    {
        return Task.FromResult(new List<string>());
    }

    public string? GetContainerTag(Dictionary<string, object> settings, EncoderSharedContext context)
    {
        if (context.ContainerExtension.EndsWith("mp4", StringComparison.OrdinalIgnoreCase))
        {
            return "hvc1";
        }
        return null;
    }

    private T GetSettingValue<T>(Dictionary<string, object> settings, string key, T defaultValue)
    {
        if (settings.TryGetValue(key, out object? val) && val != null)
        {
            try
            {
                if (typeof(T) == typeof(int))
                {
                    return (T)(object)Convert.ToInt32(val);
                }
                if (typeof(T) == typeof(float))
                {
                    return (T)(object)Convert.ToSingle(val);
                }
                if (typeof(T) == typeof(bool))
                {
                    return (T)(object)Convert.ToBoolean(val);
                }
                if (typeof(T) == typeof(string))
                {
                    return (T)(object)val.ToString()!;
                }
                return (T)Convert.ChangeType(val, typeof(T));
            }
            catch
            {
                return defaultValue;
            }
        }
        return defaultValue;
    }
}
