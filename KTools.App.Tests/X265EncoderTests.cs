using System.Collections.Generic;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using KTools_App.Encoders;

namespace KTools_App.Tests;

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
    public void BuildEncoderArguments_Lossless_ReturnsCorrectArgs()
    {
        // Arrange
        var settings = new Dictionary<string, object>
        {
            { "cpu_preset", "medium" },
            { "v_qp", 0 } // В Lossless ожидается -x265-params lossless=1
        };
        var context = new EncoderSharedContext(IsLossless: true, Force10Bit: false, ContainerExtension: ".mkv");

        // Act
        var args = _encoder.BuildEncoderArguments(settings, context);

        // Assert
        args.Should().ContainInOrder("-c:v", "libx265");
        args.Should().ContainInOrder("-preset", "medium");
        // Проверяем наличие параметра lossless=1.
        args.Should().Contain(a => a.Contains("lossless=1"));
    }

    [TestMethod]
    public void BuildEncoderArguments_CRF_ReturnsCorrectArgs()
    {
        // Arrange
        var settings = new Dictionary<string, object>
        {
            { "cpu_preset", "slow" },
            { "cpu_crf", 18 }
        };
        var context = new EncoderSharedContext(IsLossless: false, Force10Bit: false, ContainerExtension: ".mkv");

        // Act
        var args = _encoder.BuildEncoderArguments(settings, context);

        // Assert
        args.Should().ContainInOrder("-c:v", "libx265");
        args.Should().ContainInOrder("-preset", "slow");
        args.Should().ContainInOrder("-crf", "18");
        args.Should().NotContain(a => a.Contains("lossless=1"));
    }

    [TestMethod]
    public void BuildEncoderArguments_10Bit_AddsProfileAndFormat()
    {
        // Arrange
        var settings = new Dictionary<string, object>
        {
            { "cpu_preset", "fast" },
            { "cpu_crf", 20 }
        };
        var context = new EncoderSharedContext(IsLossless: false, Force10Bit: true, ContainerExtension: ".mkv");

        // Act
        var args = _encoder.BuildEncoderArguments(settings, context);

        // Assert
        args.Should().ContainInOrder("-pix_fmt", "yuv420p10le");
    }

    [TestMethod]
    public void GetEncoderSettings_BitrateFields_HaveLosslessVisibilityCondition()
    {
        // Act
        var settings = _encoder.GetEncoderSettings();

        // Assert
        var bitrateKeys = new[] { "cpu_rc", "cpu_crf", "cpu_v_bitrate" };
        foreach (var key in bitrateKeys)
        {
            var field = settings.Find(f => f.Key == key);
            field.Should().NotBeNull($"Field '{key}' should exist in X265Encoder settings");
            field!.VisibilityConditions.Should().NotBeNull($"Field '{key}' should have VisibilityConditions");

            var losslessCondition = field.VisibilityConditions!.Find(c => c.Key == "lossless");
            losslessCondition.Should().NotBeNull($"Field '{key}' should have a VisibilityCondition for 'lossless'");
            losslessCondition!.Values.Should().Contain("True");
            losslessCondition.Negate.Should().BeTrue($"Field '{key}' visibility should be negated when lossless is True");
        }
    }

    [TestMethod]
    public void GetEncoderSettings_RateControlFields_HaveCorrectVisibilityConditions()
    {
        // Act
        var settings = _encoder.GetEncoderSettings();

        // Assert
        var crfField = settings.Find(f => f.Key == "cpu_crf");
        crfField.Should().NotBeNull();
        var crfRcCond = crfField!.VisibilityConditions!.Find(c => c.Key == "cpu_rc");
        crfRcCond.Should().NotBeNull();
        crfRcCond!.Values.Should().Contain("CRF");
        crfRcCond.Negate.Should().BeFalse("CRF field should only be visible when cpu_rc is CRF");

        var bitrateField = settings.Find(f => f.Key == "cpu_v_bitrate");
        bitrateField.Should().NotBeNull();
        var brRcCond = bitrateField!.VisibilityConditions!.Find(c => c.Key == "cpu_rc");
        brRcCond.Should().NotBeNull();
        brRcCond!.Values.Should().Contain("Битрейт (ABR)");
        brRcCond.Negate.Should().BeFalse("cpu_v_bitrate field should only be visible when cpu_rc is ABR");
    }
}
