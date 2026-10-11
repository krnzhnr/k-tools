// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using KTools_App.Encoders;
using KTools_App.Encoders.Capabilities;

namespace KTools_App.Tests;

/// <summary>
/// Исчерпывающая юнит-матрица отображения настроек в аргументы FFmpeg для всех пяти
/// реализаций кодеков (libx264, libx265, hevc_nvenc, h264_nvenc, av1_nvenc): каждое дискретное
/// значение каждой опции, границы числовых диапазонов и рисковые связки — без запуска ffmpeg,
/// поэтому выполняется везде, включая CI. Реальную принимаемость команд проверяют
/// совместимостные тесты категории RequiresFfmpeg.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class EncoderArgumentMatrixTests
{
    private static readonly EncoderSharedContext Mkv = new(IsLossless: false, Force10Bit: false, ContainerExtension: ".mkv");

    // ---------- libx264: все пресеты ----------

    [TestMethod]
    [DataRow("ultrafast")]
    [DataRow("superfast")]
    [DataRow("veryfast")]
    [DataRow("faster")]
    [DataRow("fast")]
    [DataRow("medium")]
    [DataRow("slow")]
    [DataRow("slower")]
    [DataRow("veryslow")]
    [DataRow("placebo")]
    public void X264_AllPresets_MapToPresetFlag(string preset)
    {
        // Arrange
        var cap = new CpuH264Capabilities();
        var settings = new Dictionary<string, object> { { "x264_preset", preset } };

        // Act
        var args = cap.BuildCodecArguments(settings, Mkv);

        // Assert
        args.Should().ContainInOrder("-preset", preset);
    }

    // ---------- libx264: все tune и профили ----------

    [TestMethod]
    [DataRow("film")]
    [DataRow("animation")]
    [DataRow("grain")]
    [DataRow("stillimage")]
    [DataRow("fastdecode")]
    [DataRow("zerolatency")]
    [DataRow("psnr")]
    [DataRow("ssim")]
    public void X264_AllTunes_MapToTuneFlag(string tune)
    {
        // Arrange
        var cap = new CpuH264Capabilities();
        var settings = new Dictionary<string, object>
        {
            { "x264_advanced_options", true },
            { "x264_tune", tune }
        };

        // Act
        var args = cap.BuildCodecArguments(settings, Mkv);

        // Assert
        args.Should().ContainInOrder("-tune", tune);
    }

    [TestMethod]
    [DataRow("high")]
    [DataRow("main")]
    [DataRow("baseline")]
    public void X264_AllProfiles_MapToProfileFlag(string profile)
    {
        // Arrange
        var cap = new CpuH264Capabilities();
        var settings = new Dictionary<string, object>
        {
            { "x264_advanced_options", true },
            { "x264_profile", profile }
        };

        // Act
        var args = cap.BuildCodecArguments(settings, Mkv);

        // Assert
        args.Should().ContainInOrder("-profile:v", profile);
    }

    [TestMethod]
    public void X264_TuneProfileCross_AllCombos_ContainBothFlags()
    {
        // Arrange: попарная связка tune × profile — профиль может резать инструменты tune.
        var cap = new CpuH264Capabilities();
        string[] tunes = { "film", "animation", "grain", "stillimage", "fastdecode", "zerolatency", "psnr", "ssim" };
        string[] profiles = { "high", "main", "baseline" };

        // Act & Assert
        foreach (string tune in tunes)
        {
            foreach (string profile in profiles)
            {
                var settings = new Dictionary<string, object>
                {
                    { "x264_advanced_options", true },
                    { "x264_tune", tune },
                    { "x264_profile", profile }
                };
                var args = cap.BuildCodecArguments(settings, Mkv);
                args.Should().ContainInOrder(new[] { "-tune", tune }, $"связка tune={tune} + profile={profile} потеряла tune");
                args.Should().ContainInOrder(new[] { "-profile:v", profile }, $"связка tune={tune} + profile={profile} потеряла profile");
            }
        }
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(51)]
    public void X264_CrfBoundaries_PassThrough(int crf)
    {
        // Arrange
        var cap = new CpuH264Capabilities();
        var settings = new Dictionary<string, object> { { "x264_rc", "CRF" }, { "x264_crf", crf } };

        // Act
        var args = cap.BuildCodecArguments(settings, Mkv);

        // Assert
        args.Should().ContainInOrder("-crf", crf.ToString());
    }

    // ---------- libx265: все пресеты ----------

    [TestMethod]
    [DataRow("ultrafast")]
    [DataRow("superfast")]
    [DataRow("veryfast")]
    [DataRow("faster")]
    [DataRow("fast")]
    [DataRow("medium")]
    [DataRow("slow")]
    [DataRow("slower")]
    [DataRow("veryslow")]
    [DataRow("placebo")]
    public void X265_AllPresets_MapToPresetFlag(string preset)
    {
        // Arrange
        var cap = new CpuHevcCapabilities();
        var settings = new Dictionary<string, object> { { "x265_preset", preset } };

        // Act
        var args = cap.BuildCodecArguments(settings, Mkv);

        // Assert
        args.Should().ContainInOrder("-preset", preset);
    }

    // ---------- libx265: все tune, aq-mode, b-adapt, deblock ----------

    [TestMethod]
    [DataRow("grain")]
    [DataRow("animation")]
    [DataRow("fastdecode")]
    [DataRow("zerolatency")]
    [DataRow("psnr")]
    [DataRow("ssim")]
    public void X265_AllTunes_MapToTuneFlag(string tune)
    {
        // Arrange
        var cap = new CpuHevcCapabilities();
        var settings = new Dictionary<string, object>
        {
            { "x265_advanced_options", true },
            { "x265_tune", tune }
        };

        // Act
        var args = cap.BuildCodecArguments(settings, Mkv);

        // Assert
        args.Should().ContainInOrder("-tune", tune);
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("1")]
    [DataRow("2")]
    [DataRow("3")]
    [DataRow("4")]
    public void X265_AllAqModes_MapToAqModeParam(string aqMode)
    {
        // Arrange
        var cap = new CpuHevcCapabilities();
        var settings = new Dictionary<string, object>
        {
            { "x265_advanced_options", true },
            { "x265_aq_mode", aqMode }
        };

        // Act
        var args = cap.BuildCodecArguments(settings, Mkv);

        // Assert
        int idx = args.IndexOf("-x265-params");
        idx.Should().BeGreaterThanOrEqualTo(0);
        args[idx + 1].Should().Contain($"aq-mode={aqMode}");
    }

    [TestMethod]
    public void X265_AllBAdaptValues_MapCorrectly()
    {
        // Arrange
        var cap = new CpuHevcCapabilities();

        // Act & Assert: не-дефолты попадают в параметры, дефолт 2 опускается
        foreach (string bAdapt in new[] { "0", "1" })
        {
            var args = cap.BuildCodecArguments(
                new Dictionary<string, object> { { "x265_advanced_options", true }, { "x265_b_adapt", bAdapt } }, Mkv);
            int idx = args.IndexOf("-x265-params");
            idx.Should().BeGreaterThanOrEqualTo(0);
            args[idx + 1].Should().Contain($"b-adapt={bAdapt}");
        }

        var defaultArgs = cap.BuildCodecArguments(
            new Dictionary<string, object> { { "x265_advanced_options", true }, { "x265_b_adapt", "2" } }, Mkv);
        StringJoin(defaultArgs).Should().NotContain("b-adapt=", "дефолтное значение не должно дублироваться");
    }

    [TestMethod]
    [DataRow("-1:-1", "-1,-1")]
    [DataRow("-2:-2", "-2,-2")]
    [DataRow("1,1", "1,1")]
    public void X265_NonDefaultDeblocks_MapWithCommaSeparator(string setting, string expected)
    {
        // Arrange
        var cap = new CpuHevcCapabilities();
        var settings = new Dictionary<string, object>
        {
            { "x265_advanced_options", true },
            { "x265_deblock", setting }
        };

        // Act
        var args = cap.BuildCodecArguments(settings, Mkv);

        // Assert
        int idx = args.IndexOf("-x265-params");
        idx.Should().BeGreaterThanOrEqualTo(0);
        args[idx + 1].Should().Contain($"deblock={expected}");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(51)]
    public void X265_CrfBoundaries_PassThrough(int crf)
    {
        // Arrange
        var cap = new CpuHevcCapabilities();
        var settings = new Dictionary<string, object> { { "x265_rc", "CRF" }, { "x265_crf", crf } };

        // Act
        var args = cap.BuildCodecArguments(settings, Mkv);

        // Assert
        args.Should().ContainInOrder("-crf", crf.ToString());
    }

    [TestMethod]
    [DataRow(0, 0)]      // нижние границы: lookahead 0, bframes 0
    [DataRow(250, 16)]   // верхние границы: lookahead 250, bframes 16
    public void X265_LookaheadBframesBoundaries_PassThrough(int lookahead, int bframes)
    {
        // Arrange
        var cap = new CpuHevcCapabilities();
        var settings = new Dictionary<string, object>
        {
            { "x265_advanced_options", true },
            { "x265_lookahead", lookahead },
            { "x265_bframes", bframes }
        };

        // Act
        var args = cap.BuildCodecArguments(settings, Mkv);

        // Assert
        int idx = args.IndexOf("-x265-params");
        idx.Should().BeGreaterThanOrEqualTo(0);
        args[idx + 1].Should().Contain($"rc-lookahead={lookahead}");
        args[idx + 1].Should().Contain($"bframes={bframes}");
    }

    [TestMethod]
    [DataRow(0.0f, 0.0f, false)]   // оба дефолта: psy-rd отличается от внутреннего дефолта 2.0 и пишется, rdoq опускается
    [DataRow(5.0f, 50.0f, true)]   // границы: оба пишутся
    public void X265_PsyBoundaries_MapCorrectly(float psyRd, float psyRdoq, bool expectRdoq)
    {
        // Arrange
        var cap = new CpuHevcCapabilities();
        var settings = new Dictionary<string, object>
        {
            { "x265_advanced_options", true },
            { "x265_psy_rd", psyRd },
            { "x265_psy_rdoq", psyRdoq }
        };

        // Act
        var args = cap.BuildCodecArguments(settings, Mkv);

        // Assert
        string joined = StringJoin(args);
        joined.Should().Contain($"psy-rd={psyRd.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}");
        if (expectRdoq)
        {
            joined.Should().Contain($"psy-rdoq={psyRdoq.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}");
        }
        else
        {
            joined.Should().NotContain("psy-rdoq=", "дефолтное значение опускается");
        }
    }

    // ---------- NVENC: все пресеты × все три кодека ----------

    public static IEnumerable<object[]> NvencCodecPresetCases
    {
        get
        {
            string[] codecs = { "HEVC / H.265", "AVC / H.264", "AV1" };
            string[] presets = { "p1", "p2", "p3", "p4", "p5", "p6", "p7" };
            foreach (string codec in codecs)
            {
                foreach (string preset in presets)
                {
                    yield return new object[] { codec, preset };
                }
            }
        }
    }

    [TestMethod]
    [DynamicData(nameof(NvencCodecPresetCases))]
    public void Nvenc_AllCodecsAllPresets_MapToPresetFlag(string codec, string preset)
    {
        // Arrange
        var encoder = new NvencEncoder();
        var settings = NvencSettings(codec, new Dictionary<string, object> { { "nvenc_preset", preset } });

        // Act
        var args = encoder.BuildEncoderArguments(settings, Mkv);

        // Assert
        args.Should().ContainInOrder(new[] { "-preset", preset }, $"кодек {codec} потерял пресет {preset}");
        args.Should().Contain("-c:v");
    }

    // ---------- NVENC: все RC-режимы × все три кодека ----------

    public static IEnumerable<object[]> NvencCodecRcCases
    {
        get
        {
            string[] codecs = { "HEVC / H.265", "AVC / H.264", "AV1" };
            string[] rcs = { "cbr", "vbr", "constqp" };
            foreach (string codec in codecs)
            {
                foreach (string rc in rcs)
                {
                    yield return new object[] { codec, rc };
                }
            }
        }
    }

    [TestMethod]
    [DynamicData(nameof(NvencCodecRcCases))]
    public void Nvenc_AllCodecsAllRcModes_MapCorrectly(string codec, string rc)
    {
        // Arrange
        var encoder = new NvencEncoder();
        var settings = NvencSettings(codec, new Dictionary<string, object>
        {
            { "nvenc_rc", rc },
            { "v_bitrate", 3000 },
            { "v_qp", 20 }
        });

        // Act
        var args = encoder.BuildEncoderArguments(settings, Mkv);

        // Assert
        if (rc == "constqp")
        {
            args.Should().ContainInOrder("-rc", "constqp");
            args.Should().ContainInOrder("-qp", "20");
            args.Should().NotContain("-b:v", $"constqp не должен нести битрейт ({codec})");
        }
        else
        {
            args.Should().ContainInOrder("-rc", rc);
            args.Should().ContainInOrder("-b:v", "3000k");
        }
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(51)]
    public void Nvenc_QpBoundaries_PassThrough(int qp)
    {
        // Arrange: границы QP для каждого кодека NVENC
        var encoder = new NvencEncoder();

        // Act & Assert
        foreach (string codec in new[] { "HEVC / H.265", "AVC / H.264", "AV1" })
        {
            var settings = NvencSettings(codec, new Dictionary<string, object>
            {
                { "nvenc_rc", "constqp" },
                { "v_qp", qp }
            });
            var args = encoder.BuildEncoderArguments(settings, Mkv);
            args.Should().ContainInOrder(new[] { "-qp", qp.ToString() }, $"кодек {codec}, qp={qp}");
        }
    }

    // ---------- NVENC: b_ref_mode по каждому кодеку ----------

    [TestMethod]
    [DataRow("HEVC / H.265", "disabled", "disabled")]
    [DataRow("HEVC / H.265", "each", "each")]
    [DataRow("HEVC / H.265", "middle", "middle")]
    [DataRow("AVC / H.264", "disabled", "disabled")]
    [DataRow("AVC / H.264", "each", "each")]
    [DataRow("AVC / H.264", "middle", "middle")]
    [DataRow("AV1", "disabled", "disabled")]
    [DataRow("AV1", "middle", "middle")]
    [DataRow("AV1", "each", "middle")]
    public void Nvenc_BRefModes_MapPerCodec(string codec, string setting, string expected)
    {
        // Arrange: AV1 запрещает each и маппит его в middle
        var encoder = new NvencEncoder();
        var settings = NvencSettings(codec, new Dictionary<string, object> { { "nv_b_ref_mode", setting } });

        // Act
        var args = encoder.BuildEncoderArguments(settings, Mkv);

        // Assert
        args.Should().ContainInOrder("-b_ref_mode", expected);
    }

    // ---------- NVENC: AQ-переключатели и сила ----------

    [TestMethod]
    public void Nvenc_AqSwitches_MapToFlags()
    {
        // Arrange
        var encoder = new NvencEncoder();

        // Act & Assert: spatial выкл → -spatial-aq 0, иначе 1 + сила
        var offSettings = NvencSettings("HEVC / H.265", new Dictionary<string, object> { { "nv_spatial_aq", false } });
        var offArgs = encoder.BuildEncoderArguments(offSettings, Mkv);
        offArgs.Should().ContainInOrder("-spatial-aq", "0");

        var onSettings = NvencSettings("HEVC / H.265", new Dictionary<string, object>
        {
            { "nv_spatial_aq", true },
            { "nv_aq_strength", 12 }
        });
        var onArgs = encoder.BuildEncoderArguments(onSettings, Mkv);
        onArgs.Should().ContainInOrder("-spatial-aq", "1");
        onArgs.Should().ContainInOrder("-aq-strength", "12");

        var temporalOff = NvencSettings("HEVC / H.265", new Dictionary<string, object> { { "nv_temporal_aq", false } });
        encoder.BuildEncoderArguments(temporalOff, Mkv).Should().ContainInOrder("-temporal-aq", "0");
        var temporalOn = NvencSettings("HEVC / H.265", new Dictionary<string, object> { { "nv_temporal_aq", true } });
        encoder.BuildEncoderArguments(temporalOn, Mkv).Should().ContainInOrder("-temporal-aq", "1");
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(15)]
    public void Nvenc_AqStrengthBoundaries_PassThrough(int strength)
    {
        // Arrange
        var encoder = new NvencEncoder();
        var settings = NvencSettings("HEVC / H.265", new Dictionary<string, object>
        {
            { "nv_spatial_aq", true },
            { "nv_aq_strength", strength }
        });

        // Act
        var args = encoder.BuildEncoderArguments(settings, Mkv);

        // Assert
        args.Should().ContainInOrder("-aq-strength", strength.ToString());
    }

    // ---------- Все кодеки × контейнеры: теги ----------

    [TestMethod]
    [DataRow("cpu", "HEVC / H.265", ".mp4", "hvc1")]
    [DataRow("cpu", "HEVC / H.265", ".mkv", null)]
    [DataRow("cpu", "AVC / H.264", ".mp4", null)]
    [DataRow("cpu", "AVC / H.264", ".mkv", null)]
    [DataRow("nvenc", "HEVC / H.265", ".mp4", "hvc1")]
    [DataRow("nvenc", "HEVC / H.265", ".mkv", null)]
    [DataRow("nvenc", "AVC / H.264", ".mp4", null)]
    [DataRow("nvenc", "AV1", ".mp4", null)]
    [DataRow("nvenc", "AV1", ".mkv", null)]
    public void AllCodecs_ContainerTags_MapCorrectly(string encoderId, string codec, string container, string? expectedTag)
    {
        // Arrange
        IVideoEncoder encoder = encoderId == "cpu" ? new CpuEncoder() : new NvencEncoder();
        string codecKey = encoderId == "cpu" ? CpuEncoder.CodecSettingKey : "nvenc_codec";
        var settings = new Dictionary<string, object> { { codecKey, codec } };
        var context = new EncoderSharedContext(IsLossless: false, Force10Bit: false, ContainerExtension: container);

        // Act
        string? tag = encoder.GetContainerTag(settings, context);

        // Assert
        tag.Should().Be(expectedTag, $"энкодер {encoderId}, кодек {codec}, контейнер {container}");
    }

    // ---------- Все кодеки × 10-бит: pix_fmt ----------

    [TestMethod]
    [DataRow("HEVC / H.265", true, "yuv420p10le")]
    [DataRow("HEVC / H.265", false, "yuv420p")]
    [DataRow("AVC / H.264", true, "yuv420p")]
    [DataRow("AVC / H.264", false, "yuv420p")]
    public void Cpu_AllCodecsForce10Bit_MapPixelFormat(string codec, bool force10Bit, string expectedPixFmt)
    {
        // Arrange
        var encoder = new CpuEncoder();
        var settings = new Dictionary<string, object> { { CpuEncoder.CodecSettingKey, codec } };
        var context = new EncoderSharedContext(IsLossless: false, Force10Bit: force10Bit, ContainerExtension: ".mkv");

        // Act
        var args = encoder.BuildEncoderArguments(settings, context);

        // Assert
        args.Should().ContainInOrder("-pix_fmt", expectedPixFmt);
    }

    [TestMethod]
    [DataRow("HEVC / H.265", true, "p010le")]
    [DataRow("HEVC / H.265", false, "yuv420p")]
    [DataRow("AVC / H.264", true, "yuv420p")]
    [DataRow("AVC / H.264", false, "yuv420p")]
    [DataRow("AV1", true, "p010le")]
    [DataRow("AV1", false, "yuv420p")]
    public void Nvenc_AllCodecsForce10Bit_MapPixelFormat(string codec, bool force10Bit, string expectedPixFmt)
    {
        // Arrange
        var encoder = new NvencEncoder();
        var settings = NvencSettings(codec, new Dictionary<string, object>());

        // Act
        var args = encoder.BuildEncoderArguments(
            settings,
            new EncoderSharedContext(IsLossless: false, Force10Bit: force10Bit, ContainerExtension: ".mkv"));

        // Assert
        args.Should().ContainInOrder(new[] { "-pix_fmt", expectedPixFmt }, $"кодек {codec}, force10Bit={force10Bit}");
    }

    // ---------- Все кодеки: lossless-флаги ----------

    [TestMethod]
    [DataRow("cpu", "HEVC / H.265", "libx265")]
    [DataRow("cpu", "AVC / H.264", "libx264")]
    [DataRow("nvenc", "HEVC / H.265", "hevc_nvenc")]
    [DataRow("nvenc", "AVC / H.264", "h264_nvenc")]
    public void AllCodecs_Lossless_UsesCodecAndNoBitrate(string encoderId, string codec, string ffmpegName)
    {
        // Arrange
        IVideoEncoder encoder = encoderId == "cpu" ? new CpuEncoder() : new NvencEncoder();
        string codecKey = encoderId == "cpu" ? CpuEncoder.CodecSettingKey : "nvenc_codec";
        var settings = new Dictionary<string, object> { { codecKey, codec } };
        var context = new EncoderSharedContext(IsLossless: true, Force10Bit: false, ContainerExtension: ".mkv");

        // Act
        var args = encoder.BuildEncoderArguments(settings, context);

        // Assert
        args.Should().ContainInOrder("-c:v", ffmpegName);
        args.Should().NotContain("-b:v", $"lossless не должен нести битрейт ({ffmpegName})");
        args.Should().NotContain("-crf", $"lossless не должен нести CRF ({ffmpegName})");
    }

    [TestMethod]
    public void NvencAv1_Lossless_ThrowsClearError()
    {
        // Arrange: реальный FFmpeg отклоняет tune lossless для av1_nvenc (-542398533),
        // поэтому вместо криптичной ошибки кодирования — понятное исключение на старте.
        var encoder = new NvencEncoder();
        var settings = NvencSettings("AV1", new Dictionary<string, object>());
        var context = new EncoderSharedContext(IsLossless: true, Force10Bit: false, ContainerExtension: ".mkv");

        // Act
        Action act = () => encoder.BuildEncoderArguments(settings, context);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Lossless*AV1*");
    }

    [TestMethod]
    public void Nvenc_CapabilityLosslessFlags_MatchHardwareReality()
    {
        // Arrange & Act & Assert
        new NvencHevcCapabilities().SupportsLossless.Should().BeTrue();
        new NvencH264Capabilities().SupportsLossless.Should().BeTrue();
        new NvencAv1Capabilities().SupportsLossless.Should().BeFalse("av1_nvenc падает на tune lossless");
    }

    private static Dictionary<string, object> NvencSettings(string codec, Dictionary<string, object> extra)
    {
        var settings = new Dictionary<string, object> { { "nvenc_codec", codec } };
        foreach (var kv in extra)
        {
            settings[kv.Key] = kv.Value;
        }
        return settings;
    }

    private static string StringJoin(List<string> args) => string.Join(" ", args);
}
