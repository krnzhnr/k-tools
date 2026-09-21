// -*- coding: utf-8 -*-
using System.Collections.Generic;
using System.ComponentModel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.UI.Controls;

namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты для отображения встроенного заголовка дорожки MuxTrackItem.TitleHint.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class MuxTrackItemTests
{
    private static FileQueueItem CreateFileWithAudioTitle(string title)
    {
        var file = new FileQueueItem("C:\\mux\\s01e01.mka");
        var structure = new MediaStructure { FilePath = file.FilePath };
        structure.Tracks.Add(new MediaTrack { TrackId = 0, TrackType = "audio", Name = title });
        file.MediaInfo = structure;
        return file;
    }

    /// <summary>
    /// Проверяет, что встроенный заголовок аудио виден рядом с именем файла.
    /// </summary>
    [TestMethod]
    public void TitleHint_AudioTitle_ReturnsHint()
    {
        // Arrange
        var track = new MuxTrackItem(CreateFileWithAudioTitle("AniLibria"), "Аудио");

        // Act & Assert
        track.TitleHint.Should().Be("— AniLibria");
        track.HasTitleHint.Should().BeTrue();
    }

    /// <summary>
    /// Проверяет, что заголовок чужого типа дорожки игнорируется.
    /// </summary>
    [TestMethod]
    public void TitleHint_WrongTrackType_ReturnsEmpty()
    {
        // Arrange
        var file = new FileQueueItem("C:\\mux\\s01e01.mka");
        var structure = new MediaStructure { FilePath = file.FilePath };
        structure.Tracks.Add(new MediaTrack { TrackId = 0, TrackType = "subtitles", Name = "Полные" });
        file.MediaInfo = structure;
        var track = new MuxTrackItem(file, "Аудио");

        // Act & Assert
        track.TitleHint.Should().BeEmpty();
        track.HasTitleHint.Should().BeFalse();
    }

    /// <summary>
    /// Проверяет, что строка субтитров показывает заголовок субтитров.
    /// </summary>
    [TestMethod]
    public void TitleHint_SubsRow_ShowsSubsTitle()
    {
        // Arrange
        var file = new FileQueueItem("C:\\mux\\s01e01.ass");
        var structure = new MediaStructure { FilePath = file.FilePath };
        structure.Tracks.Add(new MediaTrack { TrackId = 0, TrackType = "subtitles", Name = "Forced" });
        file.MediaInfo = structure;
        var track = new MuxTrackItem(file, "Надписи", showRoleSelector: true);

        // Act & Assert
        track.TitleHint.Should().Be("— Forced");
    }

    /// <summary>
    /// Проверяет, что поздняя установка MediaInfo (фоновый анализ) обновляет подсказку.
    /// </summary>
    [TestMethod]
    public void TitleHint_MediaInfoSetLater_RaisesNotification()
    {
        // Arrange
        var file = new FileQueueItem("C:\\mux\\s01e01.mka");
        var track = new MuxTrackItem(file, "Аудио");
        var notified = new List<string?>();
        track.PropertyChanged += (object? sender, PropertyChangedEventArgs e) => notified.Add(e.PropertyName);

        // Act
        var structure = new MediaStructure { FilePath = file.FilePath };
        structure.Tracks.Add(new MediaTrack { TrackId = 0, TrackType = "audio", Name = "AniLibria" });
        file.MediaInfo = structure;

        // Assert
        track.TitleHint.Should().Be("— AniLibria");
        notified.Should().Contain(nameof(MuxTrackItem.TitleHint));
        notified.Should().Contain(nameof(MuxTrackItem.HasTitleHint));
    }
}
