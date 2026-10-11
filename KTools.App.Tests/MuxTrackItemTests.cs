// -*- coding: utf-8 -*-
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Models;
using KTools_App.UI.Controls;

namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты обёртки дорожки таблицы сборки MKV: хинт заголовка и редактор аудио.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class MuxTrackItemTests
{
    /// <summary>
    /// Создаёт аудиофайл очереди с метаданными зонда.
    /// </summary>
    private static FileQueueItem CreateAudioFile(string trackName)
    {
        var file = new FileQueueItem("C:\\Видео\\s01e01.mka");
        var structure = new MediaStructure { FilePath = file.FilePath };
        structure.Tracks.Add(new MediaTrack { TrackId = 0, TrackType = "audio", Name = trackName });
        file.MediaInfo = structure;
        return file;
    }

    /// <summary>
    /// Проверяет показ встроенного заголовка и скрытие подписи у аудио (вместо неё поле ввода).
    /// </summary>
    [TestMethod]
    public void TitleHint_ProbedTitle_HidesHintForAudio()
    {
        // Arrange
        var item = new MuxTrackItem(CreateAudioFile("Встроенный"), "Аудио");

        // Act / Assert
        item.TitleHint.Should().Be("— Встроенный");
        item.HasTitleHint.Should().BeTrue();
        item.ShowTitleHint.Should().BeFalse();
        item.AudioTitlePlaceholder.Should().Be("Встроенный");
    }

    /// <summary>
    /// Проверяет приоритет ручного заголовка над встроенным и реакцию на его смену.
    /// </summary>
    [TestMethod]
    public void TitleHint_ManualTitle_WinsOverProbed()
    {
        // Arrange
        var file = CreateAudioFile("Встроенный");
        var item = new MuxTrackItem(file, "Аудио");

        // Act
        file.MuxAudioTitle = "Ручной";

        // Assert
        item.TitleHint.Should().Be("— Ручной");
        item.HasTitleHint.Should().BeTrue();
        item.ShowTitleHint.Should().BeFalse();
        item.AudioTitlePlaceholder.Should().Be("Встроенный");
    }

    /// <summary>
    /// Проверяет показ подписи заголовка у субтитров и нейтральную подсказку без метаданных.
    /// </summary>
    [TestMethod]
    public void TitleHint_SubsRow_ShowsHint()
    {
        // Arrange
        var file = new FileQueueItem("C:\\Видео\\s01e01.srt");
        var structure = new MediaStructure { FilePath = file.FilePath };
        structure.Tracks.Add(new MediaTrack { TrackId = 0, TrackType = "subtitles", Name = "Полные" });
        file.MediaInfo = structure;
        var item = new MuxTrackItem(file, "Полные", showRoleSelector: true);

        // Act / Assert
        item.IsAudioTrack.Should().BeFalse();
        item.TitleHint.Should().Be("— Полные");
        item.ShowTitleHint.Should().BeTrue();
    }

    /// <summary>
    /// Проверяет пустой хинт и нейтральную подсказку при отсутствии заголовков.
    /// </summary>
    [TestMethod]
    public void TitleHint_NoTitles_HidesHintWithFallbackPlaceholder()
    {
        // Arrange
        var item = new MuxTrackItem(new FileQueueItem("C:\\Видео\\s01e01.mka"), "Аудио");

        // Act / Assert
        item.TitleHint.Should().BeEmpty();
        item.HasTitleHint.Should().BeFalse();
        item.ShowTitleHint.Should().BeFalse();
        item.AudioTitlePlaceholder.Should().Be("Заголовок…");
    }

    /// <summary>
    /// Проверяет обновление хинта при позднем появлении метаданных зонда.
    /// </summary>
    [TestMethod]
    public void TitleHint_MediaInfoArrivesLate_UpdatesHint()
    {
        // Arrange
        var file = new FileQueueItem("C:\\Видео\\s01e01.mka");
        var item = new MuxTrackItem(file, "Аудио");
        item.TitleHint.Should().BeEmpty();

        // Act
        var structure = new MediaStructure { FilePath = file.FilePath };
        structure.Tracks.Add(new MediaTrack { TrackId = 0, TrackType = "audio", Name = "Поздний" });
        file.MediaInfo = structure;

        // Assert
        item.TitleHint.Should().Be("— Поздний");
        item.AudioTitlePlaceholder.Should().Be("Поздний");
    }
}
