// -*- coding: utf-8 -*-
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using KTools_App.Core;

namespace KTools_App.Tests.Core;

/// <summary>
/// Юнит-тесты для сопоставления сопутствующих файлов с видео MuxGroupMatcher.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class MuxGroupMatcherTests
{
    /// <summary>
    /// Проверяет точное совпадение имен без учета регистра.
    /// </summary>
    [TestMethod]
    public void IsCompanionOf_ExactMatch_ReturnsTrue()
    {
        MuxGroupMatcher.IsCompanionOf("video", "video").Should().BeTrue();
        MuxGroupMatcher.IsCompanionOf("Video", "video").Should().BeTrue();
    }

    /// <summary>
    /// Проверяет префиксные совпадения через допустимые разделители.
    /// </summary>
    [TestMethod]
    public void IsCompanionOf_PrefixWithDelimiter_ReturnsTrue()
    {
        MuxGroupMatcher.IsCompanionOf("video", "video.dub").Should().BeTrue();
        MuxGroupMatcher.IsCompanionOf("video", "video_en").Should().BeTrue();
        MuxGroupMatcher.IsCompanionOf("video", "video-ru").Should().BeTrue();
        MuxGroupMatcher.IsCompanionOf("video", "video [LostFilm]").Should().BeTrue();
        MuxGroupMatcher.IsCompanionOf("video", "video (rus)").Should().BeTrue();
        MuxGroupMatcher.IsCompanionOf("s01e01", "s01e01.en").Should().BeTrue();
    }

    /// <summary>
    /// Проверяет отсутствие совпадения для чужих файлов и префиксов без разделителя.
    /// </summary>
    [TestMethod]
    public void IsCompanionOf_NonMatching_ReturnsFalse()
    {
        MuxGroupMatcher.IsCompanionOf("video", "other").Should().BeFalse();
        MuxGroupMatcher.IsCompanionOf("video", "video2").Should().BeFalse();
        MuxGroupMatcher.IsCompanionOf("video", "myvideo").Should().BeFalse();
        MuxGroupMatcher.IsCompanionOf("s01e01", "s01e02").Should().BeFalse();
        MuxGroupMatcher.IsCompanionOf(string.Empty, "video").Should().BeFalse();
        MuxGroupMatcher.IsCompanionOf("video", string.Empty).Should().BeFalse();
    }

    /// <summary>
    /// Проверяет приоритет точного совпадения над префиксным при выборе видео.
    /// </summary>
    [TestMethod]
    public void FindBestVideoStem_ExactBeatsPrefix_ReturnsExact()
    {
        string? best = MuxGroupMatcher.FindBestVideoStem(["video", "videoext"], "video");
        best.Should().Be("video");
    }

    /// <summary>
    /// Проверяет выбор самого длинного (наиболее специфичного) префикса.
    /// </summary>
    [TestMethod]
    public void FindBestVideoStem_LongestPrefixWins_ReturnsLongest()
    {
        string? best = MuxGroupMatcher.FindBestVideoStem(["s01", "s01e01"], "s01e01.en");
        best.Should().Be("s01e01");
    }

    /// <summary>
    /// Проверяет возврат null при отсутствии подходящего видео.
    /// </summary>
    [TestMethod]
    public void FindBestVideoStem_NoMatch_ReturnsNull()
    {
        string? best = MuxGroupMatcher.FindBestVideoStem(["s01e01"], "s01e02.en");
        best.Should().BeNull();
    }
}
