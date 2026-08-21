// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using Moq;
using KTools_App.Scripts;
using KTools_App.Core;
using KTools_App.Services.Contracts;
using KTools_App.Infrastructure;

namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты для скрипта кодирования аудио AudioEncodingScript.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class AudioEncodingScriptTests
{
    private Mock<ILogService> _logServiceMock = null!;
    private Mock<ISettingsManager> _settingsManagerMock = null!;
    private Mock<IPathManager> _pathManagerMock = null!;
    private Mock<IFFmpegRunner> _ffmpegRunnerMock = null!;
    private QaacRunner _qaacRunner = null!;
    private AudioEncodingScript _script = null!;

    [TestInitialize]
    public void Setup()
    {
        _logServiceMock = new Mock<ILogService>();
        _settingsManagerMock = new Mock<ISettingsManager>();
        _pathManagerMock = new Mock<IPathManager>();
        _ffmpegRunnerMock = new Mock<IFFmpegRunner>();
        _qaacRunner = new QaacRunner(_logServiceMock.Object, _pathManagerMock.Object);

        _script = new AudioEncodingScript(
            _logServiceMock.Object,
            _settingsManagerMock.Object,
            _pathManagerMock.Object,
            _ffmpegRunnerMock.Object,
            _qaacRunner
        );
    }

    /// <summary>
    /// Проверяет базовые свойства скрипта кодирования аудио.
    /// </summary>
    [TestMethod]
    public void ScriptProperties_VerifyCorrectValues()
    {
        _script.Category.Should().Be("Аудио");
        _script.IconName.Should().Be(AppConstants.ScriptIcons.AudioEncoding);
    }

    /// <summary>
    /// Проверяет, что все параметры QAAC содержат условие видимости только для формата QAAC.
    /// </summary>
    [TestMethod]
    public void SettingsSchema_QaacFields_RequireQaacTargetFormat()
    {
        var schema = _script.SettingsSchema;

        // Поле режима QAAC должно зависеть от формата QAAC
        var modeField = schema.Find(f => f.Key == "qaac_mode");
        modeField.Should().NotBeNull();
        modeField!.VisibleIfKey.Should().Be("target_format");
        modeField.VisibleIfValues.Should().Contain("QAAC");

        // Поле качества True VBR должно иметь составное условие с target_format == QAAC
        var qualityField = schema.Find(f => f.Key == "qaac_quality");
        qualityField.Should().NotBeNull();
        qualityField!.VisibilityConditions.Should().NotBeNull();
        qualityField.VisibilityConditions!.Should().Contain(c => c.Key == "target_format" && c.Values.Contains("QAAC"));
        qualityField.VisibilityConditions!.Should().Contain(c => c.Key == "qaac_mode" && c.Values.Contains("True VBR (-V)"));

        // Поле битрейта QAAC должно иметь составное условие с target_format == QAAC
        var bitrateField = schema.Find(f => f.Key == "qaac_bitrate");
        bitrateField.Should().NotBeNull();
        bitrateField!.VisibilityConditions.Should().NotBeNull();
        bitrateField.VisibilityConditions!.Should().Contain(c => c.Key == "target_format" && c.Values.Contains("QAAC"));
        bitrateField.VisibilityConditions!.Should().Contain(c => c.Key == "qaac_mode");

        // Чекбоксы no_delay и limiter также должны зависеть от QAAC
        var noDelayField = schema.Find(f => f.Key == "qaac_no_delay");
        noDelayField.Should().NotBeNull();
        noDelayField!.VisibleIfKey.Should().Be("target_format");
        noDelayField.VisibleIfValues.Should().Contain("QAAC");

        var limiterField = schema.Find(f => f.Key == "qaac_limiter");
        limiterField.Should().NotBeNull();
        limiterField!.VisibleIfKey.Should().Be("target_format");
        limiterField.VisibleIfValues.Should().Contain("QAAC");
    }

    /// <summary>
    /// Проверяет, что для моно-аудио в формате OGG битрейт 320k автоматически корректируется до 224k.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_OggMono_AdjustsBitrateTo224k()
    {
        // Arrange
        string testInput = "C:\\test\\mono_input.wav";
        string jsonInfo = """
        {
            "format": { "duration": "10.0" },
            "streams": [
                {
                    "codec_type": "audio",
                    "channels": 1
                }
            ]
        }
        """;
        var doc = System.Text.Json.JsonDocument.Parse(jsonInfo);
        _ffmpegRunnerMock.Setup(r => r.GetVideoInfoAsync(testInput))
            .ReturnsAsync(doc);

        List<string>? capturedExtraArgs = null;
        _ffmpegRunnerMock.Setup(r => r.RunAsync(
                testInput,
                It.IsAny<string>(),
                It.IsAny<List<string>>(),
                It.IsAny<bool>(),
                It.IsAny<double>(),
                It.IsAny<Action<FFmpegProgressInfo>>(),
                It.IsAny<System.Threading.CancellationToken>()))
            .Callback<string, string, List<string>, bool, double, Action<FFmpegProgressInfo>, System.Threading.CancellationToken>(
                (inPath, outPath, args, ow, dur, cb, ct) => capturedExtraArgs = args)
            .ReturnsAsync(true);

        var settings = new Dictionary<string, object>
        {
            { "target_format", "OGG" },
            { "bitrate", "320k" }
        };

        // Act
        var results = await _script.ExecuteSingleAsync(
            testInput,
            settings,
            "C:\\test\\out",
            (idx, total, msg, pct, fps, br) => { },
            1,
            1);

        // Assert
        capturedExtraArgs.Should().NotBeNull();
        capturedExtraArgs.Should().Contain("-b:a");
        int bIndex = capturedExtraArgs!.IndexOf("-b:a");
        capturedExtraArgs[bIndex + 1].Should().Be("224k");

        results.Should().Contain(r => r.Contains("224k") && r.Contains("моно"));
    }
}
