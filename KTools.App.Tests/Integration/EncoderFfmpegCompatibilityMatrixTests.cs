// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using KTools_App.Encoders;

namespace KTools_App.Tests.Integration;

/// <summary>
/// Полная совместимостная матрица на реальном FFmpeg (вторая часть класса
/// <see cref="EncoderFfmpegCompatibilityTests"/>): исчерпывающие прогоны каждого дискретного
/// значения каждой опции всех пяти кодеков, границы диапазонов и рисковые связки.
/// Категория RequiresFfmpeg: без FFmpeg/GPU кейсы завершаются Inconclusive.
/// Все комментарии написаны на русском языке.
/// </summary>
public partial class EncoderFfmpegCompatibilityTests
{
    // ================= libx264: пресеты ×10 =================

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
    public void EncodeMatrix_CpuH264Preset_ProducesH264(string preset)
    {
        // Arrange
        var settings = CpuSettings("AVC / H.264", new Dictionary<string, object>
        {
            { "x264_preset", preset },
            { "x264_rc", "CRF" },
            { "x264_crf", 23 }
        });

        // Act
        var (run, output, command) = EncodeWithCpu(settings, Ctx(".mkv"), ".mkv", $"m_x264_{preset}", timeoutMs: 300000);

        // Assert
        ShouldProduceDecodableVideo(run, output, command, expectedCodec: "h264", expectedPixFmt: "yuv420p");
    }

    // ================= libx264: tune ×9, profile ×4, кросс 36 =================

    [TestMethod]
    [DataRow("film")]
    [DataRow("animation")]
    [DataRow("grain")]
    [DataRow("stillimage")]
    [DataRow("fastdecode")]
    [DataRow("zerolatency")]
    [DataRow("psnr")]
    [DataRow("ssim")]
    public void EncodeMatrix_CpuH264Tune_Accepted(string tune)
    {
        // Arrange
        var settings = CpuSettings("AVC / H.264", new Dictionary<string, object>
        {
            { "x264_preset", "veryfast" },
            { "x264_rc", "CRF" },
            { "x264_crf", 23 },
            { "x264_advanced_options", true },
            { "x264_tune", tune }
        });

        // Act
        var (run, output, command) = EncodeWithCpu(settings, Ctx(".mkv"), ".mkv", $"m_x264t_{tune}");

        // Assert
        ShouldProduceDecodableVideo(run, output, command, expectedCodec: "h264", expectedPixFmt: "yuv420p");
    }

    [TestMethod]
    [DataRow("high")]
    [DataRow("main")]
    [DataRow("baseline")]
    public void EncodeMatrix_CpuH264Profile_Accepted(string profile)
    {
        // Arrange
        var settings = CpuSettings("AVC / H.264", new Dictionary<string, object>
        {
            { "x264_preset", "veryfast" },
            { "x264_rc", "CRF" },
            { "x264_crf", 23 },
            { "x264_advanced_options", true },
            { "x264_profile", profile }
        });

        // Act
        var (run, output, command) = EncodeWithCpu(settings, Ctx(".mkv"), ".mkv", $"m_x264p_{profile}");

        // Assert
        ShouldProduceDecodableVideo(run, output, command, expectedCodec: "h264", expectedPixFmt: "yuv420p");
    }

    [TestMethod]
    public void EncodeMatrix_CpuH264TuneProfileCross_AllCombosProduceH264()
    {
        // Arrange: полный кросс tune × profile — профиль может резать инструменты tune.
        string[] tunes = { "film", "animation", "grain", "stillimage", "fastdecode", "zerolatency", "psnr", "ssim" };
        string[] profiles = { "high", "main", "baseline" };

        // Act & Assert
        foreach (string tune in tunes)
        {
            foreach (string profile in profiles)
            {
                var settings = CpuSettings("AVC / H.264", new Dictionary<string, object>
                {
                    { "x264_preset", "veryfast" },
                    { "x264_rc", "CRF" },
                    { "x264_crf", 23 },
                    { "x264_advanced_options", true },
                    { "x264_tune", tune },
                    { "x264_profile", profile }
                });
                var (run, output, command) = EncodeWithCpu(
                    settings, Ctx(".mkv"), ".mkv", $"m_x264x_{tune}_{profile}");
                ShouldProduceDecodableVideo(run, output, command,
                    expectedCodec: "h264", expectedPixFmt: "yuv420p");
            }
        }
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(51)]
    public void EncodeMatrix_CpuH264CrfBoundaries_ProduceH264(int crf)
    {
        // Arrange
        var settings = CpuSettings("AVC / H.264", new Dictionary<string, object>
        {
            { "x264_preset", "veryfast" },
            { "x264_rc", "CRF" },
            { "x264_crf", crf }
        });

        // Act
        var (run, output, command) = EncodeWithCpu(settings, Ctx(".mkv"), ".mkv", $"m_x264crf_{crf}");

        // Assert
        ShouldProduceDecodableVideo(run, output, command, expectedCodec: "h264", expectedPixFmt: "yuv420p");
    }

    [TestMethod]
    public void EncodeMatrix_CpuH264AbrCustomBounds_ProduceH264()
    {
        // Arrange
        var settings = CpuSettings("AVC / H.264", new Dictionary<string, object>
        {
            { "x264_preset", "veryfast" },
            { "x264_rc", "Битрейт (ABR)" },
            { "x264_v_bitrate", 1500 },
            { "x264_auto_bitrate", false },
            { "x264_min_bitrate", 1000 },
            { "x264_max_bitrate", 2500 },
            { "x264_bufsize", 5000 }
        });

        // Act
        var (run, output, command) = EncodeWithCpu(settings, Ctx(".mkv"), ".mkv", "m_x264abr_c");

        // Assert
        ShouldProduceDecodableVideo(run, output, command, expectedCodec: "h264", expectedPixFmt: "yuv420p");
    }

    // ================= libx265: пресеты ×10 =================

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
    public void EncodeMatrix_CpuH265Preset_ProducesHevc(string preset)
    {
        // Arrange
        var settings = CpuSettings("HEVC / H.265", new Dictionary<string, object>
        {
            { "x265_preset", preset },
            { "x265_rc", "CRF" },
            { "x265_crf", 28 }
        });

        // Act
        var (run, output, command) = EncodeWithCpu(settings, Ctx(".mkv"), ".mkv", $"m_x265_{preset}", timeoutMs: 300000);

        // Assert
        ShouldProduceDecodableVideo(run, output, command, expectedCodec: "hevc", expectedPixFmt: "yuv420p");
    }

    // ================= libx265: tune, aq-mode, b-adapt, deblock =================

    [TestMethod]
    [DataRow("grain")]
    [DataRow("animation")]
    [DataRow("fastdecode")]
    [DataRow("zerolatency")]
    [DataRow("psnr")]
    [DataRow("ssim")]
    public void EncodeMatrix_CpuH265Tune_Accepted(string tune)
    {
        // Arrange
        var settings = CpuSettings("HEVC / H.265", new Dictionary<string, object>
        {
            { "x265_preset", "veryfast" },
            { "x265_rc", "CRF" },
            { "x265_crf", 28 },
            { "x265_advanced_options", true },
            { "x265_tune", tune }
        });

        // Act
        var (run, output, command) = EncodeWithCpu(settings, Ctx(".mkv"), ".mkv", $"m_x265t_{tune}");

        // Assert
        ShouldProduceDecodableVideo(run, output, command, expectedCodec: "hevc", expectedPixFmt: "yuv420p");
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("1")]
    [DataRow("2")]
    [DataRow("3")]
    [DataRow("4")]
    public void EncodeMatrix_CpuH265AqMode_Accepted(string aqMode)
    {
        // Arrange
        var settings = CpuSettings("HEVC / H.265", new Dictionary<string, object>
        {
            { "x265_preset", "veryfast" },
            { "x265_rc", "CRF" },
            { "x265_crf", 28 },
            { "x265_advanced_options", true },
            { "x265_aq_mode", aqMode }
        });

        // Act
        var (run, output, command) = EncodeWithCpu(settings, Ctx(".mkv"), ".mkv", $"m_x265aq_{aqMode}");

        // Assert
        ShouldProduceDecodableVideo(run, output, command, expectedCodec: "hevc", expectedPixFmt: "yuv420p");
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("1")]
    [DataRow("2")]
    public void EncodeMatrix_CpuH265BAdapt_Accepted(string bAdapt)
    {
        // Arrange
        var settings = CpuSettings("HEVC / H.265", new Dictionary<string, object>
        {
            { "x265_preset", "veryfast" },
            { "x265_rc", "CRF" },
            { "x265_crf", 28 },
            { "x265_advanced_options", true },
            { "x265_b_adapt", bAdapt }
        });

        // Act
        var (run, output, command) = EncodeWithCpu(settings, Ctx(".mkv"), ".mkv", $"m_x265ba_{bAdapt}");

        // Assert
        ShouldProduceDecodableVideo(run, output, command, expectedCodec: "hevc", expectedPixFmt: "yuv420p");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(51)]
    public void EncodeMatrix_CpuH265CrfBoundaries_ProduceHevc(int crf)
    {
        // Arrange
        var settings = CpuSettings("HEVC / H.265", new Dictionary<string, object>
        {
            { "x265_preset", "veryfast" },
            { "x265_rc", "CRF" },
            { "x265_crf", crf }
        });

        // Act
        var (run, output, command) = EncodeWithCpu(settings, Ctx(".mkv"), ".mkv", $"m_x265crf_{crf}");

        // Assert
        ShouldProduceDecodableVideo(run, output, command, expectedCodec: "hevc", expectedPixFmt: "yuv420p");
    }

    [TestMethod]
    public void EncodeMatrix_CpuH265AbrCustomBounds_ProduceHevc()
    {
        // Arrange
        var settings = CpuSettings("HEVC / H.265", new Dictionary<string, object>
        {
            { "x265_preset", "veryfast" },
            { "x265_rc", "Битрейт (ABR)" },
            { "x265_v_bitrate", 1500 },
            { "x265_auto_bitrate", false },
            { "x265_min_bitrate", 1000 },
            { "x265_max_bitrate", 2500 },
            { "x265_bufsize", 5000 }
        });

        // Act
        var (run, output, command) = EncodeWithCpu(settings, Ctx(".mkv"), ".mkv", "m_x265abr_c");

        // Assert
        ShouldProduceDecodableVideo(run, output, command, expectedCodec: "hevc", expectedPixFmt: "yuv420p");
    }

    [TestMethod]
    public void EncodeMatrix_CpuH265Force10Bit_Produces10BitHevc()
    {
        // Arrange: программный 10-бит — формат yuv420p10le (не p010le, как у NVENC).
        var settings = CpuSettings("HEVC / H.265", new Dictionary<string, object>
        {
            { "x265_preset", "veryfast" },
            { "x265_rc", "CRF" },
            { "x265_crf", 28 }
        });

        // Act
        var (run, output, command) = EncodeWithCpu(settings, Ctx(".mkv", force10Bit: true), ".mkv", "m_x265_10bit");

        // Assert
        ShouldProduceDecodableVideo(run, output, command, expectedCodec: "hevc", expectedPixFmt: "yuv420p10le");
    }

    [TestMethod]
    public void EncodeMatrix_CpuH265Mp4Crf_ProducesHevcWithHvc1()
    {
        // Arrange
        var settings = CpuSettings("HEVC / H.265", new Dictionary<string, object>
        {
            { "x265_preset", "veryfast" },
            { "x265_rc", "CRF" },
            { "x265_crf", 28 }
        });

        // Act
        var (run, output, command) = EncodeWithCpu(settings, Ctx(".mp4"), ".mp4", "m_x265_mp4");

        // Assert
        command.Should().Contain("hvc1");
        ShouldProduceDecodableVideo(run, output, command, expectedCodec: "hevc", expectedPixFmt: "yuv420p");
    }

    // ================= NVENC: пресеты p1-p7 × 3 кодека =================

    [TestMethod]
    [DataRow("HEVC / H.265", "hevc_nvenc", "hevc", "p010le")]
    [DataRow("AVC / H.264", "h264_nvenc", "h264", "yuv420p")]
    [DataRow("AV1", "av1_nvenc", "av1", "p010le")]
    public void EncodeMatrix_NvencForce10Bit_MapsPerCodec(string codec, string ffmpegName, string expectedCodec, string expectedPix)
    {
        // Arrange
        RequireNvencListed(ffmpegName);
        var settings = NvencBase(codec);

        // Act
        var (run, output, command) = EncodeWithNvenc(settings, Ctx(".mkv", force10Bit: true), ".mkv", $"m_nv10_{ffmpegName}");

        // Assert: ffprobe софтверно декодирует аппаратный P010 как yuv420p10le.
        AssertNvencSuccessOrMissingGpu(run, command);
        ShouldProduceDecodableVideo(run, output, command, expectedCodec, expectedPix, alternatePixFmt: "yuv420p10le");
    }

    [TestMethod]
    [DataRow("HEVC / H.265", "hevc_nvenc", "hevc", "p1")]
    [DataRow("HEVC / H.265", "hevc_nvenc", "hevc", "p2")]
    [DataRow("HEVC / H.265", "hevc_nvenc", "hevc", "p3")]
    [DataRow("HEVC / H.265", "hevc_nvenc", "hevc", "p4")]
    [DataRow("HEVC / H.265", "hevc_nvenc", "hevc", "p5")]
    [DataRow("HEVC / H.265", "hevc_nvenc", "hevc", "p6")]
    [DataRow("HEVC / H.265", "hevc_nvenc", "hevc", "p7")]
    [DataRow("AVC / H.264", "h264_nvenc", "h264", "p1")]
    [DataRow("AVC / H.264", "h264_nvenc", "h264", "p2")]
    [DataRow("AVC / H.264", "h264_nvenc", "h264", "p3")]
    [DataRow("AVC / H.264", "h264_nvenc", "h264", "p4")]
    [DataRow("AVC / H.264", "h264_nvenc", "h264", "p5")]
    [DataRow("AVC / H.264", "h264_nvenc", "h264", "p6")]
    [DataRow("AVC / H.264", "h264_nvenc", "h264", "p7")]
    [DataRow("AV1", "av1_nvenc", "av1", "p1")]
    [DataRow("AV1", "av1_nvenc", "av1", "p2")]
    [DataRow("AV1", "av1_nvenc", "av1", "p3")]
    [DataRow("AV1", "av1_nvenc", "av1", "p4")]
    [DataRow("AV1", "av1_nvenc", "av1", "p5")]
    [DataRow("AV1", "av1_nvenc", "av1", "p6")]
    [DataRow("AV1", "av1_nvenc", "av1", "p7")]
    public void EncodeMatrix_NvencPreset_AllCodecsProduceVideo(string codec, string ffmpegName, string expectedCodec, string preset)
    {
        // Arrange
        RequireNvencListed(ffmpegName);
        var settings = NvencBase(codec, preset);

        // Act
        var (run, output, command) = EncodeWithNvenc(settings, Ctx(".mkv"), ".mkv", $"m_nv_{ffmpegName}_{preset}");

        // Assert
        AssertNvencSuccessOrMissingGpu(run, command);
        ShouldProduceDecodableVideo(run, output, command, expectedCodec, "yuv420p");
    }

    // ================= NVENC: RC-режимы × 3 кодека =================

    [TestMethod]
    [DataRow("HEVC / H.265", "hevc_nvenc", "hevc", "cbr")]
    [DataRow("HEVC / H.265", "hevc_nvenc", "hevc", "vbr")]
    [DataRow("HEVC / H.265", "hevc_nvenc", "hevc", "constqp")]
    [DataRow("AVC / H.264", "h264_nvenc", "h264", "cbr")]
    [DataRow("AVC / H.264", "h264_nvenc", "h264", "vbr")]
    [DataRow("AVC / H.264", "h264_nvenc", "h264", "constqp")]
    [DataRow("AV1", "av1_nvenc", "av1", "cbr")]
    [DataRow("AV1", "av1_nvenc", "av1", "vbr")]
    [DataRow("AV1", "av1_nvenc", "av1", "constqp")]
    public void EncodeMatrix_NvencRc_AllCodecsProduceVideo(string codec, string ffmpegName, string expectedCodec, string rc)
    {
        // Arrange
        RequireNvencListed(ffmpegName);
        var settings = NvencBase(codec);
        settings["nvenc_rc"] = rc;
        settings["v_bitrate"] = 2000;
        settings["v_qp"] = 20;

        // Act
        var (run, output, command) = EncodeWithNvenc(settings, Ctx(".mkv"), ".mkv", $"m_nvrc_{ffmpegName}_{rc}");

        // Assert
        AssertNvencSuccessOrMissingGpu(run, command);
        ShouldProduceDecodableVideo(run, output, command, expectedCodec, "yuv420p");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(51)]
    public void EncodeMatrix_NvencQpBoundaries_ProduceHevc(int qp)
    {
        // Arrange
        RequireNvencListed("hevc_nvenc");
        var settings = NvencBase("HEVC / H.265");
        settings["nvenc_rc"] = "constqp";
        settings["v_qp"] = qp;

        // Act
        var (run, output, command) = EncodeWithNvenc(settings, Ctx(".mkv"), ".mkv", $"m_nvqp_{qp}");

        // Assert
        AssertNvencSuccessOrMissingGpu(run, command);
        ShouldProduceDecodableVideo(run, output, command, expectedCodec: "hevc", expectedPixFmt: "yuv420p");
    }

    [TestMethod]
    public void EncodeMatrix_NvencAbrCustomBounds_ProduceHevc()
    {
        // Arrange
        RequireNvencListed("hevc_nvenc");
        var settings = NvencBase("HEVC / H.265");
        settings["nvenc_rc"] = "vbr";
        settings["v_bitrate"] = 2000;
        settings["auto_bitrate"] = false;
        settings["min_bitrate"] = 1500;
        settings["max_bitrate"] = 3500;
        settings["bufsize"] = 7000;

        // Act
        var (run, output, command) = EncodeWithNvenc(settings, Ctx(".mkv"), ".mkv", "m_nvabr_c");

        // Assert
        AssertNvencSuccessOrMissingGpu(run, command);
        ShouldProduceDecodableVideo(run, output, command, expectedCodec: "hevc", expectedPixFmt: "yuv420p");
    }

    // ================= NVENC: b_ref_mode по каждому кодеку =================

    [TestMethod]
    [DataRow("HEVC / H.265", "hevc_nvenc", "hevc", "disabled")]
    [DataRow("HEVC / H.265", "hevc_nvenc", "hevc", "each")]
    [DataRow("HEVC / H.265", "hevc_nvenc", "hevc", "middle")]
    [DataRow("AVC / H.264", "h264_nvenc", "h264", "disabled")]
    [DataRow("AVC / H.264", "h264_nvenc", "h264", "each")]
    [DataRow("AVC / H.264", "h264_nvenc", "h264", "middle")]
    [DataRow("AV1", "av1_nvenc", "av1", "disabled")]
    [DataRow("AV1", "av1_nvenc", "av1", "middle")]
    [DataRow("AV1", "av1_nvenc", "av1", "each")]
    public void EncodeMatrix_NvencBRef_AllCodecsProduceVideo(string codec, string ffmpegName, string expectedCodec, string bRef)
    {
        // Arrange: each для AV1 маппится приложением в middle.
        RequireNvencListed(ffmpegName);
        var settings = NvencBase(codec);
        settings["nv_b_ref_mode"] = bRef;

        // Act
        var (run, output, command) = EncodeWithNvenc(settings, Ctx(".mkv"), ".mkv", $"m_nvb_{ffmpegName}_{bRef}");

        // Assert
        if (codec == "AV1" && bRef == "each")
        {
            command.Should().Contain("-b_ref_mode middle");
        }
        AssertNvencSuccessOrMissingGpu(run, command);
        ShouldProduceDecodableVideo(run, output, command, expectedCodec, "yuv420p");
    }

    // ================= NVENC: AQ, lossless, контейнеры =================

    [TestMethod]
    [DataRow("spatial_off", false, true, 8)]
    [DataRow("strength_max", true, true, 15)]
    [DataRow("temporal_off", true, false, 8)]
    public void EncodeMatrix_NvencAqVariants_ProduceHevc(string name, bool spatial, bool temporal, int strength)
    {
        // Arrange
        RequireNvencListed("hevc_nvenc");
        var settings = NvencBase("HEVC / H.265");
        settings["nv_spatial_aq"] = spatial;
        settings["nv_temporal_aq"] = temporal;
        settings["nv_aq_strength"] = strength;

        // Act
        var (run, output, command) = EncodeWithNvenc(settings, Ctx(".mkv"), ".mkv", $"m_nvaq_{name}");

        // Assert
        AssertNvencSuccessOrMissingGpu(run, command);
        ShouldProduceDecodableVideo(run, output, command, expectedCodec: "hevc", expectedPixFmt: "yuv420p");
    }

    [TestMethod]
    [DataRow("HEVC / H.265", "hevc_nvenc", "hevc")]
    [DataRow("AVC / H.264", "h264_nvenc", "h264")]
    public void EncodeMatrix_NvencLossless_AllCodecsProduceVideo(string codec, string ffmpegName, string expectedCodec)
    {
        // Arrange: AV1 здесь нет — tune lossless для av1_nvenc отклоняется реальным FFmpeg,
        // поэтому сборка аргументов бросает понятное исключение (покрыто юнит-матрицей).
        RequireNvencListed(ffmpegName);
        var settings = NvencBase(codec);

        // Act
        var (run, output, command) = EncodeWithNvenc(settings, Ctx(".mkv", lossless: true), ".mkv", $"m_nvl_{ffmpegName}");

        // Assert
        AssertNvencSuccessOrMissingGpu(run, command);
        ShouldProduceDecodableVideo(run, output, command, expectedCodec, "yuv420p");
    }

    [TestMethod]
    [DataRow("HEVC / H.265", "hevc_nvenc", "hevc", true)]
    [DataRow("AVC / H.264", "h264_nvenc", "h264", false)]
    [DataRow("AV1", "av1_nvenc", "av1", false)]
    public void EncodeMatrix_NvencMp4_ContainerTagsCorrect(string codec, string ffmpegName, string expectedCodec, bool expectHvc1)
    {
        // Arrange
        RequireNvencListed(ffmpegName);
        var settings = NvencBase(codec);

        // Act
        var (run, output, command) = EncodeWithNvenc(settings, Ctx(".mp4"), ".mp4", $"m_nvmp4_{ffmpegName}");

        // Assert
        if (expectHvc1)
        {
            command.Should().Contain("hvc1");
        }
        else
        {
            command.Should().NotContain("-tag:v");
        }
        AssertNvencSuccessOrMissingGpu(run, command);
        ShouldProduceDecodableVideo(run, output, command, expectedCodec, "yuv420p");
    }

    private static Dictionary<string, object> NvencBase(string codec, string preset = "p1")
    {
        return new Dictionary<string, object>
        {
            { "nvenc_codec", codec },
            { "nvenc_preset", preset },
            { "nvenc_rc", "vbr" },
            { "v_bitrate", 2000 },
            { "auto_bitrate", true }
        };
    }

    private static void RequireNvencListed(string ffmpegName)
    {
        if (!FfmpegTestHost.IsEncoderListed(_ffmpeg, ffmpegName))
        {
            Assert.Inconclusive($"Сборка FFmpeg без {ffmpegName}: тест пропущен.");
        }
    }
}
