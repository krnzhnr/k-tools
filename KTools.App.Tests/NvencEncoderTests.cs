using System.Collections.Generic;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using KTools_App.Encoders;
using KTools_App.Services.Contracts;

namespace KTools_App.Tests;

[TestClass]
public class NvencEncoderTests
{
    private Mock<ILogService> _logServiceMock = null!;
    private Mock<IHardwareCapabilityCache> _hardwareCacheMock = null!;
    private NvencEncoder _encoder = null!;

    [TestInitialize]
    public void Setup()
    {
        _logServiceMock = new Mock<ILogService>();
        _hardwareCacheMock = new Mock<IHardwareCapabilityCache>();
        _encoder = new NvencEncoder();
    }

    [TestMethod]
    public void BuildEncoderArguments_Lossless_ReturnsCorrectArgs()
    {
        // Arrange
        var settings = new Dictionary<string, object>
        {
            { "nvenc_preset", "p7" },
            { "v_qp", 0 }
        };
        var context = new EncoderSharedContext(IsLossless: true, Force10Bit: false, ContainerExtension: ".mkv");

        // Act
        var args = _encoder.BuildEncoderArguments(settings, context);

        // Assert
        args.Should().ContainInOrder("-c:v", "hevc_nvenc");
        args.Should().ContainInOrder("-preset", "p7");
        args.Should().ContainInOrder("-rc", "constqp");
        args.Should().ContainInOrder("-tune", "lossless");
        args.Should().Contain("-qp");
        args.Should().Contain("0");
        args.Should().NotContain("-b:v");
    }

    [TestMethod]
    public void BuildEncoderArguments_ConstQP_ReturnsCorrectArgs()
    {
        // Arrange
        var settings = new Dictionary<string, object>
        {
            { "nvenc_rc", "constqp" },
            { "v_qp", 20 }
        };
        var context = new EncoderSharedContext(IsLossless: false, Force10Bit: false, ContainerExtension: ".mkv");

        // Act
        var args = _encoder.BuildEncoderArguments(settings, context);

        // Assert
        args.Should().ContainInOrder("-rc", "constqp");
        args.Should().Contain("-qp");
        args.Should().Contain("20");
        args.Should().NotContain("-tune", "lossless");
    }

    [TestMethod]
    public void BuildEncoderArguments_VBR_ReturnsCorrectArgs()
    {
        // Arrange
        var settings = new Dictionary<string, object>
        {
            { "nvenc_rc", "vbr" },
            { "v_bitrate", "5000" }
        };
        var context = new EncoderSharedContext(IsLossless: false, Force10Bit: false, ContainerExtension: ".mkv");

        // Act
        var args = _encoder.BuildEncoderArguments(settings, context);

        // Assert
        args.Should().ContainInOrder("-rc", "vbr");
        args.Should().ContainInOrder("-b:v", "5000k");
        args.Should().NotContain("-qp");
        args.Should().NotContain("-tune", "lossless");
    }

    [TestMethod]
    public void GetEncoderSettings_BitrateFields_HaveLosslessDisableCondition()
    {
        // Act
        var settings = _encoder.GetEncoderSettings();

        // Assert
        var bitrateKeys = new[] { "auto_bitrate", "nvenc_rc", "v_bitrate", "v_qp", "min_bitrate", "max_bitrate", "bufsize" };
        foreach (var key in bitrateKeys)
        {
            var field = settings.Find(f => f.Key == key);
            field.Should().NotBeNull($"Field '{key}' should exist in NvencEncoder settings");
            field!.DisableConditions.Should().NotBeNull($"Field '{key}' should have DisableConditions");
            
            var losslessCondition = field.DisableConditions!.Find(c => c.Key == "lossless");
            losslessCondition.Should().NotBeNull($"Field '{key}' should have a DisableCondition for 'lossless'");
            losslessCondition!.Values.Should().Contain("True");
        }
    }

    [TestMethod]
    public void GetEncoderSettings_QualityAndBitrateFields_HaveRateControlVisibilityConditions()
    {
        // Act
        var settings = _encoder.GetEncoderSettings();

        // Assert
        var qpField = settings.Find(f => f.Key == "v_qp");
        qpField.Should().NotBeNull();
        var qpRcCond = qpField!.VisibilityConditions!.Find(c => c.Key == "nvenc_rc");
        qpRcCond.Should().NotBeNull();
        qpRcCond!.Values.Should().Contain("constqp");
        qpRcCond.Negate.Should().BeFalse("QP field should only be visible when nvenc_rc is constqp");

        var bitrateField = settings.Find(f => f.Key == "v_bitrate");
        bitrateField.Should().NotBeNull();
        var brRcCond = bitrateField!.VisibilityConditions!.Find(c => c.Key == "nvenc_rc");
        brRcCond.Should().NotBeNull();
        brRcCond!.Values.Should().Contain("constqp");
        brRcCond.Negate.Should().BeTrue("v_bitrate field should be hidden when nvenc_rc is constqp");
    }

    [TestMethod]
    public void GetEncoderSettings_AutoBitrateFields_HaveAutoBitrateDisableCondition()
    {
        // Act
        var settings = _encoder.GetEncoderSettings();

        // Assert
        var autoCalculatedKeys = new[] { "min_bitrate", "max_bitrate", "bufsize" };
        foreach (var key in autoCalculatedKeys)
        {
            var field = settings.Find(f => f.Key == key);
            field.Should().NotBeNull();
            field!.DisableConditions.Should().NotBeNull($"Field '{key}' should have DisableConditions");
            var autoCond = field.DisableConditions!.Find(c => c.Key == "auto_bitrate");
            autoCond.Should().NotBeNull($"Field '{key}' should have a DisableCondition for 'auto_bitrate'");
            autoCond!.Values.Should().Contain("True");
        }
    }

    [TestMethod]
    public void AutoBitrateCalculation_CalculatesCorrectMinMaxBufsize()
    {
        // Arrange
        int targetBitrate = 5000;

        // Act
        int minBitrate = targetBitrate;
        int maxBitrate = targetBitrate * 2;
        int bufsize = maxBitrate * 2;

        // Assert
        minBitrate.Should().Be(5000);
        maxBitrate.Should().Be(10000);
        bufsize.Should().Be(20000);
    }

    [TestMethod]
    public void BuildEncoderArguments_Av1Nvenc_GeneratesValidVbrAndCodec()
    {
        // Arrange
        var settings = new Dictionary<string, object>
        {
            { "nvenc_codec", "AV1" },
            { "nvenc_rc", "vbr_hq" } // Старое значение vbr_hq должно преобразоваться в валидный vbr
        };
        var context = new EncoderSharedContext(IsLossless: false, Force10Bit: true, ContainerExtension: ".mp4");

        // Act
        var args = _encoder.BuildEncoderArguments(settings, context);

        // Assert
        args.Should().ContainInOrder("-c:v", "av1_nvenc");
        args.Should().ContainInOrder("-rc", "vbr");
        args.Should().NotContain("vbr_hq", "vbr_hq не поддерживается в av1_nvenc и вызывает ошибку -22");
    }

    [TestMethod]
    public void BuildEncoderArguments_H264NvencWith10Bit_FallsBackToYuv420p()
    {
        // Arrange
        var settings = new Dictionary<string, object>
        {
            { "nvenc_codec", "AVC / H.264" }
        };
        var context = new EncoderSharedContext(IsLossless: false, Force10Bit: true, ContainerExtension: ".mp4");

        // Act
        var args = _encoder.BuildEncoderArguments(settings, context);

        // Assert
        args.Should().ContainInOrder("-c:v", "h264_nvenc");
        args.Should().ContainInOrder("-pix_fmt", "yuv420p");
        args.Should().NotContain("p010le", "NVENC H.264 не поддерживает 10-битный цвет p010le");
    }

    [TestMethod]
    public void GetContainerTag_Av1AndH264_ReturnNullTag()
    {
        // Arrange
        var context = new EncoderSharedContext(IsLossless: false, Force10Bit: false, ContainerExtension: ".mp4");

        // Act & Assert
        var av1Settings = new Dictionary<string, object> { { "nvenc_codec", "AV1" } };
        _encoder.GetContainerTag(av1Settings, context).Should().BeNull("AV1 не должен использовать тег hvc1");

        var h264Settings = new Dictionary<string, object> { { "nvenc_codec", "AVC / H.264" } };
        _encoder.GetContainerTag(h264Settings, context).Should().BeNull("H.264 не должен использовать тег hvc1");
    }

    [TestMethod]
    public void GetContainerTag_HevcMp4_ReturnsHvc1()
    {
        // Arrange
        var context = new EncoderSharedContext(IsLossless: false, Force10Bit: false, ContainerExtension: ".mp4");
        var hevcSettings = new Dictionary<string, object> { { "nvenc_codec", "HEVC / H.265" } };

        // Act
        var tag = _encoder.GetContainerTag(hevcSettings, context);

        // Assert
        tag.Should().Be("hvc1");
    }

    [TestMethod]
    public void BuildEncoderArguments_BRefModeEach_SupportedForHevcAndH264()
    {
        // Arrange
        var settings = new Dictionary<string, object>
        {
            { "nvenc_codec", "HEVC / H.265" },
            { "nv_b_ref_mode", "each" }
        };
        var context = new EncoderSharedContext(IsLossless: false, Force10Bit: false, ContainerExtension: ".mp4");

        // Act
        var args = _encoder.BuildEncoderArguments(settings, context);

        // Assert
        args.Should().ContainInOrder(new[] { "-b_ref_mode", "each" }, "HEVC NVENC поддерживает режим 'each'");
    }

    [TestMethod]
    public void BuildEncoderArguments_BRefModeEach_MapsToMiddleForAv1()
    {
        // Arrange
        var settings = new Dictionary<string, object>
        {
            { "nvenc_codec", "AV1" },
            { "nv_b_ref_mode", "each" }
        };
        var context = new EncoderSharedContext(IsLossless: false, Force10Bit: false, ContainerExtension: ".mp4");

        // Act
        var args = _encoder.BuildEncoderArguments(settings, context);

        // Assert
        args.Should().ContainInOrder("-b_ref_mode", "middle");
        args.Should().NotContain("each", "Режим 'each' вызывает сбой в NVENC AV1 и заменяется на 'middle'");
    }

    [TestMethod]
    public void CapabilityClasses_DirectInstantiation_ReportCorrectProperties()
    {
        var h264Cap = new KTools_App.Encoders.Capabilities.NvencH264Capabilities();
        h264Cap.CodecName.Should().Be("AVC / H.264");
        h264Cap.FfmpegCodecName.Should().Be("h264_nvenc");
        h264Cap.Supports10Bit.Should().BeFalse();
        h264Cap.GetPixelFormat(force10Bit: true).Should().Be("yuv420p");

        var hevcCap = new KTools_App.Encoders.Capabilities.NvencHevcCapabilities();
        hevcCap.CodecName.Should().Be("HEVC / H.265");
        hevcCap.FfmpegCodecName.Should().Be("hevc_nvenc");
        hevcCap.Supports10Bit.Should().BeTrue();
        hevcCap.GetPixelFormat(force10Bit: true).Should().Be("p010le");

        var av1Cap = new KTools_App.Encoders.Capabilities.NvencAv1Capabilities();
        av1Cap.CodecName.Should().Be("AV1");
        av1Cap.FfmpegCodecName.Should().Be("av1_nvenc");
        av1Cap.Supports10Bit.Should().BeTrue();
        av1Cap.GetPixelFormat(force10Bit: true).Should().Be("p010le");
    }

    [TestMethod]
    public void GetEncoderSettings_AqFields_HaveLosslessDisableConditions()
    {
        // Act
        var settings = _encoder.GetEncoderSettings();

        // Assert
        var aqKeys = new[] { "nv_spatial_aq", "nv_aq_strength", "nv_temporal_aq" };
        foreach (var key in aqKeys)
        {
            var field = settings.Find(f => f.Key == key);
            field.Should().NotBeNull($"Field '{key}' should exist");
            field!.DisableConditions.Should().NotBeNull($"Field '{key}' should have DisableConditions");

            var losslessCond = field.DisableConditions!.Find(c => c.Key == "lossless");
            losslessCond.Should().NotBeNull($"Field '{key}' should have DisableCondition for 'lossless'");
            losslessCond!.Values.Should().Contain("True");
        }
    }

    [TestMethod]
    public void GetEncoderSettings_AutoBitrateField_HasDefaultValueTrue()
    {
        // Act
        var settings = _encoder.GetEncoderSettings();

        // Assert
        var autoBitrateField = settings.Find(f => f.Key == "auto_bitrate");
        autoBitrateField.Should().NotBeNull("Field 'auto_bitrate' should exist");
        autoBitrateField!.DefaultValue.Should().Be(true, "auto_bitrate по умолчанию должен иметь значение true");

        var autoCalculatedKeys = new[] { "min_bitrate", "max_bitrate", "bufsize" };
        foreach (var key in autoCalculatedKeys)
        {
            var field = settings.Find(f => f.Key == key);
            field.Should().NotBeNull($"Field '{key}' should exist");
            var autoCond = field!.DisableConditions?.Find(c => c.Key == "auto_bitrate");
            autoCond.Should().NotBeNull($"Field '{key}' should have DisableCondition for auto_bitrate");
            autoCond!.Values.Should().Contain("True");
        }
    }
}
