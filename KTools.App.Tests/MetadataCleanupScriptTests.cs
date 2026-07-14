// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using FluentAssertions;
using KTools_App.Core;
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
    private MetadataCleanupScript _script = null!;

    [TestInitialize]
    public void Setup()
    {
        _logServiceMock = new Mock<ILogService>();
        _settingsManagerMock = new Mock<ISettingsManager>();
        _pathManagerMock = new Mock<IPathManager>();

        _script = new MetadataCleanupScript(
            _logServiceMock.Object,
            _settingsManagerMock.Object,
            _pathManagerMock.Object
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
        _script.FileExtensions.Should().Contain(new[] { ".mkv", ".mp4", ".avi" });
    }

    /// <summary>
    /// Проверяет валидность схемы настроек скрипта.
    /// </summary>
    [TestMethod]
    public void SettingsSchema_ContainsSuffixAndOptionToDelete()
    {
        // Act
        var schema = _script.SettingsSchema;

        // Assert
        schema.Should().NotBeNull();
        schema.Should().HaveCount(2);

        var suffixField = schema.Find(f => f.Key == "Suffix");
        suffixField.Should().NotBeNull();
        suffixField!.Type.Should().Be(SettingType.Text);
        suffixField.DefaultValue.Should().Be("_cl");

        var deleteOriginalField = schema.Find(f => f.Key == "DeleteOriginal");
        deleteOriginalField.Should().NotBeNull();
        deleteOriginalField!.Type.Should().Be(SettingType.Checkbox);
        deleteOriginalField.DefaultValue.Should().Be(false);
    }

    /// <summary>
    /// Проверяет режим симуляции работы (когда FFmpeg отсутствует на диске).
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_FfmpegMissing_RunsSimulationAndOutputsFile()
    {
        // Arrange
        string tempSourceFile = Path.GetTempFileName();
        string tempOutputDir = Path.GetDirectoryName(tempSourceFile) ?? AppContext.BaseDirectory;
        string expectedOutputName = Path.GetFileNameWithoutExtension(tempSourceFile) + "_cl" + Path.GetExtension(tempSourceFile);
        string expectedOutputPath = Path.Combine(tempOutputDir, expectedOutputName);

        // Настраиваем отсутствие FFmpeg
        _pathManagerMock.Setup(p => p.GetBinaryPath("ffmpeg")).Returns("nonexistent_ffmpeg_path.exe");
        _settingsManagerMock.Setup(s => s.GetSetting("General", "OverwriteExisting", false)).Returns(true);

        var settings = new Dictionary<string, object>
        {
            { "Suffix", "_cl" },
            { "DeleteOriginal", true }
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
            results.Should().Contain(s => s.Contains("Очищены метаданные (Имитация)"));
            results.Should().Contain(s => s.Contains("Удален исходник"));
            File.Exists(expectedOutputPath).Should().BeTrue();
            File.Exists(tempSourceFile).Should().BeFalse(); // Так как DeleteOriginal = true
        }
        finally
        {
            if (File.Exists(tempSourceFile)) File.Delete(tempSourceFile);
            if (File.Exists(expectedOutputPath)) File.Delete(expectedOutputPath);
        }
    }
}
