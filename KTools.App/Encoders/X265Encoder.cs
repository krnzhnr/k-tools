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

    public string StableId => "x265";

    public string DisplayName => "x265 (CPU)";

    public string GetFfmpegCodecName(Dictionary<string, object> settings)
    {
        return "libx265";
    }

    public string GetPixelFormat(EncoderSharedContext context)
    {
        return context.Force10Bit ? "yuv420p10le" : "yuv420p";
    }

    public List<SettingField> GetEncoderSettings(Dictionary<string, object>? currentSettings = null)
    {
        return new List<SettingField>
        {
            new SettingField(
                "cpu_preset",
                "Пресет",
                SettingType.Combo,
                "medium",
                "Видео:Кодирование",
                options: PresetOptions,
                comment: "Баланс между скоростью кодирования и сжатием/качеством",
                column: 1,
                colSpan: 1
            ),
            new SettingField(
                "cpu_rc",
                "Режим качества",
                SettingType.Combo,
                "CRF",
                "Видео:Битрейт",
                options: RateControlOptions,
                column: 0,
                colSpan: 1,
                visibilityConditions: new List<SettingVisibilityCondition>
                {
                    new("lossless", "True", negate: true)
                }
            ),
            new SettingField(
                "cpu_crf",
                "CRF / Качество",
                SettingType.Int,
                23,
                "Видео:Битрейт",
                comment: "Коэффициент постоянного качества (0-51). Меньше = лучше",
                column: 1,
                colSpan: 1,
                visibilityConditions: new List<SettingVisibilityCondition>
                {
                    new("lossless", "True", negate: true),
                    new("cpu_rc", "CRF")
                },
                minimum: 0,
                maximum: 51
            ),
            new SettingField(
                "cpu_v_bitrate",
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
                    new("cpu_rc", "Битрейт (ABR)")
                },
                minimum: 100,
                maximum: 500000
            ),
            new SettingField(
                "cpu_tune",
                "Tune",
                SettingType.Combo,
                "Нет",
                "Видео:Расширенные параметры",
                options: TuneOptions,
                column: 0,
                colSpan: 1
            ),
            new SettingField(
                "cpu_aq_mode",
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
                "cpu_lookahead",
                "RC Lookahead",
                SettingType.Int,
                20,
                "Видео:Расширенные параметры",
                comment: "Количество кадров анализа вперед (0-250). 0 - отключено, по умолчанию: 20",
                column: 0,
                colSpan: 1,
                minimum: 0,
                maximum: 250
            )
        };
    }

    public List<string> BuildEncoderArguments(Dictionary<string, object> settings, EncoderSharedContext context)
    {
        var args = new List<string>();
        args.AddRange(new[] { "-c:v", GetFfmpegCodecName(settings), "-pix_fmt", GetPixelFormat(context) });
        args.AddRange(new[] { "-preset", GetSettingValue(settings, "cpu_preset", "medium") });

        var x265Params = new List<string>();

        if (context.IsLossless)
        {
            x265Params.Add("lossless=1");
        }
        else
        {
            string cpuRc = GetSettingValue(settings, "cpu_rc", "CRF");
            if (cpuRc == "CRF")
            {
                int crf = GetSettingValue(settings, "cpu_crf", 23);
                args.AddRange(new[] { "-crf", crf.ToString() });
            }
            else
            {
                int vBr = GetSettingValue(settings, "cpu_v_bitrate", 4000);
                int maxBr = vBr * 2;
                int bufSize = maxBr * 2;
                args.AddRange(new[] {
                    "-b:v", $"{vBr}k",
                    "-maxrate", $"{maxBr}k",
                    "-bufsize", $"{bufSize}k"
                });
            }
        }

        string tune = GetSettingValue(settings, "cpu_tune", "Нет");
        if (tune != "Нет")
        {
            args.AddRange(new[] { "-tune", tune });
        }

        x265Params.Add($"aq-mode={GetSettingValue(settings, "cpu_aq_mode", "2")}");

        int cpuLa = GetSettingValue(settings, "cpu_lookahead", 20);
        if (cpuLa > 0)
        {
            int clampedLa = Math.Clamp(cpuLa, 0, 250);
            x265Params.Add($"rc-lookahead={clampedLa}");
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
