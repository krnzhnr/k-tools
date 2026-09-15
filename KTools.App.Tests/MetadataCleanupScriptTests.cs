// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Infrastructure;
using KTools_App.Scripts;
using KTools_App.Services.Contracts;

namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты для скрипта очистки метаданных MetadataCleanupScript.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class MetadataCleanupScriptTests
{
    private Mock<ILogService> _logServiceMock = null!;
    private Mock<ISettingsManager> _settingsManagerMock = null!;
    private Mock<IPathManager> _pathManagerMock = null!;
    private Mock<IFFmpegRunner> _ffmpegRunnerMock = null!;
    private Mock<IMediaProbeService> _mediaProbeServiceMock = null!;
    private MetadataCleanupScript _script = null!;

    [TestInitialize]
    public void Setup()
    {
        _logServiceMock = new Mock<ILogService>();
        _settingsManagerMock = new Mock<ISettingsManager>();
        _pathManagerMock = new Mock<IPathManager>();
        _ffmpegRunnerMock = new Mock<IFFmpegRunner>();
        _mediaProbeServiceMock = new Mock<IMediaProbeService>();

        _script = new MetadataCleanupScript(
            _logServiceMock.Object,
            _settingsManagerMock.Object,
            _pathManagerMock.Object,
            _ffmpegRunnerMock.Object,
            _mediaProbeServiceMock.Object
        );
    }

    /// <summary>
    /// Проверяет базовые свойства скрипта (имя, категорию, иконку, расширения).
    /// </summary>
    [TestMethod]
    public void ScriptProperties_VerifyCorrectValues()
    {
        // Assert
        _script.Category.Should().Be("Видео");
        _script.IconName.Should().Be(AppConstants.ScriptIcons.MetadataCleanup);
        _script.FileExtensions.Should().Contain(new[] { ".mkv", ".mp4", ".avi", ".mp3", ".flac" });
    }

    /// <summary>
    /// Проверяет валидность схемы настроек скрипта.
    /// </summary>
    [TestMethod]
    public void SettingsSchema_ContainsOverwriteAndOptionToDelete()
    {
        // Act
        var schema = _script.SettingsSchema;

        // Assert
        schema.Should().NotBeNull();
        schema.Should().HaveCount(2);

        var overwriteField = schema.Find(f => f.Key == "overwrite_source");
        overwriteField.Should().NotBeNull();
        overwriteField!.Type.Should().Be(SettingType.Checkbox);
        overwriteField.DefaultValue.Should().Be(false);

        var deleteOriginalField = schema.Find(f => f.Key == "delete_source");
        deleteOriginalField.Should().NotBeNull();
        deleteOriginalField!.Type.Should().Be(SettingType.Checkbox);
        deleteOriginalField.DefaultValue.Should().Be(false);
    }

    /// <summary>
    /// Проверяет выполнение успешной очистки метаданных через IFFmpegRunner.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_SuccessfulRun_ReturnsSuccess()
    {
        // Arrange
        string tempSourceFile = Path.GetTempFileName();
        string tempOutputDir = Path.GetDirectoryName(tempSourceFile) ?? AppContext.BaseDirectory;

        _settingsManagerMock.Setup(s => s.GetSetting("General", "OverwriteExisting", false)).Returns(true);
        _ffmpegRunnerMock.Setup(f => f.RunAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<List<string>>(),
            It.IsAny<List<string>>(),
            It.IsAny<bool>(),
            It.IsAny<double>(),
            It.IsAny<Action<ProgressInfo>>(),
            It.IsAny<System.Threading.CancellationToken>()
        )).ReturnsAsync(true);

        var settings = new Dictionary<string, object>
        {
            { "overwrite_source", false },
            { "delete_source", false }
        };

        try
        {
            // Act
            var results = await _script.ExecuteSingleAsync(
                tempSourceFile,
                settings,
                tempOutputDir,
                (idx, total, status, pct, fps, bit) => { },
                0,
                1
            );

            // Assert
            results.Should().Contain(s => s.Contains("Очищены метаданные"));
        }
        finally
        {
            if (File.Exists(tempSourceFile)) File.Delete(tempSourceFile);
        }
    }
}

