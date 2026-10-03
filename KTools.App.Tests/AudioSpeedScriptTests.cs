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
/// Юнит-тесты для скрипта изменения скорости аудио AudioSpeedScript.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class AudioSpeedScriptTests
{
    private Mock<ILogService> _logServiceMock = null!;
    private Mock<ISettingsManager> _settingsManagerMock = null!;
    private Mock<IPathManager> _pathManagerMock = null!;
    private Mock<IDependencyManager> _dependencyManagerMock = null!;
    private Mock<IFFmpegRunner> _ffmpegRunnerMock = null!;
    private Mock<IEac3toRunner> _eac3toRunnerMock = null!;
    private AudioSpeedScript _script = null!;

    [TestInitialize]
    public void Setup()
    {
        _logServiceMock = new Mock<ILogService>();
        _settingsManagerMock = new Mock<ISettingsManager>();
        _pathManagerMock = new Mock<IPathManager>();
        _dependencyManagerMock = new Mock<IDependencyManager>();
        _ffmpegRunnerMock = new Mock<IFFmpegRunner>();
        _eac3toRunnerMock = new Mock<IEac3toRunner>();

        _script = new AudioSpeedScript(
            _logServiceMock.Object,
            _settingsManagerMock.Object,
            _pathManagerMock.Object,
            _dependencyManagerMock.Object,
            _ffmpegRunnerMock.Object,
            _eac3toRunnerMock.Object
        );
    }

    /// <summary>
    /// Проверяет базовые свойства скрипта изменения скорости.
    /// </summary>
    [TestMethod]
    public void ScriptProperties_VerifyCorrectValues()
    {
        _script.Category.Should().Be("Аудио");
        _script.IconName.Should().Be(AppConstants.ScriptIcons.AudioSpeed);
    }

    /// <summary>
    /// Проверяет схему настроек и наличие режима Custom (23.976 → 24.000).
    /// </summary>
    [TestMethod]
    public void SettingsSchema_Contains23976To24Mode()
    {
        var schema = _script.SettingsSchema;
        schema.Should().NotBeNull();
        var speedField = schema.Find(f => f.Key == "SpeedMode");
        speedField.Should().NotBeNull();
        speedField!.Options.Should().Contain("Custom (23.976 → 24.000)");
    }

    /// <summary>
    /// Проверяет передачу аргументов eac3to при выборе режима Custom (23.976 → 24.000).
    /// </summary>
    [TestMethod]
    public async System.Threading.Tasks.Task ExecuteSingleAsync_Custom23976To24_PassesCorrectEac3toOptions()
    {
        // Arrange
        _dependencyManagerMock.Setup(d => d.IsInstalled("eac3to")).Returns(true);
        _pathManagerMock.Setup(p => p.GetShortPath(Moq.It.IsAny<string>())).Returns<string>(s => s);

        string tempFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "test_audio.wav");
        await System.IO.File.WriteAllTextAsync(tempFile, "fake wav");

        List<string>? capturedArgs = null;
        _eac3toRunnerMock.Setup(e => e.RunAsync(
                Moq.It.IsAny<List<string>>(),
                Moq.It.IsAny<string?>(),
                Moq.It.IsAny<Action<double>?>(),
                Moq.It.IsAny<System.Threading.CancellationToken>(),
                Moq.It.IsAny<ProcessExecutionContext?>(),
                Moq.It.IsAny<string?>()))
            .Callback<List<string>, string?, Action<double>?, System.Threading.CancellationToken, ProcessExecutionContext?, string?>(
                (args, wd, prog, ct, ctx, artifact) =>
                {
                    capturedArgs = new List<string>(args);
                    // Имитируем успешное создание выходного файла утилитой eac3to
                    string outPath = args[1].Trim('"');
                    System.IO.File.WriteAllText(outPath, "result");
                })
            .ReturnsAsync(KTools_App.Tests.TestHelpers.MockBuilders.ProcessSucceeded());

        var settings = new Dictionary<string, object>
        {
            ["SpeedMode"] = "Custom (23.976 → 24.000)",
            ["OutputFormat"] = "WAV",
            ["DeleteOriginal"] = false
        };

        try
        {
            // Act
            var result = await _script.ExecuteSingleAsync(
                tempFile,
                settings,
                System.IO.Path.GetTempPath(),
                (idx, total, status, pct, fps, bit) => { },
                0,
                1);

            // Assert
            result.Status.Should().Be(KTools_App.Models.ExecutionStatus.Succeeded);
            capturedArgs.Should().NotBeNull();
            capturedArgs.Should().Contain("-23.976");
            capturedArgs.Should().Contain("-changeTo24.000");
        }
        finally
        {
            if (System.IO.File.Exists(tempFile))
            {
                System.IO.File.Delete(tempFile);
            }
            string expectedResult = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "test_audio_23_to_24.wav");
            if (System.IO.File.Exists(expectedResult))
            {
                System.IO.File.Delete(expectedResult);
            }
        }
    }
}

