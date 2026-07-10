// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Scripts;
using KTools_App.Services.Contracts;
using KTools_App.Infrastructure;

namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты для скрипта конвертации контейнеров ContainerConversionScript.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class ContainerConversionScriptTests
{
    private Mock<ILogService> _logServiceMock = null!;
    private Mock<ISettingsManager> _settingsManagerMock = null!;
    private Mock<IPathManager> _pathManagerMock = null!;
    private Mock<IFFmpegRunner> _ffmpegRunnerMock = null!;
    private ContainerConversionScript _script = null!;

    [TestInitialize]
    public void Setup()
    {
        _logServiceMock = new Mock<ILogService>();
        _settingsManagerMock = new Mock<ISettingsManager>();
        _pathManagerMock = new Mock<IPathManager>();
        _ffmpegRunnerMock = new Mock<IFFmpegRunner>();

        _script = new ContainerConversionScript(
            _logServiceMock.Object,
            _settingsManagerMock.Object,
            _pathManagerMock.Object,
            _ffmpegRunnerMock.Object
        );
    }

    /// <summary>
    /// Проверяет базовые свойства скрипта.
    /// </summary>
    [TestMethod]
    public void ScriptProperties_VerifyCorrectValues()
    {
        _script.Category.Should().Be("Видео");
        _script.IconName.Should().Be("forward");
        _script.RequiredDependencies.Should().Contain("ffmpeg");
    }

    /// <summary>
    /// Проверяет валидность схемы настроек.
    /// </summary>
    [TestMethod]
    public void SettingsSchema_VerifyFields()
    {
        var schema = _script.SettingsSchema;
        schema.Should().NotBeNull();

        var targetFormat = schema.Find(f => f.Key == "target_format");
        targetFormat.Should().NotBeNull();
        targetFormat!.Type.Should().Be(SettingType.Combo);
        targetFormat.DefaultValue.Should().Be("MP4");

        var deleteOriginal = schema.Find(f => f.Key == "delete_original");
        deleteOriginal.Should().NotBeNull();
        deleteOriginal!.Type.Should().Be(SettingType.Checkbox);
        deleteOriginal.DefaultValue.Should().Be(false);
    }

    /// <summary>
    /// Проверяет успешную конвертацию при совместимых кодеках (копирование потоков -c copy).
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_CompatibleCodecs_CopiesStreams()
    {
        // Arrange
        string uniqueId = Guid.NewGuid().ToString("N");
        string tempSourceFile = Path.Combine(Path.GetTempPath(), $"source_{uniqueId}.avi");
        File.WriteAllText(tempSourceFile, "dummy");
        string tempOutputDir = Path.GetTempPath();
        string expectedDest = Path.Combine(tempOutputDir, $"source_{uniqueId}.mp4");

        // Имитируем JSON-ответ ffprobe с совместимыми кодеками для MP4 (h264 и aac)
        string jsonStr = @"{
            ""streams"": [
                { ""codec_type"": ""video"", ""codec_name"": ""h264"" },
                { ""codec_type"": ""audio"", ""codec_name"": ""aac"" }
            ],
            ""format"": { ""duration"": ""120.5"" }
        }";
        var jsonDoc = JsonDocument.Parse(jsonStr);

        _ffmpegRunnerMock.Setup(r => r.GetVideoInfoAsync(tempSourceFile)).ReturnsAsync(jsonDoc);

        List<string>? capturedExtraArgs = null;
        _ffmpegRunnerMock.Setup(r => r.RunAsync(
            tempSourceFile, It.IsAny<string>(), It.IsAny<List<string>>(), null, false, 120.5, It.IsAny<Action<ProgressInfo>>(), It.IsAny<CancellationToken>()
        )).Callback<string, string, List<string>, List<string>, bool, double, Action<ProgressInfo>, CancellationToken>(
            (inP, outP, extArgs, inArgs, ovr, dur, prog, ct) => capturedExtraArgs = extArgs
        ).ReturnsAsync(true);

        var settings = new Dictionary<string, object>
        {
            { "target_format", "MP4" },
            { "delete_original", false }
        };

        try
        {
            // Act
            var results = await _script.ExecuteSingleAsync(tempSourceFile, settings, tempOutputDir, (idx, total, status, pct, fps, bit) => { }, 0, 1);

            // Assert
            results.Should().Contain(s => s.Contains("✅ Конвертирован"));
            capturedExtraArgs.Should().NotBeNull();
            capturedExtraArgs.Should().Contain("-c");
            capturedExtraArgs.Should().Contain("copy");
        }
        finally
        {
            if (File.Exists(tempSourceFile)) File.Delete(tempSourceFile);
            if (File.Exists(expectedDest)) File.Delete(expectedDest);
        }
    }

    /// <summary>
    /// Проверяет отмену конвертации при несовместимости кодека с целевым контейнером.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_IncompatibleCodecs_SkipsConversion()
    {
        // Arrange
        string tempSourceFile = Path.Combine(Path.GetTempPath(), $"source_incompatible_{Guid.NewGuid():N}.mkv");
        File.WriteAllText(tempSourceFile, "dummy");
        string tempOutputDir = Path.GetTempPath();

        // VP9 и FLAC не поддерживаются в MP4 контейнере по правилам нашего CheckCompatibility
        string jsonStr = @"{
            ""streams"": [
                { ""codec_type"": ""video"", ""codec_name"": ""vp9"" },
                { ""codec_type"": ""audio"", ""codec_name"": ""flac"" }
            ],
            ""format"": { ""duration"": ""60.0"" }
        }";
        var jsonDoc = JsonDocument.Parse(jsonStr);

        _ffmpegRunnerMock.Setup(r => r.GetVideoInfoAsync(tempSourceFile)).ReturnsAsync(jsonDoc);

        var settings = new Dictionary<string, object>
        {
            { "target_format", "MP4" },
            { "delete_original", false }
        };

        try
        {
            // Act
            var results = await _script.ExecuteSingleAsync(tempSourceFile, settings, tempOutputDir, (idx, total, status, pct, fps, bit) => { }, 0, 1);

            // Assert
            results.Should().Contain(s => s.Contains("ПРОПУСК (требуется перекодирование)"));
            // Убеждаемся, что запуск FFmpeg не производился
            _ffmpegRunnerMock.Verify(r => r.RunAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<List<string>>(),
                It.IsAny<bool>(), It.IsAny<double>(), It.IsAny<Action<ProgressInfo>>(), It.IsAny<CancellationToken>()
            ), Times.Never);
        }
        finally
        {
            if (File.Exists(tempSourceFile)) File.Delete(tempSourceFile);
        }
    }

    /// <summary>
    /// Проверяет пропуск операции, если исходный файл уже в целевом формате.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_SameFormat_SkipsConversion()
    {
        // Arrange
        string tempSourceFile = Path.Combine(Path.GetTempPath(), $"source_{Guid.NewGuid():N}.mp4");
        File.WriteAllText(tempSourceFile, "dummy");
        string tempOutputDir = Path.GetTempPath();

        var settings = new Dictionary<string, object>
        {
            { "target_format", "MP4" },
            { "delete_original", false }
        };

        try
        {
            // Act
            var results = await _script.ExecuteSingleAsync(tempSourceFile, settings, tempOutputDir, (idx, total, status, pct, fps, bit) => { }, 0, 1);

            // Assert
            results.Should().Contain(s => s.Contains("ПРОПУСК (уже MP4)"));
            _ffmpegRunnerMock.Verify(r => r.GetVideoInfoAsync(It.IsAny<string>()), Times.Never);
        }
        finally
        {
            if (File.Exists(tempSourceFile)) File.Delete(tempSourceFile);
        }
    }

    /// <summary>
    /// Проверяет, что при отсутствии метаданных (ffprobe null) совместимость принимается по умолчанию.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_NullMetadata_CompatibleByDefault()
    {
        // Arrange
        string uniqueId = Guid.NewGuid().ToString("N");
        string tempSourceFile = Path.Combine(Path.GetTempPath(), $"source_{uniqueId}.avi");
        File.WriteAllText(tempSourceFile, "dummy");
        string tempOutputDir = Path.GetTempPath();
        string expectedDest = Path.Combine(tempOutputDir, $"source_{uniqueId}.mp4");

        _ffmpegRunnerMock.Setup(r => r.GetVideoInfoAsync(tempSourceFile)).ReturnsAsync((JsonDocument?)null);
        _ffmpegRunnerMock.Setup(r => r.RunAsync(
            tempSourceFile, It.IsAny<string>(), It.IsAny<List<string>>(), null, false, 0.0, It.IsAny<Action<ProgressInfo>>(), It.IsAny<CancellationToken>()
        )).ReturnsAsync(true);

        var settings = new Dictionary<string, object>
        {
            { "target_format", "MP4" },
            { "delete_original", false }
        };

        try
        {
            // Act
            var results = await _script.ExecuteSingleAsync(tempSourceFile, settings, tempOutputDir, (idx, total, status, pct, fps, bit) => { }, 0, 1);

            // Assert
            results.Should().Contain(s => s.Contains("✅ Конвертирован"));
        }
        finally
        {
            if (File.Exists(tempSourceFile)) File.Delete(tempSourceFile);
            if (File.Exists(expectedDest)) File.Delete(expectedDest);
        }
    }

    /// <summary>
    /// Проверяет перепаковку M2TS в MP4 при совместимых кодеках.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_M2tsToMp4_CompatibleCodecs_CopiesStreams()
    {
        // Arrange
        string uniqueId = Guid.NewGuid().ToString("N");
        string tempSourceFile = Path.Combine(Path.GetTempPath(), $"source_{uniqueId}.m2ts");
        File.WriteAllText(tempSourceFile, "dummy");
        string tempOutputDir = Path.GetTempPath();
        string expectedDest = Path.Combine(tempOutputDir, $"source_{uniqueId}.mp4");

        string jsonStr = @"{
            ""streams"": [
                { ""codec_type"": ""video"", ""codec_name"": ""h264"" },
                { ""codec_type"": ""audio"", ""codec_name"": ""aac"" }
            ],
            ""format"": { ""duration"": ""10.0"" }
        }";
        var jsonDoc = JsonDocument.Parse(jsonStr);

        _ffmpegRunnerMock.Setup(r => r.GetVideoInfoAsync(tempSourceFile)).ReturnsAsync(jsonDoc);
        _ffmpegRunnerMock.Setup(r => r.RunAsync(
            tempSourceFile, It.IsAny<string>(), It.IsAny<List<string>>(), null, false, 10.0, It.IsAny<Action<ProgressInfo>>(), It.IsAny<CancellationToken>()
        )).ReturnsAsync(true);

        var settings = new Dictionary<string, object>
        {
            { "target_format", "MP4" },
            { "delete_original", false }
        };

        try
        {
            // Act
            var results = await _script.ExecuteSingleAsync(tempSourceFile, settings, tempOutputDir, (idx, total, status, pct, fps, bit) => { }, 0, 1);

            // Assert
            results.Should().Contain(s => s.Contains("✅ Конвертирован"));
        }
        finally
        {
            if (File.Exists(tempSourceFile)) File.Delete(tempSourceFile);
            if (File.Exists(expectedDest)) File.Delete(expectedDest);
        }
    }

    /// <summary>
    /// Проверяет, что GIF формат всегда требует перекодирования.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_Gif_RequiresEncoding_SkipsConversion()
    {
        // Arrange
        string uniqueId = Guid.NewGuid().ToString("N");
        string tempSourceFile = Path.Combine(Path.GetTempPath(), $"source_{uniqueId}.gif");
        File.WriteAllText(tempSourceFile, "dummy");
        string tempOutputDir = Path.GetTempPath();

        string jsonStr = @"{
            ""streams"": [
                { ""codec_type"": ""video"", ""codec_name"": ""gif"" }
            ],
            ""format"": { ""duration"": ""5.0"" }
        }";
        var jsonDoc = JsonDocument.Parse(jsonStr);

        _ffmpegRunnerMock.Setup(r => r.GetVideoInfoAsync(tempSourceFile)).ReturnsAsync(jsonDoc);

        var settings = new Dictionary<string, object>
        {
            { "target_format", "MP4" },
            { "delete_original", false }
        };

        try
        {
            // Act
            var results = await _script.ExecuteSingleAsync(tempSourceFile, settings, tempOutputDir, (idx, total, status, pct, fps, bit) => { }, 0, 1);

            // Assert
            results.Should().Contain(s => s.Contains("ПРОПУСК (требуется перекодирование)"));
        }
        finally
        {
            if (File.Exists(tempSourceFile)) File.Delete(tempSourceFile);
        }
    }
}
