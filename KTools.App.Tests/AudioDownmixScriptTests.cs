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
/// Юнит-тесты для скрипта даунмикса аудио AudioDownmixScript.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class AudioDownmixScriptTests
{
    private Mock<ILogService> _logServiceMock = null!;
    private Mock<ISettingsManager> _settingsManagerMock = null!;
    private Mock<IPathManager> _pathManagerMock = null!;
    private Mock<IDependencyManager> _dependencyManagerMock = null!;
    private Mock<IMediaProbeService> _mediaProbeServiceMock = null!;
    private Mock<IFFmpegRunner> _ffmpegRunnerMock = null!;
    private DeeRunner _deeRunner = null!;
    private AudioDownmixScript _script = null!;

    [TestInitialize]
    public void Setup()
    {
        _logServiceMock = new Mock<ILogService>();
        _settingsManagerMock = new Mock<ISettingsManager>();
        _pathManagerMock = new Mock<IPathManager>();
        _dependencyManagerMock = new Mock<IDependencyManager>();
        _mediaProbeServiceMock = new Mock<IMediaProbeService>();
        _ffmpegRunnerMock = new Mock<IFFmpegRunner>();
        _deeRunner = new DeeRunner(_logServiceMock.Object, _ffmpegRunnerMock.Object, _pathManagerMock.Object);

        _script = new AudioDownmixScript(
            _logServiceMock.Object,
            _settingsManagerMock.Object,
            _pathManagerMock.Object,
            _dependencyManagerMock.Object,
            _mediaProbeServiceMock.Object,
            _ffmpegRunnerMock.Object,
            _deeRunner
        );
    }

    /// <summary>
    /// Проверяет базовые свойства скрипта даунмикса.
    /// </summary>
    [TestMethod]
    public void ScriptProperties_VerifyCorrectValues()
    {
        _script.Category.Should().Be("Аудио");
        _script.IconName.Should().Be(AppConstants.ScriptIcons.AudioDownmix);
    }

    /// <summary>
    /// Проверяет, что при выполнении даунмикса в режиме FFmpeg зондируется длительность,
    /// вычисляется и передается прогресс через callback.
    /// </summary>
    [TestMethod]
    public async System.Threading.Tasks.Task ExecuteSingleAsync_FFmpegMode_ParsesProgressAndReportsToCallback()
    {
        // Arrange
        string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"downmix_test_{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(tempDir);
        string tempSourceFile = System.IO.Path.Combine(tempDir, "input.mkv");
        System.IO.File.WriteAllText(tempSourceFile, "test data");

        try
        {
            var settings = new Dictionary<string, object>
            {
                { "DownmixMode", "FFmpeg (EBU R128)" },
                { "OutputFormat", "AAC" },
                { "Bitrate", "256" },
                { "Suffix", "_stereo" },
                { "DeleteOriginal", false }
            };

            var mediaStructure = new MediaStructure
            {
                FilePath = tempSourceFile,
                Duration = 200.0
            };

            _mediaProbeServiceMock
                .Setup(m => m.ProbeAsync(tempSourceFile))
                .ReturnsAsync(mediaStructure);

            _ffmpegRunnerMock
                .Setup(f => f.RunAsync(
                    tempSourceFile,
                    It.IsAny<string>(),
                    It.IsAny<List<string>>(),
                    It.IsAny<List<string>>(),
                    It.IsAny<bool>(),
                    200.0,
                    It.IsAny<Action<ProgressInfo>>(),
                    It.IsAny<System.Threading.CancellationToken>()))
                .Callback<string, string, List<string>, List<string>, bool, double, Action<ProgressInfo>, System.Threading.CancellationToken>(
                    (input, output, extra, inputArgs, overwrite, duration, onProgress, token) =>
                    {
                        // Имитируем передачу распарсенного прогресса даунмикса от FFmpeg
                        var progressInfo = new ProgressInfo(100.0, 50.0, Fps: null, Bitrate: "256kbits/s", Speed: 5.0, Eta: "00:20");
                        onProgress?.Invoke(progressInfo);
                        
                        // Имитируем создание выходного файла
                        System.IO.File.WriteAllText(output, "output data");
                    })
                .ReturnsAsync(true);

            var reportedCallbacks = new List<(int FileIdx, int Total, string Msg, double? Pct)>();

            // Act
            var results = await _script.ExecuteSingleAsync(
                tempSourceFile,
                settings,
                outputPath: null,
                progressCallback: (fileIdx, total, msg, pct, fps, bitrate) =>
                {
                    reportedCallbacks.Add((fileIdx, total, msg, pct));
                },
                fileIndex: 0,
                totalCount: 1);

            // Assert
            _mediaProbeServiceMock.Verify(m => m.ProbeAsync(tempSourceFile), Times.Once);
            _ffmpegRunnerMock.Verify(f => f.RunAsync(
                tempSourceFile,
                It.IsAny<string>(),
                It.IsAny<List<string>>(),
                null,
                false,
                200.0,
                It.IsAny<Action<ProgressInfo>>(),
                It.IsAny<System.Threading.CancellationToken>()), Times.Once);

            reportedCallbacks.Should().Contain(c => c.Msg.Contains("Даунмикс | 50.0%") || c.Msg.Contains("Даунмикс | 50,0%"));
            results.Should().Contain(r => r.Contains("✅ Даунмикс выполнен"));
        }
        finally
        {
            if (System.IO.Directory.Exists(tempDir))
            {
                System.IO.Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
