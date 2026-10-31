// -*- coding: utf-8 -*-
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using Moq;
using KTools_App.Core;
using KTools_App.Encoders;

namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты диспетчера программного CPU-энкодера: выбор подкодека по настройке cpu_codec,
/// дефолт HEVC для старых конфигураций, делегирование аргументов и тегов.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class CpuEncoderTests
{
    private CpuEncoder _encoder = null!;

    [TestInitialize]
    public void Setup()
    {
        _encoder = new CpuEncoder();
    }

    [TestMethod]
    public void Properties_VerifyCorrectMetadata()
    {
        _encoder.StableId.Should().Be("cpu");
        _encoder.DisplayName.Should().Be("CPU");
        _encoder.IsSupported(new Mock<IHardwareCapabilityCache>().Object).Should().BeTrue("CPU доступен всегда");
    }

    [TestMethod]
    public void GetFfmpegCodecName_MissingCodecSetting_DefaultsToLibx265()
    {
        // Старые конфигурации без ключа cpu_codec продолжают кодироваться как x265
        _encoder.GetFfmpegCodecName(new Dictionary<string, object>()).Should().Be("libx265");
    }

    [TestMethod]
    public void GetFfmpegCodecName_HevcSelected_ReturnsLibx265()
    {
        var settings = new Dictionary<string, object> { { CpuEncoder.CodecSettingKey, "HEVC / H.265" } };

        _encoder.GetFfmpegCodecName(settings).Should().Be("libx265");
    }

    [TestMethod]
    public void GetFfmpegCodecName_H264Selected_ReturnsLibx264()
    {
        var settings = new Dictionary<string, object> { { CpuEncoder.CodecSettingKey, "AVC / H.264" } };

        _encoder.GetFfmpegCodecName(settings).Should().Be("libx264");
    }

    [TestMethod]
    public void GetFfmpegCodecName_UnknownCodec_FallsBackToLibx265()
    {
        var settings = new Dictionary<string, object> { { CpuEncoder.CodecSettingKey, "VP9" } };

        _encoder.GetFfmpegCodecName(settings).Should().Be("libx265");
    }

    [TestMethod]
    public void GetEncoderSettings_DefaultExposesCodecSelectorAndHevcFieldsOnly()
    {
        // Act
        var fields = _encoder.GetEncoderSettings();

        // Assert
        var selector = fields.Find(f => f.Key == CpuEncoder.CodecSettingKey);
        selector.Should().NotBeNull();
        selector!.Type.Should().Be(SettingType.Combo);
        selector.Options.Should().BeEquivalentTo("HEVC / H.265", "AVC / H.264");
        selector.DefaultValue.Should().Be("HEVC / H.265");

        fields.Should().Contain(f => f.Key == "x265_preset");
        fields.Should().NotContain(f => f.Key.StartsWith("x264_"),
            "поля невыбранного кодека не должны попадать в схему");
    }

    [TestMethod]
    public void GetEncoderSettings_H264Selected_ExposesOnlyH264Fields()
    {
        // Arrange
        var current = new Dictionary<string, object> { { CpuEncoder.CodecSettingKey, "AVC / H.264" } };

        // Act
        var fields = _encoder.GetEncoderSettings(current);

        // Assert
        fields.Should().Contain(f => f.Key == "x264_preset");
        fields.Should().NotContain(f => f.Key.StartsWith("x265_"),
            "поля невыбранного кодека не должны попадать в схему");
    }

    [TestMethod]
    public void BuildEncoderArguments_H264Selected_DelegatesToLibx264()
    {
        // Arrange
        var settings = new Dictionary<string, object>
        {
            { CpuEncoder.CodecSettingKey, "AVC / H.264" },
            { "x264_preset", "veryfast" },
            { "x264_rc", "CRF" },
            { "x264_crf", 20 }
        };
        var context = new EncoderSharedContext(IsLossless: false, Force10Bit: false, ContainerExtension: ".mkv");

        // Act
        var args = _encoder.BuildEncoderArguments(settings, context);

        // Assert
        args.Should().ContainInOrder("-c:v", "libx264");
        args.Should().ContainInOrder("-preset", "veryfast");
        args.Should().ContainInOrder("-crf", "20");
    }

    [TestMethod]
    public void BuildEncoderArguments_HevcSelected_DelegatesToLibx265()
    {
        // Arrange
        var settings = new Dictionary<string, object>
        {
            { CpuEncoder.CodecSettingKey, "HEVC / H.265" },
            { "x265_preset", "slow" },
            { "x265_rc", "CRF" },
            { "x265_crf", 24 }
        };
        var context = new EncoderSharedContext(IsLossless: false, Force10Bit: false, ContainerExtension: ".mkv");

        // Act
        var args = _encoder.BuildEncoderArguments(settings, context);

        // Assert
        args.Should().ContainInOrder("-c:v", "libx265");
        args.Should().ContainInOrder("-preset", "slow");
        args.Should().ContainInOrder("-crf", "24");
    }

    [TestMethod]
    public void GetContainerTag_HevcMp4_ReturnsHvc1AndH264ReturnsNull()
    {
        var contextMp4 = new EncoderSharedContext(IsLossless: false, Force10Bit: false, ContainerExtension: ".mp4");
        var hevcSettings = new Dictionary<string, object> { { CpuEncoder.CodecSettingKey, "HEVC / H.265" } };
        var h264Settings = new Dictionary<string, object> { { CpuEncoder.CodecSettingKey, "AVC / H.264" } };

        _encoder.GetContainerTag(hevcSettings, contextMp4).Should().Be("hvc1");
        _encoder.GetContainerTag(h264Settings, contextMp4).Should().BeNull();
    }

    [TestMethod]
    public void BuildInputArgumentsAsync_AnyMedia_ReturnsEmpty()
    {
        // Arrange
        var structure = new KTools_App.Core.MediaStructure { FilePath = "dummy.mkv", Duration = 10.0 };

        // Act
        var args = _encoder.BuildInputArgumentsAsync(structure, CancellationToken.None).GetAwaiter().GetResult();

        // Assert
        args.Should().BeEmpty("программному кодированию аппаратное декодирование на входе не требуется");
    }

    [TestMethod]
    public void GetEncoderSettings_AnyContext_AlwaysContainsCodecSelector()
    {
        // Контракт для перестройки UI: селектор обязан присутствовать при любом контексте,
        // иначе смена кодека не детектируется и интерфейс показывает чужие поля.
        _encoder.GetEncoderSettings(null).Should().Contain(f => f.Key == CpuEncoder.CodecSettingKey);
        _encoder.GetEncoderSettings(new Dictionary<string, object> { { "lossless", true } })
            .Should().Contain(f => f.Key == CpuEncoder.CodecSettingKey);
        _encoder.GetEncoderSettings(new Dictionary<string, object> { { CpuEncoder.CodecSettingKey, "AVC / H.264" } })
            .Should().Contain(f => f.Key == CpuEncoder.CodecSettingKey);
    }

    [TestMethod]
    public void GetFfmpegCodecName_JsonElementFromDisk_ResolvesCodec()
    {
        // Регрессия: значение с диска (JsonElement) обязано резолвиться так же,
        // как его показывает интерфейс, а не падать в кодек по умолчанию.
        using var doc = System.Text.Json.JsonDocument.Parse("\"AVC / H.264\"");
        var settings = new Dictionary<string, object> { { CpuEncoder.CodecSettingKey, doc.RootElement.Clone() } };

        _encoder.GetFfmpegCodecName(settings).Should().Be("libx264");
    }

    [TestMethod]
    public void BuildEncoderArguments_JsonElementPreset_UsesSavedValue()
    {
        // Arrange: пресет сохранён в прошлой сессии и прочитан с диска
        using var doc = System.Text.Json.JsonDocument.Parse("\"ultrafast\"");
        var settings = new Dictionary<string, object>
        {
            { CpuEncoder.CodecSettingKey, "AVC / H.264" },
            { "x264_preset", doc.RootElement.Clone() },
            { "x264_rc", "CRF" },
            { "x264_crf", 20 }
        };
        var context = new EncoderSharedContext(IsLossless: false, Force10Bit: false, ContainerExtension: ".mkv");

        // Act
        var args = _encoder.BuildEncoderArguments(settings, context);

        // Assert: UI показывает ultrafast — команда обязана нести ultrafast, а не дефолт medium
        args.Should().ContainInOrder("-preset", "ultrafast");
    }

    [TestMethod]
    public void GetPixelFormat_H264With10Bit_Returns8BitFormat()
    {
        var context = new EncoderSharedContext(IsLossless: false, Force10Bit: true, ContainerExtension: ".mkv", CodecName: "AVC / H.264");

        _encoder.GetPixelFormat(context).Should().Be("yuv420p");
    }

    [TestMethod]
    public void GetPixelFormat_HevcWith10Bit_Returns10BitFormat()
    {
        var context = new EncoderSharedContext(IsLossless: false, Force10Bit: true, ContainerExtension: ".mkv", CodecName: "HEVC / H.265");

        _encoder.GetPixelFormat(context).Should().Be("yuv420p10le");
    }

    [TestMethod]
    public void GetPixelFormat_EmptyCodec_DefaultsToHevc()
    {
        // Старые конфигурации без ключа cpu_codec резолвятся в HEVC-дефолт.
        var context = new EncoderSharedContext(IsLossless: false, Force10Bit: true, ContainerExtension: ".mkv");

        _encoder.GetPixelFormat(context).Should().Be("yuv420p10le");
    }
}
