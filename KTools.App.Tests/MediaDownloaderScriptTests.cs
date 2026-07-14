// -*- coding: utf-8 -*-
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using Moq;
using KTools_App.Scripts;
using KTools_App.Core;
using KTools_App.Services.Contracts;

namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты для скрипта загрузки медиа MediaDownloaderScript.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class MediaDownloaderScriptTests
{
    private Mock<ILogService> _logServiceMock = null!;
    private Mock<ISettingsManager> _settingsManagerMock = null!;
    private Mock<IPathManager> _pathManagerMock = null!;
    private MediaDownloaderScript _script = null!;

    [TestInitialize]
    public void Setup()
    {
        _logServiceMock = new Mock<ILogService>();
        _settingsManagerMock = new Mock<ISettingsManager>();
        _pathManagerMock = new Mock<IPathManager>();

        _script = new MediaDownloaderScript(
            _logServiceMock.Object,
            _settingsManagerMock.Object,
            _pathManagerMock.Object
        );
    }

    /// <summary>
    /// Проверяет базовые свойства скрипта загрузки медиа.
    /// </summary>
    [TestMethod]
    public void ScriptProperties_VerifyCorrectValues()
    {
        _script.Category.Should().Be("Сеть");
        _script.IconName.Should().Be(AppConstants.ScriptIcons.MediaDownloader);
        _script.FirstTabHeader.Should().Be("Загрузка");
        _script.ShowUrlInputBar.Should().BeTrue();
        _script.RequiredDependencies.Should().Contain("yt-dlp");
        _script.RequiredDependencies.Should().Contain("ffmpeg");
    }
}
