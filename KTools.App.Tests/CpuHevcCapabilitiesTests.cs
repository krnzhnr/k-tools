// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Encoders;
using KTools_App.Encoders.Capabilities;

namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты программного кодека HEVC (libx265): сборка аргументов, валидация границ,
/// Clamping, форматирование float независимое от локали и режим Lossless.
/// Портированы 1:1 с бывшего X265Encoder (ключи настроек x265_* не менялись).
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class CpuHevcCapabilitiesTests
{
    private CpuHevcCapabilities _cap = null!;
    private EncoderSharedContext _contextMkv = null!;

    [TestInitialize]
    public void Setup()
    {
        _cap = new CpuHevcCapabilities();
        _contextMkv = new EncoderSharedContext(IsLossless: false, Force10Bit: false, ContainerExtension: ".mkv");
    }

    [TestMethod]
    public void Properties_VerifyCorrectMetadata()
    {
        _cap.CodecName.Should().Be("HEVC / H.265");
        _cap.FfmpegCodecName.Should().Be("libx265");
        _cap.Supports10Bit.Should().BeTrue();
        _cap.GetPixelFormat(force10Bit: false).Should().Be("yuv420p");
        _cap.GetPixelFormat(force10Bit: true).Should().Be("yuv420p10le");
    }

    [TestMethod]
    public void BuildCodecArguments_LosslessMode_OverridesRateControlAndSetsLosslessFlag()
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
        var args = _cap.BuildCodecArguments(settings, context);

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
    public void BuildCodecArguments_AqModeAndStrength_ClampsAndFormatsWithDot()
    {
        // Arrange: сила AQ передаётся равной 5.5f (должна ограничиться до 3.0)
        var settings = new Dictionary<string, object>
        {
            { "x265_advanced_options", true },
            { "x265_aq_mode", "3" },
            { "x265_aq_strength", 5.5f }
        };

        // Act
        var args = _cap.BuildCodecArguments(settings, _contextMkv);

        // Assert
        int paramsIndex = args.IndexOf("-x265-params");
        string x265ParamsStr = args[paramsIndex + 1];
        x265ParamsStr.Should().Contain("aq-mode=3");
        x265ParamsStr.Should().Contain("aq-strength=3.0");
        x265ParamsStr.Should().NotContain("aq-strength=3,0"); // Проверка CultureInfo.InvariantCulture
    }

    [TestMethod]
    public void BuildCodecArguments_LookaheadAndBframes_ClampsAndOmitsDefaults()
    {
        // Arrange
        var settings = new Dictionary<string, object>
        {
            { "x265_advanced_options", true },
            { "x265_lookahead", 300 }, // Ограничивается до 250
            { "x265_bframes", 20 },     // Ограничивается до 16
            { "x265_b_adapt", "1" }
        };

        // Act
        var args = _cap.BuildCodecArguments(settings, _contextMkv);

        // Assert
        int paramsIndex = args.IndexOf("-x265-params");
        string x265ParamsStr = args[paramsIndex + 1];
        x265ParamsStr.Should().Contain("rc-lookahead=250");
        x265ParamsStr.Should().Contain("bframes=16");
        x265ParamsStr.Should().Contain("b-adapt=1");
    }

    [TestMethod]
    public void BuildCodecArguments_PsyRdAndPsyRdoq_FormatsCorrectly()
    {
        // Arrange
        var settings = new Dictionary<string, object>
        {
            { "x265_advanced_options", true },
            { "x265_psy_rd", 2.5f },
            { "x265_psy_rdoq", 15.0f }
        };

        // Act
        var args = _cap.BuildCodecArguments(settings, _contextMkv);

        // Assert
        int paramsIndex = args.IndexOf("-x265-params");
        string x265ParamsStr = args[paramsIndex + 1];
        x265ParamsStr.Should().Contain("psy-rd=2.5");
        x265ParamsStr.Should().Contain("psy-rdoq=15.0");
    }

    [TestMethod]
    public void BuildCodecArguments_DeblockSaoFastPskip_AppliesFlags()
    {
        // Arrange
        var settings = new Dictionary<string, object>
        {
            { "x265_advanced_options", true },
            { "x265_deblock", "-1:-1" },
            { "x265_no_sao", true },
            { "x265_no_fast_pskip", true }
        };

        // Act
        var args = _cap.BuildCodecArguments(settings, _contextMkv);

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

        _cap.GetContainerTag(new Dictionary<string, object>(), contextMp4).Should().Be("hvc1");
        _cap.GetContainerTag(new Dictionary<string, object>(), contextMkv).Should().BeNull();
    }

    [TestMethod]
    public void GetCodecSettings_BitrateFields_VerifyPresenceAndConditions()
    {
        // Act
        var settings = _cap.GetCodecSettings();

        // Assert
        var rcField = settings.Find(f => f.Key == "x265_rc");
        rcField.Should().NotBeNull();
        rcField!.DisableConditions.Should().NotBeNull();
        rcField.DisableConditions!.Find(c => c.Key == "lossless").Should().NotBeNull();

        var autoBrField = settings.Find(f => f.Key == "x265_auto_bitrate");
        autoBrField.Should().NotBeNull();
        autoBrField!.VisibilityConditions.Should().NotBeNull();
        autoBrField.VisibilityConditions!.Find(c => c.Key == "x265_rc" && c.Values.Contains("Битрейт (ABR)")).Should().NotBeNull();

        foreach (var key in new[] { "x265_min_bitrate", "x265_max_bitrate", "x265_bufsize" })
        {
            var field = settings.Find(f => f.Key == key);
            field.Should().NotBeNull($"Поле '{key}' обязано существовать");
            field!.DisableConditions.Should().NotBeNull();
            field.DisableConditions!.Find(c => c.Key == "x265_auto_bitrate").Should().NotBeNull();
            field.DisableConditions.Should().NotContain(c => c.Key == "auto_bitrate",
                $"поле '{key}' не должно ссылаться на чужой контроллер NVENC");
        }
    }

    [TestMethod]
    public void BuildCodecArguments_CustomBitrateParameters_PassesMinMaxBufsize()
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

        // Act
        var args = _cap.BuildCodecArguments(settings, _contextMkv);

        // Assert
        args.Should().ContainInOrder("-b:v", "6000k");
        args.Should().ContainInOrder("-minrate", "4500k");
        args.Should().ContainInOrder("-maxrate", "9000k");
        args.Should().ContainInOrder("-bufsize", "18000k");
    }

    [TestMethod]
    public void GetCodecSettings_AdvancedOptions_ExposesToggleExpanderWithAllChildKeys()
    {
        // Act
        var settings = _cap.GetCodecSettings();

        // Assert: сам экспандер с тумблером, выключенным по умолчанию
        var expander = settings.Find(f => f.Key == "x265_advanced_options");
        expander.Should().NotBeNull();
        expander!.Type.Should().Be(SettingType.Expander);
        expander.HasToggleSwitch.Should().BeTrue();
        expander.DefaultValue.Should().Be(false);

        // Assert: все расширенные параметры живут внутри экспандера, а не отдельными полями
        var childKeys = expander.ChildFields.Select(f => f.Key).ToList();
        childKeys.Should().BeEquivalentTo(
            "x265_tune", "x265_aq_mode", "x265_aq_strength", "x265_lookahead",
            "x265_bframes", "x265_b_adapt", "x265_psy_rd", "x265_psy_rdoq",
            "x265_deblock", "x265_no_sao", "x265_no_fast_pskip");
        settings.Should().NotContain(f =>
            f.Key.StartsWith("x265_") && f.Key != "x265_advanced_options"
            && childKeys.Contains(f.Key));
    }

    [TestMethod]
    public void BuildCodecArguments_AdvancedOptionsDisabled_OmitsAllAdvancedParams()
    {
        // Arrange: тумблер выключен, но дочерние значения изменены — они должны игнорироваться
        var settings = new Dictionary<string, object>
        {
            { "x265_advanced_options", false },
            { "x265_tune", "grain" },
            { "x265_aq_mode", "3" },
            { "x265_aq_strength", 2.0f },
            { "x265_lookahead", 50 },
            { "x265_bframes", 8 },
            { "x265_b_adapt", "0" },
            { "x265_psy_rd", 3.0f },
            { "x265_psy_rdoq", 10.0f },
            { "x265_deblock", "-1:-1" },
            { "x265_no_sao", true },
            { "x265_no_fast_pskip", true }
        };

        // Act
        var args = _cap.BuildCodecArguments(settings, _contextMkv);

        // Assert: чистый дефолт — ни tune, ни одного расширенного параметра
        args.Should().NotContain("-tune");
        args.Should().NotContain("-x265-params");
    }

    [TestMethod]
    public void BuildCodecArguments_AdvancedOptionsMissingKey_OmitsAllAdvancedParams()
    {
        // Arrange: ключ тумблера отсутствует (старые сохранённые настройки) — тоже чистый дефолт
        var settings = new Dictionary<string, object>
        {
            { "x265_aq_mode", "3" }
        };

        // Act
        var args = _cap.BuildCodecArguments(settings, _contextMkv);

        // Assert
        args.Should().NotContain("-x265-params");
    }

    [TestMethod]
    public void BuildCodecArguments_AdvancedOptionsEnabled_AppliesParamsAsBefore()
    {
        // Arrange: тумблер включён — поведение как раньше, включая безусловный aq-mode
        var settings = new Dictionary<string, object>
        {
            { "x265_advanced_options", true },
            { "x265_tune", "grain" }
        };

        // Act
        var args = _cap.BuildCodecArguments(settings, _contextMkv);

        // Assert
        args.Should().ContainInOrder("-tune", "grain");
        args.Should().Contain("-x265-params");
        int paramsIndex = args.IndexOf("-x265-params");
        string x265ParamsStr = args[paramsIndex + 1];
        x265ParamsStr.Should().Contain("aq-mode=2");
    }
}
