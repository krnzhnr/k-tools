// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using Moq;
using KTools_App.Scripts;
using KTools_App.Core;
using KTools_App.Models;
using KTools_App.Services.Contracts;
using KTools_App.Infrastructure;
using KTools_App.Tests.TestHelpers;

namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты для скрипта управления потоками StreamManagementScript.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class StreamManagementScriptTests
{
    private Mock<ILogService> _logServiceMock = null!;
    private Mock<ISettingsManager> _settingsManagerMock = null!;
    private Mock<IPathManager> _pathManagerMock = null!;
    private Mock<IMediaProbeService> _mediaProbeServiceMock = null!;
    private Mock<IFFmpegRunner> _ffmpegRunnerMock = null!;
    private Mock<IMkvmergeRunner> _mkvmergeRunnerMock = null!;
    private StreamManagementScript _script = null!;

    [TestInitialize]
    public void Setup()
    {
        _logServiceMock = new Mock<ILogService>();
        _settingsManagerMock = new Mock<ISettingsManager>();
        _pathManagerMock = new Mock<IPathManager>();
        _mediaProbeServiceMock = new Mock<IMediaProbeService>();
        _ffmpegRunnerMock = new Mock<IFFmpegRunner>();
        _mkvmergeRunnerMock = new Mock<IMkvmergeRunner>();

        _script = new StreamManagementScript(
            _logServiceMock.Object,
            _settingsManagerMock.Object,
            _pathManagerMock.Object,
            _mediaProbeServiceMock.Object,
            _ffmpegRunnerMock.Object,
            _mkvmergeRunnerMock.Object
        );
    }

    /// <summary>
    /// Проверяет базовые свойства скрипта управления потоками.
    /// </summary>
    [TestMethod]
    public void ScriptProperties_VerifyCorrectValues()
    {
        _script.Category.Should().Be("Контейнеры");
        _script.IconName.Should().Be(AppConstants.ScriptIcons.StreamManagement);
    }

    /// <summary>
    /// Проверяет, что неудачная подмена оригинала не сообщается как успех:
    /// результат частичный с кодом source-replacement-failed, исходник сохраняется.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_SourceReplacementFails_ReportsPartialAndKeepsSource()
    {
        // Arrange
        using var scope = new TempDirectoryScope();
        string source = scope.CreateFile("streams.mkv", "оригинал");
        SetupStructure(source, 100);
        _settingsManagerMock.Setup(s => s.GetSetting("General", "OverwriteExisting", false)).Returns(true);
        _mkvmergeRunnerMock.Setup(m => m.RunAsync(
                It.IsAny<string>(),
                It.IsAny<List<MkvInputSource>>(),
                It.IsAny<string>(),
                It.IsAny<List<string>>(),
                It.IsAny<Action<double>>(),
                It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(MockBuilders.ProcessSucceeded());

        // Act — mkvmerge «успешен», но файл результата не создан
        ExecutionResult result = await _script.ExecuteSingleAsync(
            source,
            BuildSettings(source, true),
            outputPath: null,
            progressCallback: (idx, total, status, pct, fps, bit) => { },
            fileIndex: 0,
            totalCount: 1);

        // Assert
        result.Status.Should().Be(ExecutionStatus.PartiallySucceeded,
            "неудачная подмена оригинала не является полным успехом");
        result.ErrorCode.Should().Be("source-replacement-failed");
        result.OutputExists.Should().BeFalse("выходной файл не создан");
        result.CleanupState.Should().Be(CleanupState.Failed);
        File.Exists(source).Should().BeTrue("исходный файл обязан сохраниться");
        File.ReadAllText(source).Should().Be("оригинал");
    }

    /// <summary>
    /// Проверяет, что неудачная подмена не оставляет временных артефактов на диске.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_SourceReplacementFails_LeavesNoArtifacts()
    {
        // Arrange
        using var scope = new TempDirectoryScope();
        string source = scope.CreateFile("artifacts.mkv", "оригинал");
        SetupStructure(source, 100);
        _settingsManagerMock.Setup(s => s.GetSetting("General", "OverwriteExisting", false)).Returns(true);
        _mkvmergeRunnerMock.Setup(m => m.RunAsync(
                It.IsAny<string>(),
                It.IsAny<List<MkvInputSource>>(),
                It.IsAny<string>(),
                It.IsAny<List<string>>(),
                It.IsAny<Action<double>>(),
                It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(MockBuilders.ProcessSucceeded());

        // Act
        await _script.ExecuteSingleAsync(
            source,
            BuildSettings(source, true),
            outputPath: null,
            progressCallback: (idx, total, status, pct, fps, bit) => { },
            fileIndex: 0,
            totalCount: 1);

        // Assert
        Directory.GetFiles(scope.RootPath).Should().ContainSingle(
            "на диске остаётся только исходный файл");
    }

    /// <summary>
    /// Проверяет успешную подмену оригинала: результат оказывается по пути исходника.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_SourceReplacementSucceeds_ReturnsSucceeded()
    {
        // Arrange
        using var scope = new TempDirectoryScope();
        string source = scope.CreateFile("good.mkv", "оригинал");
        SetupStructure(source, 100);
        _settingsManagerMock.Setup(s => s.GetSetting("General", "OverwriteExisting", false)).Returns(true);
        _mkvmergeRunnerMock.Setup(m => m.RunAsync(
                It.IsAny<string>(),
                It.IsAny<List<MkvInputSource>>(),
                It.IsAny<string>(),
                It.IsAny<List<string>>(),
                It.IsAny<Action<double>>(),
                It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync((string outputPath, List<MkvInputSource> inputs, string title, List<string> extraArgs, Action<double> onProgress, System.Threading.CancellationToken token, ProcessExecutionContext? processContext) =>
            {
                File.WriteAllText(outputPath, "собрано");
                return MockBuilders.ProcessSucceeded();
            });

        // Act
        ExecutionResult result = await _script.ExecuteSingleAsync(
            source,
            BuildSettings(source, true),
            outputPath: null,
            progressCallback: (idx, total, status, pct, fps, bit) => { },
            fileIndex: 0,
            totalCount: 1);

        // Assert
        result.Status.Should().Be(ExecutionStatus.Succeeded);
        result.OutputFile.Should().Be(source);
        result.OutputExists.Should().BeTrue();
        File.ReadAllText(source).Should().Be("собрано");
    }

    /// <summary>
    /// Проверяет типизированный контракт пропуска при отсутствии выбранных дорожек.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_NoTrackSelection_ReturnsTypedSkipped()
    {
        // Arrange
        string source = "C:\\test\\no-selection.mkv";

        // Act
        ExecutionResult result = await _script.ExecuteSingleAsync(
            source,
            new Dictionary<string, object>(),
            outputPath: null,
            progressCallback: (idx, total, status, pct, fps, bit) => { },
            fileIndex: 0,
            totalCount: 1);

        // Assert
        result.Status.Should().Be(ExecutionStatus.Skipped);
        result.ErrorCode.Should().Be("no-track-selection");
        result.OutputExists.Should().BeFalse();
        result.OutputFile.Should().BeNull("без выбранных дорожек выходной файл не создаётся");
    }

    private void SetupStructure(string path, double duration)
    {
        MediaStructure structure = new() { FilePath = path, Duration = duration };
        structure.Tracks.Add(new MediaTrack { TrackId = 0, TrackType = "video", Codec = "h264" });
        structure.Tracks.Add(new MediaTrack { TrackId = 1, TrackType = "audio", Codec = "aac" });
        _mediaProbeServiceMock.Setup(p => p.ProbeAsync(path)).ReturnsAsync(structure);
    }

    private static Dictionary<string, object> BuildSettings(string source, bool overwriteSource)
    {
        return new Dictionary<string, object>
        {
            ["overwrite_source"] = overwriteSource,
            ["selected_tracks_per_file"] = new Dictionary<string, List<int>>
            {
                [source] = new List<int> { 0, 1 }
            }
        };
    }
}
