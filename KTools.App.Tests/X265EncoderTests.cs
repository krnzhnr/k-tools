// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using KTools_App.Encoders;
using KTools_App.Encoders.Capabilities;
using Moq;

namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты для проверки сборки аргументов x265, валидации границ, Clamping,
/// форматирования float независимого от локали и режима Lossless.
/// </summary>
[TestClass]
public class X265EncoderTests
{
    private X265Encoder _encoder = null!;

    [TestInitialize]
    public void Setup()
    {
        _encoder = new X265Encoder();
    }

    [TestMethod]
    public void Properties_VerifyCorrectMetadata()
    {
        _encoder.StableId.Should().Be("x265");
        _encoder.DisplayName.Should().Be("x265");
        _encoder.GetFfmpegCodecName(new Dictionary<string, object>()).Should().Be("libx265");
    }

    [TestMethod]
    public void GetPixelFormat_Force10Bit_Returns10BitFormat()
    {
        var context8Bit = new EncoderSharedContext(IsLossless: false, Force10Bit: false, ContainerExtension: ".mkv");
        var context10Bit = new EncoderSharedContext(IsLossless: false, Force10Bit: true, ContainerExtension: ".mkv");

        _encoder.GetPixelFormat(context8Bit).Should().Be("yuv420p");
        _encoder.GetPixelFormat(context10Bit).Should().Be("yuv420p10le");
    }

    [TestMethod]
    public void BuildEncoderArguments_LosslessMode_OverridesRateControlAndSetsLosslessFlag()
    {
        // Arrange: передаём и CRF, и битрейт, но вызываем в режиме Lossless = true
        var settings = new Dictionary<string, object>
        {
            { "x265_preset", "medium" },
            { "x265_rc", "CRF" },
            { "x265_crf", 18 },
            { "x265_v_bitrate", 8000 }
        };
        var context = new EncoderSharedContext(IsLossless: true, Force10Bit: false, ContainerExtension: ".mkv");

        // Act
        var args = _encoder.BuildEncoderArguments(settings, context);

        // Assert
        args.Should().Contain("-c:v");
        args.Should().Contain("libx265");
        args.Should().NotContain("-crf");
        args.Should().NotContain("-b:v");
        args.Should().Contain("-x265-params");

        int paramsIndex = args.IndexOf("-x265-params");
        string x265ParamsStr = args[paramsIndex + 1];
        x265ParamsStr.Should().Contain("lossless=1");
    }

    [TestMethod]
    public void BuildEncoderArguments_AqModeAndStrength_ClampsAndFormatsWithDot()
    {
        // Arrange: сила AQ передаётся равной 5.5f (должна ограничиться до 3.0)
        var settings = new Dictionary<string, object>
        {
            { "x265_aq_mode", "3" },
            { "x265_aq_strength", 5.5f }
        };
        var context = new EncoderSharedContext(IsLossless: false, Force10Bit: false, ContainerExtension: ".mkv");

        // Act
        var args = _encoder.BuildEncoderArguments(settings, context);

        // Assert
        int paramsIndex = args.IndexOf("-x265-params");
        string x265ParamsStr = args[paramsIndex + 1];
        x265ParamsStr.Should().Contain("aq-mode=3");
        x265ParamsStr.Should().Contain("aq-strength=3.0");
        x265ParamsStr.Should().NotContain("aq-strength=3,0"); // Проверка CultureInfo.InvariantCulture
    }

    [TestMethod]
    public void BuildEncoderArguments_LookaheadAndBframes_ClampsAndOmitsDefaults()
    {
        // Arrange
        var settings = new Dictionary<string, object>
        {
            { "x265_lookahead", 300 }, // Ограничивается до 250
            { "x265_bframes", 20 },     // Ограничивается до 16
            { "x265_b_adapt", "1" }
        };
        var context = new EncoderSharedContext(IsLossless: false, Force10Bit: false, ContainerExtension: ".mkv");

        // Act
        var args = _encoder.BuildEncoderArguments(settings, context);

        // Assert
        int paramsIndex = args.IndexOf("-x265-params");
        string x265ParamsStr = args[paramsIndex + 1];
        x265ParamsStr.Should().Contain("rc-lookahead=250");
        x265ParamsStr.Should().Contain("bframes=16");
        x265ParamsStr.Should().Contain("b-adapt=1");
    }

    [TestMethod]
    public void BuildEncoderArguments_PsyRdAndPsyRdoq_FormatsCorrectly()
    {
        // Arrange
        var settings = new Dictionary<string, object>
        {
            { "x265_psy_rd", 2.5f },
            { "x265_psy_rdoq", 15.0f }
        };
        var context = new EncoderSharedContext(IsLossless: false, Force10Bit: false, ContainerExtension: ".mkv");

        // Act
        var args = _encoder.BuildEncoderArguments(settings, context);

        // Assert
        int paramsIndex = args.IndexOf("-x265-params");
        string x265ParamsStr = args[paramsIndex + 1];
        x265ParamsStr.Should().Contain("psy-rd=2.5");
        x265ParamsStr.Should().Contain("psy-rdoq=15.0");
    }

    [TestMethod]
    public void BuildEncoderArguments_DeblockSaoFastPskip_AppliesFlags()
    {
        // Arrange
        var settings = new Dictionary<string, object>
        {
            { "x265_deblock", "-1:-1" },
            { "x265_no_sao", true },
            { "x265_no_fast_pskip", true }
        };
        var context = new EncoderSharedContext(IsLossless: false, Force10Bit: false, ContainerExtension: ".mkv");

        // Act
        var args = _encoder.BuildEncoderArguments(settings, context);

        // Assert
        int paramsIndex = args.IndexOf("-x265-params");
        string x265ParamsStr = args[paramsIndex + 1];
        x265ParamsStr.Should().Contain("deblock=-1,-1");
        x265ParamsStr.Should().Contain("no-sao=1");
        x265ParamsStr.Should().Contain("no-fast-pskip=1");
    }

    [TestMethod]
    public void GetContainerTag_Mp4Container_ReturnsHvc1()
    {
        var contextMp4 = new EncoderSharedContext(IsLossless: false, Force10Bit: false, ContainerExtension: ".mp4");
        var contextMkv = new EncoderSharedContext(IsLossless: false, Force10Bit: false, ContainerExtension: ".mkv");

        _encoder.GetContainerTag(new Dictionary<string, object>(), contextMp4).Should().Be("hvc1");
        _encoder.GetContainerTag(new Dictionary<string, object>(), contextMkv).Should().BeNull();
    }

    [TestMethod]
    public void GetEncoderSettings_BitrateFields_VerifyPresenceAndConditions()
    {
        // Act
        var settings = _encoder.GetEncoderSettings();

        // Assert
        var rcField = settings.Find(f => f.Key == "x265_rc");
        rcField.Should().NotBeNull();
        rcField!.DisableConditions.Should().NotBeNull();
        rcField.DisableConditions!.Find(c => c.Key == "lossless").Should().NotBeNull();

        var autoBrField = settings.Find(f => f.Key == "x265_auto_bitrate");
        autoBrField.Should().NotBeNull();
        autoBrField!.VisibilityConditions.Should().NotBeNull();
        autoBrField.VisibilityConditions!.Find(c => c.Key == "x265_rc" && c.Values.Contains("Битрейт (ABR)")).Should().NotBeNull();

        var minBrField = settings.Find(f => f.Key == "x265_min_bitrate");
        minBrField.Should().NotBeNull();
        minBrField!.DisableConditions.Should().NotBeNull();
        minBrField.DisableConditions!.Find(c => c.Key == "x265_auto_bitrate").Should().NotBeNull();

        var maxBrField = settings.Find(f => f.Key == "x265_max_bitrate");
        maxBrField.Should().NotBeNull();
        maxBrField!.DisableConditions.Should().NotBeNull();
        maxBrField.DisableConditions!.Find(c => c.Key == "x265_auto_bitrate").Should().NotBeNull();

        var bufField = settings.Find(f => f.Key == "x265_bufsize");
        bufField.Should().NotBeNull();
        bufField!.DisableConditions.Should().NotBeNull();
        bufField.DisableConditions!.Find(c => c.Key == "x265_auto_bitrate").Should().NotBeNull();
    }

    [TestMethod]
    public void BuildEncoderArguments_CustomBitrateParameters_PassesMinMaxBufsize()
    {
        // Arrange
        var settings = new Dictionary<string, object>
        {
            { "x265_rc", "Битрейт (ABR)" },
            { "x265_v_bitrate", 6000 },
            { "x265_auto_bitrate", false },
            { "x265_min_bitrate", 4500 },
            { "x265_max_bitrate", 9000 },
            { "x265_bufsize", 18000 }
        };
        var context = new EncoderSharedContext(IsLossless: false, Force10Bit: false, ContainerExtension: ".mkv");

        // Act
        var args = _encoder.BuildEncoderArguments(settings, context);

        // Assert
        args.Should().ContainInOrder("-b:v", "6000k");
        args.Should().ContainInOrder("-minrate", "4500k");
        args.Should().ContainInOrder("-maxrate", "9000k");
        args.Should().ContainInOrder("-bufsize", "18000k");
    }
}
