// -*- coding: utf-8 -*-
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Encoders;
using KTools_App.Encoders.Capabilities;

namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты программного кодека AVC / H.264 (libx264): метаданные, режимы CRF/ABR/Lossless,
/// tune/profile под тумблером расширенных параметров, отсутствие тега контейнера.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class CpuH264CapabilitiesTests
{
    private CpuH264Capabilities _cap = null!;
    private EncoderSharedContext _contextMkv = null!;

    [TestInitialize]
    public void Setup()
    {
        _cap = new CpuH264Capabilities();
        _contextMkv = new EncoderSharedContext(IsLossless: false, Force10Bit: false, ContainerExtension: ".mkv");
    }

    [TestMethod]
    public void Properties_VerifyCorrectMetadata()
    {
        _cap.CodecName.Should().Be("AVC / H.264");
        _cap.FfmpegCodecName.Should().Be("libx264");
        _cap.Supports10Bit.Should().BeFalse();
    }

    [TestMethod]
    public void GetPixelFormat_AnyMode_AlwaysReturns8Bit()
    {
        // 10-бит Hi10P осознанно не поддерживается из-за невоспроизводимости на большинстве устройств
        _cap.GetPixelFormat(force10Bit: false).Should().Be("yuv420p");
        _cap.GetPixelFormat(force10Bit: true).Should().Be("yuv420p");
    }

    [TestMethod]
    public void BuildCodecArguments_CrfMode_GeneratesCorrectArgs()
    {
        // Arrange
        var settings = new Dictionary<string, object>
        {
            { "x264_preset", "medium" },
            { "x264_rc", "CRF" },
            { "x264_crf", 21 }
        };

        // Act
        var args = _cap.BuildCodecArguments(settings, _contextMkv);

        // Assert
        args.Should().ContainInOrder("-c:v", "libx264");
        args.Should().ContainInOrder("-pix_fmt", "yuv420p");
        args.Should().ContainInOrder("-preset", "medium");
        args.Should().ContainInOrder("-crf", "21");
        args.Should().NotContain("-b:v");
    }

    [TestMethod]
    public void BuildCodecArguments_Force10Bit_FallsBackToYuv420p()
    {
        // Arrange
        var settings = new Dictionary<string, object>();
        var context = new EncoderSharedContext(IsLossless: false, Force10Bit: true, ContainerExtension: ".mp4");

        // Act
        var args = _cap.BuildCodecArguments(settings, context);

        // Assert
        args.Should().ContainInOrder("-pix_fmt", "yuv420p");
        args.Should().NotContain("yuv420p10le");
    }

    [TestMethod]
    public void BuildCodecArguments_LosslessMode_UsesQp0WithoutRateControl()
    {
        // Arrange
        var settings = new Dictionary<string, object>
        {
            { "x264_preset", "medium" },
            { "x264_rc", "CRF" },
            { "x264_crf", 18 }
        };
        var context = new EncoderSharedContext(IsLossless: true, Force10Bit: false, ContainerExtension: ".mkv");

        // Act
        var args = _cap.BuildCodecArguments(settings, context);

        // Assert
        args.Should().ContainInOrder("-qp", "0");
        args.Should().NotContain("-crf");
        args.Should().NotContain("-b:v");
    }

    [TestMethod]
    public void BuildCodecArguments_AutoBitrate_CalculatesMinMaxBufsize()
    {
        // Arrange
        var settings = new Dictionary<string, object>
        {
            { "x264_rc", "Битрейт (ABR)" },
            { "x264_v_bitrate", 5000 },
            { "x264_auto_bitrate", true }
        };

        // Act
        var args = _cap.BuildCodecArguments(settings, _contextMkv);

        // Assert: min = цель, max = цель*2, буфер = max*2
        args.Should().ContainInOrder("-b:v", "5000k");
        args.Should().ContainInOrder("-minrate", "5000k");
        args.Should().ContainInOrder("-maxrate", "10000k");
        args.Should().ContainInOrder("-bufsize", "20000k");
    }

    [TestMethod]
    public void BuildCodecArguments_CustomBitrateParameters_PassesMinMaxBufsize()
    {
        // Arrange
        var settings = new Dictionary<string, object>
        {
            { "x264_rc", "Битрейт (ABR)" },
            { "x264_v_bitrate", 6000 },
            { "x264_auto_bitrate", false },
            { "x264_min_bitrate", 4500 },
            { "x264_max_bitrate", 9000 },
            { "x264_bufsize", 18000 }
        };

        // Act
        var args = _cap.BuildCodecArguments(settings, _contextMkv);

        // Assert
        args.Should().ContainInOrder("-b:v", "6000k");
        args.Should().ContainInOrder("-minrate", "4500k");
        args.Should().ContainInOrder("-maxrate", "9000k");
        args.Should().ContainInOrder("-bufsize", "18000k");
    }

    [TestMethod]
    public void BuildCodecArguments_AdvancedOptionsDisabled_OmitsTuneAndProfile()
    {
        // Arrange
        var settings = new Dictionary<string, object>
        {
            { "x264_advanced_options", false },
            { "x264_tune", "film" },
            { "x264_profile", "high" }
        };

        // Act
        var args = _cap.BuildCodecArguments(settings, _contextMkv);

        // Assert
        args.Should().NotContain("-tune");
        args.Should().NotContain("-profile:v");
    }

    [TestMethod]
    public void BuildCodecArguments_AdvancedOptionsEnabled_AppliesTuneAndProfile()
    {
        // Arrange
        var settings = new Dictionary<string, object>
        {
            { "x264_advanced_options", true },
            { "x264_tune", "film" },
            { "x264_profile", "high" }
        };

        // Act
        var args = _cap.BuildCodecArguments(settings, _contextMkv);

        // Assert
        args.Should().ContainInOrder("-tune", "film");
        args.Should().ContainInOrder("-profile:v", "high");
    }

    [TestMethod]
    public void GetContainerTag_AnyContainer_ReturnsNull()
    {
        var contextMp4 = new EncoderSharedContext(IsLossless: false, Force10Bit: false, ContainerExtension: ".mp4");

        _cap.GetContainerTag(new Dictionary<string, object>(), contextMp4).Should().BeNull("H.264 не должен использовать тег hvc1");
        _cap.GetContainerTag(new Dictionary<string, object>(), _contextMkv).Should().BeNull();
    }

    [TestMethod]
    public void GetCodecSettings_BitrateFields_ReferenceOnlyOwnAutoBitrateKey()
    {
        // Act
        var settings = _cap.GetCodecSettings();

        // Assert
        settings.Find(f => f.Key == "x264_preset").Should().NotBeNull();
        settings.Find(f => f.Key == "x264_rc").Should().NotBeNull();
        settings.Find(f => f.Key == "x264_auto_bitrate").Should().NotBeNull();

        foreach (var key in new[] { "x264_min_bitrate", "x264_max_bitrate", "x264_bufsize" })
        {
            var field = settings.Find(f => f.Key == key);
            field.Should().NotBeNull($"Поле '{key}' обязано существовать");
            field!.DisableConditions.Should().NotBeNull();
            field.DisableConditions!.Find(c => c.Key == "x264_auto_bitrate").Should().NotBeNull();
            field.DisableConditions.Should().OnlyContain(c => c.Key == "x264_auto_bitrate" || c.Key == "lossless");
        }

        var expander = settings.Find(f => f.Key == "x264_advanced_options");
        expander.Should().NotBeNull();
        expander!.Type.Should().Be(SettingType.Expander);
        expander.HasToggleSwitch.Should().BeTrue();
        expander.DefaultValue.Should().Be(false);
        expander.ChildFields.Select(f => f.Key).Should().BeEquivalentTo("x264_tune", "x264_profile");
    }
}
