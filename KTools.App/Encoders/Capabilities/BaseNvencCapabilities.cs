// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KTools_App.Core;
using KTools_App.Models;

namespace KTools_App.Encoders.Capabilities;

/// <summary>
/// Абстрактный базовый класс для возможностей NVENC.
/// Содержит ИСКЛЮЧИТЕЛЬНО те параметры, которые доказанно и 100% идентичны для всех подкодеков NVENC.
/// </summary>
public abstract class BaseNvencCapabilities : INvencCodecCapabilities
{
    // === Конфигурация общих статических вариантов NVENC в начале файла ===
    protected static readonly List<string> CodecOptions = new() { "HEVC / H.265", "AVC / H.264", "AV1" };
    protected static readonly List<string> PresetOptions = new() { "p1", "p2", "p3", "p4", "p5", "p6", "p7" };
    protected static readonly List<string> RateControlOptions = new() { "cbr", "vbr", "constqp" };
    protected static readonly List<string> MultipassOptions = new() { "disabled", "qres", "fullres" };

    public abstract string CodecName { get; }
    public abstract string FfmpegCodecName { get; }
    public abstract bool Supports10Bit { get; }

    public virtual string GetPixelFormat(bool force10Bit)
    {
        return (Supports10Bit && force10Bit) ? "p010le" : "yuv420p";
    }

    public virtual Task<List<string>> BuildInputArgumentsAsync(MediaStructure mediaStructure, CancellationToken ct)
    {
        var args = new List<string>();
        if (mediaStructure != null && mediaStructure.GetVideoTracks().Count > 0)
        {
            args.AddRange(new[] { "-hwaccel", "cuda" });
        }
        return Task.FromResult(args);
    }

    public virtual string? GetContainerTag(Dictionary<string, object> settings, EncoderSharedContext context)
    {
        return null;
    }

    /// <summary>
    /// Возвращает 100% проверенные общие настройки NVENC плюс специфичные настройки подкодека.
    /// </summary>
    public List<SettingField> GetCodecSettings(Dictionary<string, object>? currentSettings = null, IHardwareCapabilityCache? hardwareCache = null)
    {
        var fields = GetCommonNvencSettings(hardwareCache);
        fields.AddRange(GetCodecSpecificSettings(currentSettings));
        return fields;
    }

    /// <summary>
    /// Собирает аргументы CLI FFmpeg: общие NVENC аргументы + специфичные для кодека.
    /// </summary>
    public List<string> BuildCodecArguments(Dictionary<string, object> settings, EncoderSharedContext context)
    {
        var args = new List<string>();
        string pixFmt = GetPixelFormat(context.Force10Bit);

        args.AddRange(new[] { "-c:v", FfmpegCodecName, "-pix_fmt", pixFmt });

        string preset = GetSettingValue(settings, "nvenc_preset", "p7");
        args.AddRange(new[] { "-preset", preset });

        if (context.IsLossless)
        {
            args.AddRange(new[] { "-rc", "constqp", "-tune", "lossless", "-qp", "0" });
        }
        else
        {
            string rc = GetSettingValue(settings, "nvenc_rc", "vbr");
            if (rc.Equals("vbr_hq", StringComparison.OrdinalIgnoreCase))
            {
                rc = "vbr";
            }
            args.AddRange(new[] { "-rc", rc });

            if (rc == "constqp")
            {
                int qp = GetSettingValue(settings, "v_qp", 0);
                args.AddRange(new[] { "-qp", qp.ToString() });
            }
            else
            {
                int vBr = GetSettingValue(settings, "v_bitrate", 4000);
                bool autoBitrate = GetSettingValue(settings, "auto_bitrate", true);

                int minBr = autoBitrate ? vBr : GetSettingValue(settings, "min_bitrate", 4000);
                int maxBr = autoBitrate ? vBr * 2 : GetSettingValue(settings, "max_bitrate", 8000);
                int bufSize = autoBitrate ? maxBr * 2 : GetSettingValue(settings, "bufsize", 16000);

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

        int lookahead = GetSettingValue(settings, "nv_lookahead", 32);
        if (lookahead > 0)
        {
            args.AddRange(new[] { "-rc-lookahead", Math.Clamp(lookahead, 0, 32).ToString() });
        }

        if (!context.IsLossless)
        {
            bool spatialAq = GetSettingValue(settings, "nv_spatial_aq", true);
            if (spatialAq)
            {
                args.AddRange(new[] { "-spatial-aq", "1" });
                int aqStrength = GetSettingValue(settings, "nv_aq_strength", 8);
                args.AddRange(new[] { "-aq-strength", Math.Clamp(aqStrength, 1, 15).ToString() });
            }
            else
            {
                args.AddRange(new[] { "-spatial-aq", "0" });
            }

            bool temporalAq = GetSettingValue(settings, "nv_temporal_aq", true);
            if (temporalAq)
            {
                args.AddRange(new[] { "-temporal-aq", "1" });
            }
            else
            {
                args.AddRange(new[] { "-temporal-aq", "0" });
            }
        }

        string multipass = GetSettingValue(settings, "nv_multipass", "fullres");
        if (!string.IsNullOrEmpty(multipass))
        {
            args.AddRange(new[] { "-multipass", multipass });
        }

        AppendCodecSpecificArguments(settings, context, args);
        return args;
    }

    /// <summary>
    /// Абстрактный метод для получения настроек, специфичных для подкодека (например, b_ref_mode).
    /// </summary>
    protected abstract List<SettingField> GetCodecSpecificSettings(Dictionary<string, object>? currentSettings);

    /// <summary>
    /// Абстрактный метод для добавления специфичных аргументов CLI для подкодека.
    /// </summary>
    protected abstract void AppendCodecSpecificArguments(Dictionary<string, object> settings, EncoderSharedContext context, List<string> args);

    /// <summary>
    /// Возвращает список 100% общих настроек, присущих ВСЕМ подкодекам NVENC.
    /// </summary>
    protected List<SettingField> GetCommonNvencSettings(IHardwareCapabilityCache? hardwareCache = null)
    {
        bool temporalAqSupported = hardwareCache?.IsNvencTemporalAqSupported ?? true;

        var temporalAqDisableConditions = new List<SettingDisableCondition>
        {
            new("lossless", "True")
        };

        if (!temporalAqSupported)
        {
            temporalAqDisableConditions.Add(new SettingDisableCondition("encoder", "nvenc"));
        }

        return new List<SettingField>
        {
            new SettingField(
                "nvenc_codec",
                "Кодек NVENC",
                SettingType.Combo,
                CodecName,
                "Видео:Кодирование",
                options: CodecOptions,
                column: 1,
                colSpan: 1
            ),
            new SettingField(
                "nvenc_preset",
                "Пресет скорости",
                SettingType.Combo,
                "p4",
                "Видео:Кодирование",
                options: PresetOptions,
                comment: "Баланс между скоростью кодирования и сжатием/качеством (p1 - самый быстрый, p7 - максимальное сжатие)",
                column: 1,
                colSpan: 1
            ),
            new SettingField(
                "auto_bitrate",
                "Автовычисление границ битрейтов",
                SettingType.Checkbox,
                true,
                "Видео:Битрейт",
                comment: "Автоматически задает мин/макс битрейт и размер буфера на основе основного битрейта",
                column: 0,
                colSpan: 2,
                visibilityConditions: new List<SettingVisibilityCondition>
                {
                    new("lossless", "True", negate: true),
                    new("nvenc_rc", "constqp", negate: true)
                }
            ),
            new SettingField(
                "nvenc_rc",
                "Режим управления битрейтом",
                SettingType.Combo,
                "vbr",
                "Видео:Битрейт",
                options: RateControlOptions,
                comment: "CBR - постоянный битрейт, VBR - переменный, ConstQP - фиксированное качество",
                column: 0,
                colSpan: 1,
                visibilityConditions: new List<SettingVisibilityCondition>
                {
                    new("lossless", "True", negate: true)
                }
            ),
            new SettingField(
                "v_bitrate",
                "Битрейт видео (кбит/с)",
                SettingType.Int,
                4000,
                "Видео:Битрейт",
                comment: "Целевой битрейт видеопотока",
                column: 1,
                colSpan: 1,
                visibilityConditions: new List<SettingVisibilityCondition>
                {
                    new("lossless", "True", negate: true),
                    new("nvenc_rc", "constqp", negate: true)
                },
                minimum: 100,
                maximum: 500000
            ),
            new SettingField(
                "v_qp",
                "QP / Качество",
                SettingType.Int,
                0,
                "Видео:Битрейт",
                comment: "0 = авто, или фикс. QP (0-51). Меньше = лучше",
                column: 1,
                colSpan: 1,
                visibilityConditions: new List<SettingVisibilityCondition>
                {
                    new("lossless", "True", negate: true),
                    new("nvenc_rc", "constqp")
                },
                minimum: 0,
                maximum: 51
            ),
            new SettingField(
                "min_bitrate",
                "Мин. битрейт (кбит/с)",
                SettingType.Int,
                4000,
                "Видео:Битрейт",
                comment: "Минимальный битрейт",
                column: 0,
                colSpan: 1,
                visibilityConditions: new List<SettingVisibilityCondition>
                {
                    new("lossless", "True", negate: true),
                    new("nvenc_rc", "constqp", negate: true)
                },
                disableConditions: new List<SettingDisableCondition>
                {
                    new("auto_bitrate", "True")
                },
                minimum: 0,
                maximum: 500000
            ),
            new SettingField(
                "max_bitrate",
                "Макс. битрейт (кбит/с)",
                SettingType.Int,
                8000,
                "Видео:Битрейт",
                comment: "Максимальный битрейт",
                column: 1,
                colSpan: 1,
                visibilityConditions: new List<SettingVisibilityCondition>
                {
                    new("lossless", "True", negate: true),
                    new("nvenc_rc", "constqp", negate: true)
                },
                disableConditions: new List<SettingDisableCondition>
                {
                    new("auto_bitrate", "True")
                },
                minimum: 0,
                maximum: 500000
            ),
            new SettingField(
                "bufsize",
                "Размер буфера VBV (кбит)",
                SettingType.Int,
                16000,
                "Видео:Битрейт",
                comment: "Размер VBV буфера",
                column: 0,
                colSpan: 2,
                visibilityConditions: new List<SettingVisibilityCondition>
                {
                    new("lossless", "True", negate: true),
                    new("nvenc_rc", "constqp", negate: true)
                },
                disableConditions: new List<SettingDisableCondition>
                {
                    new("auto_bitrate", "True")
                },
                minimum: 0,
                maximum: 1000000
            ),
            new SettingField(
                "nv_spatial_aq",
                "Spatial AQ",
                SettingType.Checkbox,
                true,
                "Видео:Расширенные параметры",
                comment: "Пространственное адаптивное квантование (улучшает детали на градиентах и плоских поверхностях)",
                column: 0,
                colSpan: 1,
                disableConditions: new List<SettingDisableCondition>
                {
                    new("lossless", "True")
                }
            ),
            new SettingField(
                "nv_aq_strength",
                "Сила Spatial AQ",
                SettingType.Int,
                8,
                "Видео:Расширенные параметры",
                comment: "Сила адаптивного квантования (1-15). По умолчанию: 8",
                column: 0,
                colSpan: 1,
                disableConditions: new List<SettingDisableCondition>
                {
                    new("nv_spatial_aq", "False"),
                    new("lossless", "True")
                },
                minimum: 1,
                maximum: 15
            ),
            new SettingField(
                "nv_temporal_aq",
                "Temporal AQ",
                SettingType.Checkbox,
                temporalAqSupported,
                "Видео:Расширенные параметры",
                comment: temporalAqSupported 
                    ? "Временное адаптивное квантование (сохраняет детали в динамике)"
                    : "Не поддерживается вашей видеокартой / драйвером NVIDIA",
                column: 1,
                colSpan: 1,
                disableConditions: temporalAqDisableConditions
            )
        };
    }

    protected T GetSettingValue<T>(Dictionary<string, object> settings, string key, T defaultValue)
    {
        if (settings.TryGetValue(key, out object? val) && val != null)
        {
            try
            {
                if (typeof(T) == typeof(int))
                {
                    return (T)(object)Convert.ToInt32(val);
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
