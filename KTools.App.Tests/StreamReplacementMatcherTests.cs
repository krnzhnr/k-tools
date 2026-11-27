// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Scripts;

namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты автоопределения замен дорожек по названиям и языкам.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class StreamReplacementMatcherTests
{
    private static MediaTrack Track(int id, string type, string? name = null, string language = "und")
    {
        return new MediaTrack { TrackId = id, TrackType = type, Codec = "ac3", Name = name ?? string.Empty, Language = language };
    }

    private static StreamReplacementCandidate ContainerCandidate(string file, MediaTrack track)
    {
        return new StreamReplacementCandidate { FilePath = file, SourceTrackId = track.TrackId, Track = track };
    }

    private static StreamReplacementCandidate SimpleCandidate(string file)
    {
        return new StreamReplacementCandidate { FilePath = file, SourceTrackId = 0, Track = null };
    }

    /// <summary>
    /// Проверяет, что совпадение названий дорожек без учёта регистра даёт подстановку.
    /// </summary>
    [TestMethod]
    public void FindBestMatch_NameEquals_ReturnsMatchingCandidate()
    {
        // Arrange
        var source = Track(1, "audio", "Дубляж", "rus");
        var candidates = new List<StreamReplacementCandidate>
        {
            ContainerCandidate("C:\\rep\\film.mkv", Track(0, "audio", "Оригинал", "eng")),
            ContainerCandidate("C:\\rep\\dub.mkv", Track(2, "audio", "дубляж", "rus")),
        };

        // Act
        var best = StreamReplacementMatcher.FindBestMatch(source, candidates);

        // Assert
        best.Should().NotBeNull();
        best!.FilePath.Should().Be("C:\\rep\\dub.mkv");
        best.SourceTrackId.Should().Be(2);
    }

    /// <summary>
    /// Проверяет, что простой файл с именем, совпадающим с названием дорожки, подставляется автоматически.
    /// </summary>
    [TestMethod]
    public void FindBestMatch_SimpleFileNameEquals_ReturnsCandidate()
    {
        // Arrange
        var source = Track(2, "subtitles", "Перевод Иванова", "rus");
        var candidates = new List<StreamReplacementCandidate>
        {
            SimpleCandidate("C:\\rep\\Перевод Иванова.srt"),
            SimpleCandidate("C:\\rep\\прочее.srt"),
        };

        // Act
        var best = StreamReplacementMatcher.FindBestMatch(source, candidates);

        // Assert
        best.Should().NotBeNull();
        best!.FilePath.Should().Be("C:\\rep\\Перевод Иванова.srt");
    }

    /// <summary>
    /// Проверяет, что единственный кандидат с тем же языком подставляется при пустых названиях.
    /// </summary>
    [TestMethod]
    public void FindBestMatch_LanguageEqualsSingleCandidate_ReturnsCandidate()
    {
        // Arrange
        var source = Track(1, "audio", null, "eng");
        var candidates = new List<StreamReplacementCandidate>
        {
            ContainerCandidate("C:\\rep\\a.mkv", Track(0, "audio", null, "rus")),
            ContainerCandidate("C:\\rep\\b.mkv", Track(1, "audio", null, "eng")),
        };

        // Act
        var best = StreamReplacementMatcher.FindBestMatch(source, candidates);

        // Assert
        best.Should().NotBeNull();
        best!.FilePath.Should().Be("C:\\rep\\b.mkv");
    }

    /// <summary>
    /// Проверяет, что несколько кандидатов с тем же языком не дают автоматической подстановки.
    /// </summary>
    [TestMethod]
    public void FindBestMatch_LanguageEqualsMultipleCandidates_ReturnsNull()
    {
        // Arrange
        var source = Track(1, "audio", null, "rus");
        var candidates = new List<StreamReplacementCandidate>
        {
            ContainerCandidate("C:\\rep\\a.mkv", Track(0, "audio", null, "rus")),
            ContainerCandidate("C:\\rep\\b.mkv", Track(1, "audio", null, "rus")),
        };

        // Act
        var best = StreamReplacementMatcher.FindBestMatch(source, candidates);

        // Assert
        best.Should().BeNull("неоднозначное совпадение оставлено на ручной выбор");
    }

    /// <summary>
    /// Проверяет, что точное совпадение названия важнее совпадения только по языку.
    /// </summary>
    [TestMethod]
    public void FindBestMatch_NameHasPriorityOverLanguage_ReturnsNameMatch()
    {
        // Arrange
        var source = Track(1, "audio", "Дубляж", "rus");
        var candidates = new List<StreamReplacementCandidate>
        {
            ContainerCandidate("C:\\rep\\a.mkv", Track(0, "audio", null, "rus")),
            ContainerCandidate("C:\\rep\\b.mkv", Track(5, "audio", "Дубляж", "eng")),
        };

        // Act
        var best = StreamReplacementMatcher.FindBestMatch(source, candidates);

        // Assert
        best.Should().NotBeNull();
        best!.FilePath.Should().Be("C:\\rep\\b.mkv");
    }

    /// <summary>
    /// Проверяет, что при отсутствии совпадений возвращается null.
    /// </summary>
    [TestMethod]
    public void FindBestMatch_NoMatches_ReturnsNull()
    {
        // Arrange
        var source = Track(1, "audio", "Дубляж", "rus");
        var candidates = new List<StreamReplacementCandidate>
        {
            ContainerCandidate("C:\\rep\\a.mkv", Track(0, "audio", "Оригинал", "eng")),
            SimpleCandidate("C:\\rep\\прочее.srt"),
        };

        // Act
        var best = StreamReplacementMatcher.FindBestMatch(source, candidates);

        // Assert
        best.Should().BeNull();
    }

    /// <summary>
    /// Проверяет, что пустой список кандидатов не даёт подстановки.
    /// </summary>
    [TestMethod]
    public void FindBestMatch_EmptyCandidates_ReturnsNull()
    {
        // Arrange
        var source = Track(1, "audio", "Дубляж", "rus");

        // Act
        var best = StreamReplacementMatcher.FindBestMatch(source, new List<StreamReplacementCandidate>());

        // Assert
        best.Should().BeNull();
    }

    /// <summary>
    /// Проверяет, что пустая исходная дорожка приводит к понятной ошибке.
    /// </summary>
    [TestMethod]
    public void FindBestMatch_NullSource_ThrowsArgumentNullException()
    {
        // Act
        Action act = () => StreamReplacementMatcher.FindBestMatch(null!, new List<StreamReplacementCandidate>());

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }
}
