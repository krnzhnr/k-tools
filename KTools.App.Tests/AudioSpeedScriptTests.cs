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
    /// Проверяет схему настроек и наличие режима 23.976 ➔ 24.000.
    /// </summary>
    [TestMethod]
    public void SettingsSchema_Contains23976To24Mode()
    {
        var schema = _script.SettingsSchema;
        schema.Should().NotBeNull();
        var speedField = schema.Find(f => f.Key == "SpeedMode");
        speedField.Should().NotBeNull();
        speedField!.Options.Should().Contain("23.976 ➔ 24.000");
    }

    /// <summary>
    /// Проверяет передачу аргументов eac3to при выборе режима 23.976 ➔ 24.000.
    /// </summary>
    [TestMethod]
    public async System.Threading.Tasks.Task ExecuteSingleAsync_Custom23976To24_PassesCorrectEac3toOptions()
    {
        // Arrange
        _dependencyManagerMock.Setup(d => d.IsInstalled("eac3to")).Returns(true);
        _pathManagerMock.Setup(p => p.GetShortPath(Moq.It.IsAny<string>())).Returns<string>(s => s);

        string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "KTools_AudioSpeed23976_" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(tempDir);
        string tempFile = System.IO.Path.Combine(tempDir, "test_audio.wav");
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
            ["SpeedMode"] = "23.976 ➔ 24.000",
            ["OutputFormat"] = "WAV",
            ["DeleteOriginal"] = false
        };

        try
        {
            // Act
            var result = await _script.ExecuteSingleAsync(
                tempFile,
                settings,
                tempDir,
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
            if (System.IO.Directory.Exists(tempDir))
            {
                System.IO.Directory.Delete(tempDir, true);
            }
        }
    }

    /// <summary>
    /// Проверяет схему настроек и наличие режима 29.970 ➔ 23.976.
    /// </summary>
    [TestMethod]
    public void SettingsSchema_Contains29970To23976Mode()
    {
        var schema = _script.SettingsSchema;
        schema.Should().NotBeNull();
        var speedField = schema.Find(f => f.Key == "SpeedMode");
        speedField.Should().NotBeNull();
        speedField!.Options.Should().Contain("29.970 ➔ 23.976");
    }

    /// <summary>
    /// Проверяет новое оформление настройки скорости: лейбл с FPS, пункты вида «частота ➔ частота» и обновлённый дефолт.
    /// </summary>
    [TestMethod]
    public void SettingsSchema_RenamedFpsMode_ContainsFpsLabelAndArrowOptions()
    {
        // Arrange и Act
        var schema = _script.SettingsSchema;
        var speedField = schema.Find(f => f.Key == "SpeedMode");

        // Assert
        speedField.Should().NotBeNull();
        speedField!.Label.Should().Be("Режим преобразования FPS");
        speedField.DefaultValue?.ToString().Should().Be("25.000 ➔ 23.976");
        speedField.Options.Should().BeEquivalentTo(
            "25.000 ➔ 23.976",
            "23.976 ➔ 25.000",
            "24.000 ➔ 23.976",
            "23.976 ➔ 24.000",
            "25.000 ➔ 24.000",
            "29.970 ➔ 23.976");
        string joined = string.Join("\n", speedField.Options);
        joined.Should().NotContain("→");
        joined.Should().NotContain("Slowdown");
        joined.Should().NotContain("Speedup");
        joined.Should().NotContain("Custom");
    }

    /// <summary>
    /// Проверяет миграцию сохранённых настроек: старые значения режимов дают те же аргументы eac3to, что и новые.
    /// </summary>
    [TestMethod]
    public async System.Threading.Tasks.Task ExecuteSingleAsync_LegacySpeedModes_MapToCorrectEac3toOptions()
    {
        // Arrange: старые названия режимов и ожидаемые флаги eac3to для каждого.
        var legacyCases = new Dictionary<string, string[]>
        {
            ["Slowdown (25.000 → 23.976)"] = new[] { "-slowdown" },
            ["Speedup (23.976 → 25.000)"] = new[] { "-speedup" },
            ["Custom (24.000 → 23.976)"] = new[] { "-24.000", "-slowdown" },
            ["Custom (23.976 → 24.000)"] = new[] { "-23.976", "-changeTo24.000" },
            ["Custom (25.000 → 24.000)"] = new[] { "-25.000", "-changeTo24.000" },
            ["Custom (29.970 → 23.976)"] = new[] { "-29.970", "-changeTo23.976" }
        };

        foreach (var legacyCase in legacyCases)
        {
            _dependencyManagerMock.Setup(d => d.IsInstalled("eac3to")).Returns(true);
            _pathManagerMock.Setup(p => p.GetShortPath(Moq.It.IsAny<string>())).Returns<string>(s => s);

            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "KTools_AudioSpeedLegacy_" + System.Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(tempDir);
            string tempFile = System.IO.Path.Combine(tempDir, "test_audio.wav");
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
                ["SpeedMode"] = legacyCase.Key,
                ["OutputFormat"] = "WAV",
                ["DeleteOriginal"] = false
            };

            try
            {
                // Act
                var result = await _script.ExecuteSingleAsync(
                    tempFile,
                    settings,
                    tempDir,
                    (idx, total, status, pct, fps, bit) => { },
                    0,
                    1);

                // Assert: старый режим мигрировал и дал корректные флаги eac3to.
                result.Status.Should().Be(KTools_App.Models.ExecutionStatus.Succeeded, $"старый режим '{legacyCase.Key}' обязан мигрировать в новый");
                capturedArgs.Should().NotBeNull();
                foreach (string expectedFlag in legacyCase.Value)
                {
                    capturedArgs.Should().Contain(expectedFlag);
                }
            }
            finally
            {
                if (System.IO.Directory.Exists(tempDir))
                {
                    System.IO.Directory.Delete(tempDir, true);
                }
            }
        }
    }

    /// <summary>
    /// Проверяет передачу аргументов eac3to при выборе режима 29.970 ➔ 23.976.
    /// </summary>
    [TestMethod]
    public async System.Threading.Tasks.Task ExecuteSingleAsync_Custom29970To23976_PassesCorrectEac3toOptions()
    {
        // Arrange
        _dependencyManagerMock.Setup(d => d.IsInstalled("eac3to")).Returns(true);
        _pathManagerMock.Setup(p => p.GetShortPath(Moq.It.IsAny<string>())).Returns<string>(s => s);

        string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "KTools_AudioSpeed29970_" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(tempDir);
        string tempFile = System.IO.Path.Combine(tempDir, "test_audio.wav");
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
            ["SpeedMode"] = "29.970 ➔ 23.976",
            ["OutputFormat"] = "WAV",
            ["DeleteOriginal"] = false
        };

        try
        {
            // Act
            var result = await _script.ExecuteSingleAsync(
                tempFile,
                settings,
                tempDir,
                (idx, total, status, pct, fps, bit) => { },
                0,
                1);

            // Assert
            result.Status.Should().Be(KTools_App.Models.ExecutionStatus.Succeeded);
            capturedArgs.Should().NotBeNull();
            capturedArgs.Should().Contain("-29.970");
            capturedArgs.Should().Contain("-changeTo23.976");
        }
        finally
        {
            if (System.IO.Directory.Exists(tempDir))
            {
                System.IO.Directory.Delete(tempDir, true);
            }
        }
    }
}

