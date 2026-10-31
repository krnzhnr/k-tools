// -*- coding: utf-8 -*-
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using KTools_App.Core;

namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты универсального соглашения об именовании ключей авторасчёта битрейта.
/// Механизм един для всех кодеков: префикс выводится из ключа, без настройки под каждый кодек.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class BitrateAutoModeTests
{
    [TestMethod]
    public void IsAutoBitrateController_PrefixedAndPlainKeys_ReturnsTrue()
    {
        BitrateAutoMode.IsAutoBitrateController("auto_bitrate").Should().BeTrue();
        BitrateAutoMode.IsAutoBitrateController("x265_auto_bitrate").Should().BeTrue();
        BitrateAutoMode.IsAutoBitrateController("x264_auto_bitrate").Should().BeTrue();
    }

    [TestMethod]
    public void IsAutoBitrateController_UnrelatedKeys_ReturnsFalse()
    {
        BitrateAutoMode.IsAutoBitrateController("v_bitrate").Should().BeFalse();
        BitrateAutoMode.IsAutoBitrateController("x265_v_bitrate").Should().BeFalse();
        BitrateAutoMode.IsAutoBitrateController("lossless").Should().BeFalse();
        BitrateAutoMode.IsAutoBitrateController("bitrate").Should().BeFalse();
    }

    [TestMethod]
    public void TryGetPrefix_PlainNvencKeys_ReturnsEmptyPrefix()
    {
        BitrateAutoMode.TryGetPrefix("auto_bitrate").Should().Be(string.Empty);
        BitrateAutoMode.TryGetPrefix("v_bitrate").Should().Be(string.Empty);
        BitrateAutoMode.TryGetPrefix("min_bitrate").Should().Be(string.Empty);
        BitrateAutoMode.TryGetPrefix("max_bitrate").Should().Be(string.Empty);
        BitrateAutoMode.TryGetPrefix("bufsize").Should().Be(string.Empty);
    }

    [TestMethod]
    public void TryGetPrefix_PrefixedKeys_ReturnsCodecPrefix()
    {
        BitrateAutoMode.TryGetPrefix("x265_auto_bitrate").Should().Be("x265");
        BitrateAutoMode.TryGetPrefix("x265_v_bitrate").Should().Be("x265");
        BitrateAutoMode.TryGetPrefix("x265_min_bitrate").Should().Be("x265");
        BitrateAutoMode.TryGetPrefix("x265_max_bitrate").Should().Be("x265");
        BitrateAutoMode.TryGetPrefix("x265_bufsize").Should().Be("x265");
        BitrateAutoMode.TryGetPrefix("x264_auto_bitrate").Should().Be("x264");
    }

    [TestMethod]
    public void TryGetPrefix_UnrelatedKey_ReturnsNull()
    {
        BitrateAutoMode.TryGetPrefix("lossless").Should().BeNull();
        BitrateAutoMode.TryGetPrefix("nvenc_preset").Should().BeNull();
        BitrateAutoMode.TryGetPrefix("encoder").Should().BeNull();
    }

    [TestMethod]
    public void GroupKeys_EmptyPrefix_ReturnsPlainKeys()
    {
        BitrateAutoMode.TargetBitrateKey(string.Empty).Should().Be("v_bitrate");
        BitrateAutoMode.MinBitrateKey(string.Empty).Should().Be("min_bitrate");
        BitrateAutoMode.MaxBitrateKey(string.Empty).Should().Be("max_bitrate");
        BitrateAutoMode.BufsizeKey(string.Empty).Should().Be("bufsize");
        BitrateAutoMode.WithPrefix(string.Empty, "auto_bitrate").Should().Be("auto_bitrate");
    }

    [TestMethod]
    public void GroupKeys_CodecPrefix_ReturnsPrefixedKeys()
    {
        BitrateAutoMode.TargetBitrateKey("x265").Should().Be("x265_v_bitrate");
        BitrateAutoMode.MinBitrateKey("x265").Should().Be("x265_min_bitrate");
        BitrateAutoMode.MaxBitrateKey("x265").Should().Be("x265_max_bitrate");
        BitrateAutoMode.BufsizeKey("x265").Should().Be("x265_bufsize");
        BitrateAutoMode.WithPrefix("x264", "auto_bitrate").Should().Be("x264_auto_bitrate");
    }

    [TestMethod]
    public void ComputeBounds_TargetBitrate_CalculatesMinMaxBufsize()
    {
        // Arrange
        const int target = 5000;

        // Act
        var (min, max, buf) = BitrateAutoMode.ComputeBounds(target);

        // Assert: минимум равен цели, максимум — удвоенной цели, буфер — удвоенному максимуму
        min.Should().Be(5000);
        max.Should().Be(10000);
        buf.Should().Be(20000);
    }
}
