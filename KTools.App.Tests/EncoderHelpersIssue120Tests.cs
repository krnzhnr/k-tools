// -*- coding: utf-8 -*-
using System.Collections.Generic;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using KTools_App.Encoders;
using KTools_App.Encoders.Capabilities;

namespace KTools_App.Tests;

/// <summary>
/// Проверка дедупликации построения аргументов между энкодерами (issue #120):
/// EncoderBitrateHelper, BRefModeHelper и ContainerTagHelper обязаны давать
/// те же аргументы, что раньше строились тремя копиями кода, без смены ключей.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class EncoderHelpersIssue120Tests
{
    private static readonly EncoderSharedContext Mkv = new(IsLossless: false, Force10Bit: false, ContainerExtension: ".mkv");

    // ---------- EncoderBitrateHelper ----------

    [TestMethod]
    public void ResolveAutoBounds_AutoVbr_CalculatesMinMaxBufsize()
    {
        // Arrange & Act: авторежим VBR раньше считался тремя копиями формулы
        EncoderBitrateHelper.ResolveAutoBounds(4000, collapseForCbr: false, out int minBr, out int maxBr, out int bufSize);

        // Assert: min = vBr, max = 2*vBr, буфер = 2*max
        minBr.Should().Be(4000);
        maxBr.Should().Be(8000);
        bufSize.Should().Be(16000);
    }

    [TestMethod]
    public void ResolveAutoBounds_AutoCbr_CollapsesBoundsToTarget()
    {
        // Arrange & Act: в CBR границы и буфер равны целевому битрейту,
        // иначе maxrate 2x превращает CBR в переменный битрейт
        EncoderBitrateHelper.ResolveAutoBounds(4000, collapseForCbr: true, out int minBr, out int maxBr, out int bufSize);

        // Assert
        minBr.Should().Be(4000);
        maxBr.Should().Be(4000);
        bufSize.Should().Be(4000);
    }

    [TestMethod]
    public void AppendBitrateArguments_FullBounds_EmitsAllFlags()
    {
        // Arrange
        var args = new List<string>();

        // Act
        EncoderBitrateHelper.AppendBitrateArguments(args, targetBitrateKbps: 4000, minBitrateKbps: 4000, maxBitrateKbps: 8000, bufferSizeKbit: 16000);

        // Assert: порядок и формат совпадают со старой тройной копией
        args.Should().ContainInOrder("-b:v", "4000k");
        args.Should().ContainInOrder("-minrate", "4000k");
        args.Should().ContainInOrder("-maxrate", "8000k");
        args.Should().ContainInOrder("-bufsize", "16000k");
    }

    [TestMethod]
    public void AppendBitrateArguments_ZeroBounds_OmitsZeroFlags()
    {
        // Arrange
        var args = new List<string>();

        // Act
        EncoderBitrateHelper.AppendBitrateArguments(args, targetBitrateKbps: 3000, minBitrateKbps: 0, maxBitrateKbps: 0, bufferSizeKbit: 0);

        // Assert: целевой битрейт пишется всегда, нулевые границы опускаются
        args.Should().ContainInOrder("-b:v", "3000k");
        args.Should().NotContain("-minrate");
        args.Should().NotContain("-maxrate");
        args.Should().NotContain("-bufsize");
    }

    // ---------- BRefModeHelper ----------

    [TestMethod]
    public void CreateSettingField_DefaultOptions_ContainsEachMiddleDisabled()
    {
        // Arrange & Act
        var field = BRefModeHelper.CreateSettingField(BRefModeHelper.GetOptions(supportEach: true), BRefModeHelper.DefaultComment);

        // Assert: ключи и дефолты без изменений
        field.Key.Should().Be("nv_b_ref_mode");
        field.DefaultValue.Should().Be("middle");
        field.Options.Should().ContainInOrder("disabled", "each", "middle");
    }

    [TestMethod]
    public void AppendArguments_EachModeWithNormalization_MapsToMiddle()
    {
        // Arrange: AV1 запрещает each и маппит его в middle (защита от ошибки -542398533)
        var settings = new Dictionary<string, object> { { "nv_b_ref_mode", "each" } };
        var args = new List<string>();

        // Act
        BRefModeHelper.AppendArguments(settings, Mkv, args, normalizeEachToMiddle: true);

        // Assert
        args.Should().ContainInOrder("-b_ref_mode", "middle");
    }

    [TestMethod]
    public void AppendArguments_EachModeWithoutNormalization_KeepsEach()
    {
        // Arrange: HEVC и H.264 поддерживают each напрямую
        var settings = new Dictionary<string, object> { { "nv_b_ref_mode", "each" } };
        var args = new List<string>();

        // Act
        BRefModeHelper.AppendArguments(settings, Mkv, args, normalizeEachToMiddle: false);

        // Assert
        args.Should().ContainInOrder("-b_ref_mode", "each");
    }

    [TestMethod]
    public void AppendArguments_LosslessContext_OmitsBRefMode()
    {
        // Arrange: в lossless rate control отключён, B-пирамида не влияет на результат
        var settings = new Dictionary<string, object> { { "nv_b_ref_mode", "middle" } };
        var lossless = new EncoderSharedContext(IsLossless: true, Force10Bit: false, ContainerExtension: ".mkv");
        var args = new List<string>();

        // Act
        BRefModeHelper.AppendArguments(settings, lossless, args, normalizeEachToMiddle: false);

        // Assert
        args.Should().NotContain("-b_ref_mode");
    }

    // ---------- ContainerTagHelper ----------

    [TestMethod]
    public void GetHevcContainerTag_Mp4Extension_ReturnsHvc1()
    {
        // Arrange, Act & Assert
        ContainerTagHelper.GetHevcContainerTag(".mp4").Should().Be("hvc1");
        ContainerTagHelper.GetHevcContainerTag(".MP4").Should().Be("hvc1");
    }

    [TestMethod]
    public void GetHevcContainerTag_NonMp4Extension_ReturnsNull()
    {
        // Arrange, Act & Assert
        ContainerTagHelper.GetHevcContainerTag(".mkv").Should().BeNull();
        ContainerTagHelper.GetHevcContainerTag(".avi").Should().BeNull();
    }

    // ---------- EncoderCapabilitySelector ----------

    [TestMethod]
    public void Resolve_UnknownCodec_ReturnsDefaultCapabilities()
    {
        // Arrange: неизвестный кодек обязан резолвиться в дефолт (HEVC),
        // как раньше делали две копии GetCapabilityProvider
        var hevc = new NvencHevcCapabilities();
        var map = new Dictionary<string, INvencCodecCapabilities> { { hevc.CodecName, hevc } };
        var settings = new Dictionary<string, object> { { "nvenc_codec", "неизвестный кодек" } };

        // Act
        var resolved = EncoderCapabilitySelector.Resolve(settings, "nvenc_codec", map, hevc);

        // Assert
        resolved.Should().BeSameAs(hevc);
    }

    [TestMethod]
    public void Resolve_MissingSettings_ReturnsDefaultCapabilities()
    {
        // Arrange: старые конфигурации без ключа кодека резолвятся в дефолт
        var hevc = new NvencHevcCapabilities();
        var map = new Dictionary<string, INvencCodecCapabilities> { { hevc.CodecName, hevc } };

        // Act & Assert
        EncoderCapabilitySelector.Resolve(null, "nvenc_codec", map, hevc).Should().BeSameAs(hevc);
        EncoderCapabilitySelector.Resolve(new Dictionary<string, object>(), "nvenc_codec", map, hevc).Should().BeSameAs(hevc);
    }
}
