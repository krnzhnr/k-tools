// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using Moq;
using KTools_App.Scripts;
using KTools_App.Core;
using KTools_App.Infrastructure;
using KTools_App.Models;
using KTools_App.Services.Contracts;
using KTools_App.Tests.TestHelpers;

namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты для скрипта сдвига аудио AudioShiftScript.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class AudioShiftScriptTests
{
    private Mock<ILogService> _logServiceMock = null!;
    private Mock<ISettingsManager> _settingsManagerMock = null!;
    private Mock<IPathManager> _pathManagerMock = null!;
    private Mock<IFFmpegRunner> _ffmpegRunnerMock = null!;
    private Mock<IEac3toRunner> _eac3toRunnerMock = null!;
    private AudioShiftScript _script = null!;

    [TestInitialize]
    public void Setup()
    {
        _logServiceMock = MockBuilders.CreateLogServiceMock();
        _settingsManagerMock = MockBuilders.CreateSettingsManagerMock();
        _pathManagerMock = MockBuilders.CreatePathManagerMock();
        _ffmpegRunnerMock = new Mock<IFFmpegRunner>();
        _eac3toRunnerMock = new Mock<IEac3toRunner>();

        _script = new AudioShiftScript(
            _logServiceMock.Object,
            _settingsManagerMock.Object,
            _pathManagerMock.Object,
            _ffmpegRunnerMock.Object,
            _eac3toRunnerMock.Object,
            MockBuilders.CreateMediaProbeMock().Object);
    }

    /// <summary>
    /// Создаёт скрипт с зондом, возвращающим аудиодорожку заданного кодека.
    /// </summary>
    private AudioShiftScript CreateScriptWithCodec(string codec)
    {
        var structure = new MediaStructure { FilePath = "probe", Duration = 5.0 };
        structure.Tracks.Add(new MediaTrack
        {
            TrackId = 0,
            TrackType = "audio",
            Codec = codec,
            Channels = 2
        });

        return new AudioShiftScript(
            _logServiceMock.Object,
            _settingsManagerMock.Object,
            _pathManagerMock.Object,
            _ffmpegRunnerMock.Object,
            _eac3toRunnerMock.Object,
            MockBuilders.CreateMediaProbeMock(structure).Object);
    }

    /// <summary>
    /// Создаёт временный входной файл и возвращает его путь.
    /// </summary>
    private static string CreateTempInput(string tempDir, string fileName)
    {
        string path = Path.Combine(tempDir, fileName);
        File.WriteAllText(path, "fake audio");
        return path;
    }

    private static Dictionary<string, object> BitstreamSettings() => new()
    {
        ["ShiftMs"] = 100,
        ["ShiftDirection"] = "Вперед",
        ["OutputFormat"] = "eac3to Bitstream (Без перекодирования)"
    };

    /// <summary>
    /// Проверяет, что Bitstream-режим на Opus останавливается сразу с понятной ошибкой без вызова утилит.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_BitstreamOpusSource_FailsFastWithClearError()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), "KTools_ShiftOpus_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string inputFile = CreateTempInput(tempDir, "in.opus");
        var script = CreateScriptWithCodec("opus");

        try
        {
            // Act
            ExecutionResult result = await script.ExecuteSingleAsync(
                inputFile,
                BitstreamSettings(),
                tempDir,
                (fIdx, total, status, progress, fps, bitrate) => { },
                0,
                1);

            // Assert
            result.Status.Should().Be(ExecutionStatus.Failed);
            result.ErrorCode.Should().Be("eac3to-unsupported-codec");
            result.Messages.Should().ContainSingle(m => m.Contains("opus") && m.Contains("FLAC"));
            _eac3toRunnerMock.Verify(
                e => e.RunAsync(
                    It.IsAny<List<string>>(),
                    It.IsAny<string?>(),
                    It.IsAny<Action<double>?>(),
                    It.IsAny<CancellationToken>(),
                    It.IsAny<ProcessExecutionContext?>(),
                    It.IsAny<string?>()),
                Times.Never);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    /// <summary>
    /// Проверяет, что Bitstream-режим на AC3 по-прежнему идёт через eac3to без регрессии.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_BitstreamAc3SourceWithWorkingEac3to_Succeeds()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), "KTools_ShiftAc3_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string inputFile = CreateTempInput(tempDir, "in.ac3");
        var script = CreateScriptWithCodec("ac3");

        _eac3toRunnerMock
            .Setup(e => e.RunAsync(
                It.IsAny<List<string>>(),
                It.IsAny<string?>(),
                It.IsAny<Action<double>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<ProcessExecutionContext?>(),
                It.IsAny<string?>()))
            .Callback<List<string>, string?, Action<double>?, CancellationToken, ProcessExecutionContext?, string?>(
                (args, workingDir, onProgress, ct, ctx, artifact) =>
                {
                    string outPath = args[1].Trim('"');
                    File.WriteAllText(outPath, "shifted");
                })
            .ReturnsAsync(MockBuilders.ProcessSucceeded());

        try
        {
            // Act
            ExecutionResult result = await script.ExecuteSingleAsync(
                inputFile,
                BitstreamSettings(),
                tempDir,
                (fIdx, total, status, progress, fps, bitrate) => { },
                0,
                1);

            // Assert
            result.Status.Should().Be(ExecutionStatus.Succeeded);
            _eac3toRunnerMock.Verify(
                e => e.RunAsync(
                    It.IsAny<List<string>>(),
                    It.IsAny<string?>(),
                    It.IsAny<Action<double>?>(),
                    It.IsAny<CancellationToken>(),
                    It.IsAny<ProcessExecutionContext?>(),
                    It.IsAny<string?>()),
                Times.Once);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    /// <summary>
    /// Проверяет, что FLAC-режим на Opus работает через FFmpeg и запрет Bitstream его не затрагивает.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_FlacModeOpusSource_SucceedsViaFfmpeg()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), "KTools_ShiftFlac_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string inputFile = CreateTempInput(tempDir, "in.opus");
        var script = CreateScriptWithCodec("opus");

        _ffmpegRunnerMock
            .Setup(f => f.RunAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<bool>(),
                It.IsAny<double>(),
                It.IsAny<Action<ProgressInfo>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<ProcessExecutionContext?>()))
            .Callback<string, string?, List<string>?, List<string>?, bool, double, Action<ProgressInfo>?, CancellationToken, ProcessExecutionContext?>(
                (inputPath, outputPath, extraArgs, inputArgs, overwrite, duration, onProgress, ct, ctx) =>
                {
                    if (!string.IsNullOrEmpty(outputPath))
                    {
                        File.WriteAllText(outputPath, "shifted");
                    }
                })
            .ReturnsAsync(MockBuilders.ProcessSucceeded());

        var settings = new Dictionary<string, object>
        {
            ["ShiftMs"] = 100,
            ["ShiftDirection"] = "Вперед",
            ["OutputFormat"] = "FLAC (FFmpeg Lossless)"
        };

        try
        {
            // Act
            ExecutionResult result = await script.ExecuteSingleAsync(
                inputFile,
                settings,
                tempDir,
                (fIdx, total, status, progress, fps, bitrate) => { },
                0,
                1);

            // Assert
            result.Status.Should().Be(ExecutionStatus.Succeeded);
            _eac3toRunnerMock.Verify(
                e => e.RunAsync(
                    It.IsAny<List<string>>(),
                    It.IsAny<string?>(),
                    It.IsAny<Action<double>?>(),
                    It.IsAny<CancellationToken>(),
                    It.IsAny<ProcessExecutionContext?>(),
                    It.IsAny<string?>()),
                Times.Never);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }
}
