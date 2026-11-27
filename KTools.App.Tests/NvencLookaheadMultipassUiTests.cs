// -*- coding: utf-8 -*-
using System.Collections.Generic;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using KTools_App.Core;
using KTools_App.Encoders;

namespace KTools_App.Tests;

/// <summary>
/// Тесты issue #115: ключи nv_lookahead/nv_multipass читаются построителем CLI,
/// поэтому обязаны существовать как поля настроек с блокировкой при lossless.
/// </summary>
[TestClass]
public class NvencLookaheadMultipassUiTests
{
    [TestMethod]
    public void GetEncoderSettings_NvLookaheadField_ExposesIntRangeWithLosslessDisable()
    {
        // Arrange
        var encoder = new NvencEncoder();

        // Act
        var settings = encoder.GetEncoderSettings();

        // Assert
        var field = settings.Find(f => f.Key == "nv_lookahead");
        field.Should().NotBeNull("построитель CLI читает 'nv_lookahead', поле обязано быть в интерфейсе");
        field!.Type.Should().Be(SettingType.Int);
        field.Minimum.Should().Be(0);
        field.Maximum.Should().Be(32);
        var losslessCond = field.DisableConditions?.Find(c => c.Key == "lossless");
        losslessCond.Should().NotBeNull("lookahead обслуживает rate control и обязан блокироваться при lossless");
        losslessCond!.Values.Should().Contain("True");
    }

    [TestMethod]
    public void GetEncoderSettings_NvMultipassField_ExposesComboWithLosslessDisable()
    {
        // Arrange
        var encoder = new NvencEncoder();

        // Act
        var settings = encoder.GetEncoderSettings();

        // Assert
        var field = settings.Find(f => f.Key == "nv_multipass");
        field.Should().NotBeNull("построитель CLI читает 'nv_multipass', поле обязано быть в интерфейсе");
        field!.Type.Should().Be(SettingType.Combo);
        field.Options.Should().BeEquivalentTo(new[] { "disabled", "qres", "fullres" });
        var losslessCond = field.DisableConditions?.Find(c => c.Key == "lossless");
        losslessCond.Should().NotBeNull("multipass обслуживает rate control и обязан блокироваться при lossless");
        losslessCond!.Values.Should().Contain("True");
    }

    [TestMethod]
    public void BuildEncoderArguments_NvLookaheadZero_OmitsRcLookaheadFlag()
    {
        // Arrange: значение 0 из нового поля интерфейса обязано убирать флаг,
        // чтобы владельцы старых GPU могли снизить расход видеопамяти
        var encoder = new NvencEncoder();
        var settings = new Dictionary<string, object>
        {
            { "nvenc_codec", "HEVC / H.265" },
            { "nv_lookahead", 0 }
        };
        var context = new EncoderSharedContext(IsLossless: false, Force10Bit: false, ContainerExtension: ".mkv");

        // Act
        var args = encoder.BuildEncoderArguments(settings, context);

        // Assert
        args.Should().NotContain("-rc-lookahead");
    }
}
