// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using KTools_App.Services.Implementations;

namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты службы диалогов без UI-хоста: деградация при отсутствии XamlRoot
/// и проверка аргументов до обращений к WinUI.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class DialogServiceTests
{
    /// <summary>
    /// Проверяет тихий возврат null из диалога заголовка без главного окна.
    /// </summary>
    [TestMethod]
    public async Task AskAudioTitleAsync_NullXamlRoot_ReturnsNull()
    {
        // Arrange
        var service = new DialogService();

        // Act
        string? title = await service.AskAudioTitleAsync(
            new List<string> { "s01e01.mka" },
            "Дефолт",
            new List<string> { "Дубляж" });

        // Assert
        title.Should().BeNull();
    }

    /// <summary>
    /// Проверяет тихий возврат null из диалога переименования без главного окна.
    /// </summary>
    [TestMethod]
    public async Task RenameAudioTitleAsync_NullXamlRoot_ReturnsNull()
    {
        // Arrange
        var service = new DialogService();

        // Act
        string? title = await service.RenameAudioTitleAsync(
            "Озвучка1",
            3,
            new List<string> { "Дубляж" });

        // Assert
        title.Should().BeNull();
    }

    /// <summary>
    /// Проверяет отклонение пустого списка заготовок до обращений к WinUI.
    /// </summary>
    [TestMethod]
    public async Task AskAudioTitleAsync_NullPresets_ThrowsArgumentNull()
    {
        // Arrange
        var service = new DialogService();

        // Act
        Func<Task> act = () => service.AskAudioTitleAsync(
            new List<string> { "s01e01.mka" },
            string.Empty,
            null!);

        // Assert
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    /// <summary>
    /// Проверяет отклонение пустого списка заготовок в переименовании.
    /// </summary>
    [TestMethod]
    public async Task RenameAudioTitleAsync_NullPresets_ThrowsArgumentNull()
    {
        // Arrange
        var service = new DialogService();

        // Act
        Func<Task> act = () => service.RenameAudioTitleAsync("Озвучка1", 1, null!);

        // Assert
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    /// <summary>
    /// Проверяет возврат null при пустом списке дорожек субтитров без показа диалога.
    /// </summary>
    [TestMethod]
    public async Task ChooseSubtitleTrackAsync_EmptyTracks_ReturnsNull()
    {
        // Arrange
        var service = new DialogService();

        // Act
        var track = await service.ChooseSubtitleTrackAsync(
            "s01e01.mkv",
            new List<KTools_App.Core.MediaTrack>());

        // Assert
        track.Should().BeNull();
    }
}
