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
/// Юнит-тесты для скрипта замены потоков StreamReplacementScript.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class StreamReplacementScriptTests
{
    private Mock<ILogService> _logServiceMock = null!;
    private Mock<ISettingsManager> _settingsManagerMock = null!;
    private Mock<IPathManager> _pathManagerMock = null!;
    private Mock<IMediaProbeService> _mediaProbeServiceMock = null!;
    private Mock<IFFmpegRunner> _ffmpegRunnerMock = null!;
    private Mock<IMkvmergeRunner> _mkvmergeRunnerMock = null!;
    private StreamReplacementScript _script = null!;

    [TestInitialize]
    public void Setup()
    {
        _logServiceMock = new Mock<ILogService>();
        _settingsManagerMock = new Mock<ISettingsManager>();
        _pathManagerMock = new Mock<IPathManager>();
        _mediaProbeServiceMock = new Mock<IMediaProbeService>();
        _ffmpegRunnerMock = new Mock<IFFmpegRunner>();
        _mkvmergeRunnerMock = new Mock<IMkvmergeRunner>();

        _script = new StreamReplacementScript(
            _logServiceMock.Object,
            _settingsManagerMock.Object,
            _pathManagerMock.Object,
            _mediaProbeServiceMock.Object,
            _ffmpegRunnerMock.Object,
            _mkvmergeRunnerMock.Object
        );
    }

    /// <summary>
    /// Проверяет базовые свойства скрипта замены потоков.
    /// </summary>
    [TestMethod]
    public void ScriptProperties_VerifyCorrectValues()
    {
        _script.Category.Should().Be("Контейнеры");
        _script.IconName.Should().Be(AppConstants.ScriptIcons.StreamReplacement);
    }

    /// <summary>
    /// Проверяет, что если для файла не назначено ни одной замены, скрипт завершается с ошибкой.
    /// </summary>
    [TestMethod]
    public async System.Threading.Tasks.Task ExecuteSingleAsync_NoReplacements_FailsWithErrorMessage()
    {
        // Arrange
        string testFile = "C:\\test\\video.mp4";
        var settings = new Dictionary<string, object>();

        // Act
        var results = await _script.ExecuteSingleAsync(testFile, settings, null, (f, t, m, p, fps, b) => { }, 0, 1);

        // Assert
        results.Status.Should().Be(ExecutionStatus.Failed);
        results.ErrorCode.Should().Be("no-replacements");
        results.OutputExists.Should().BeFalse();
        results.Messages.Should().ContainSingle();
        results[0].Should().Contain("❌ Ошибка: не назначено ни одной замены");
    }

    /// <summary>
    /// Проверяет, что неудачная подмена оригинала не сообщается как успех:
    /// результат частичный с кодом source-replacement-failed, исходник сохраняется.
    /// </summary>
    [TestMethod]
    public async System.Threading.Tasks.Task ExecuteSingleAsync_SourceReplacementFails_ReportsPartialAndKeepsSource()
    {
        // Arrange
        using var scope = new TempDirectoryScope();
        string testFile = scope.CreateFile("replaced.mkv", "оригинал");
        SetupStructure(testFile);

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
            testFile,
            BuildSettings(testFile, true),
            outputPath: null,
            progressCallback: (f, t, m, p, fps, b) => { },
            fileIndex: 0,
            totalCount: 1);

        // Assert
        result.Status.Should().Be(ExecutionStatus.PartiallySucceeded,
            "неудачная подмена оригинала не является полным успехом");
        result.ErrorCode.Should().Be("source-replacement-failed");
        result.OutputExists.Should().BeFalse("выходной файл не создан");
        result.CleanupState.Should().Be(CleanupState.Failed);
        File.Exists(testFile).Should().BeTrue("исходный файл обязан сохраниться");
        File.ReadAllText(testFile).Should().Be("оригинал");
    }

    /// <summary>
    /// Проверяет, что неудачная подмена не оставляет временных артефактов на диске.
    /// </summary>
    [TestMethod]
    public async System.Threading.Tasks.Task ExecuteSingleAsync_SourceReplacementFails_LeavesNoArtifacts()
    {
        // Arrange
        using var scope = new TempDirectoryScope();
        string testFile = scope.CreateFile("artifacts.mkv", "оригинал");
        SetupStructure(testFile);

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
            testFile,
            BuildSettings(testFile, true),
            outputPath: null,
            progressCallback: (f, t, m, p, fps, b) => { },
            fileIndex: 0,
            totalCount: 1);

        // Assert
        Directory.GetFiles(scope.RootPath).Should().ContainSingle("на диске остаётся только исходный файл");
    }

    /// <summary>
    /// Проверяет успешную подмену оригинала: результат оказывается по пути исходника.
    /// </summary>
    [TestMethod]
    public async System.Threading.Tasks.Task ExecuteSingleAsync_SourceReplacementSucceeds_ReturnsSucceeded()
    {
        // Arrange
        using var scope = new TempDirectoryScope();
        string testFile = scope.CreateFile("good.mkv", "оригинал");
        SetupStructure(testFile);

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
            testFile,
            BuildSettings(testFile, true),
            outputPath: null,
            progressCallback: (f, t, m, p, fps, b) => { },
            fileIndex: 0,
            totalCount: 1);

        // Assert
        result.Status.Should().Be(ExecutionStatus.Succeeded);
        result.OutputFile.Should().Be(testFile);
        result.OutputExists.Should().BeTrue();
        File.ReadAllText(testFile).Should().Be("собрано");
    }

    private void SetupStructure(string path)
    {
        MediaStructure structure = new() { FilePath = path, Duration = 100 };
        structure.Tracks.Add(new MediaTrack { TrackId = 0, TrackType = "video" });
        structure.Tracks.Add(new MediaTrack { TrackId = 1, TrackType = "audio" });
        _mediaProbeServiceMock.Setup(p => p.ProbeAsync(path)).ReturnsAsync(structure);
    }

    private static Dictionary<string, object> BuildSettings(string source, bool overwriteSource)
    {
        Dictionary<string, object> fileReplacements = new()
        {
            ["1"] = new Dictionary<string, object> { ["path"] = "C:\\test\\replacement.aac", ["src_id"] = 0 }
        };

        return new Dictionary<string, object>
        {
            ["overwrite_source"] = overwriteSource,
            ["replacements"] = new Dictionary<string, object> { [source] = fileReplacements }
        };
    }

    /// <summary>
    /// Проверяет, что если ни одна замена не совпала с существующими треками файла, скрипт падает с ошибкой.
    /// </summary>
    [TestMethod]
    public async System.Threading.Tasks.Task ExecuteSingleAsync_TrackIdMismatch_FailsWithErrorMessage()
    {
        // Arrange
        string testFile = "C:\\test\\video.mp4";

        var structure = new MediaStructure { FilePath = testFile, Duration = 100 };
        structure.Tracks.Add(new MediaTrack { TrackId = 0, TrackType = "video" });
        _mediaProbeServiceMock.Setup(p => p.ProbeAsync(testFile)).ReturnsAsync(structure);

        // Назначаем замену для недействительного track_id = 99 (в файле только 0)
        var fileReplacements = new Dictionary<string, object>
        {
            {
                "99", new Dictionary<string, object> { { "path", "C:\\test\\audio.m4a" }, { "src_id", 0 } }
            }
        };
        var settings = new Dictionary<string, object>
        {
            { "replacements", new Dictionary<string, object> { { testFile, fileReplacements } } }
        };

        // Act
        var results = await _script.ExecuteSingleAsync(testFile, settings, null, (f, t, m, p, fps, b) => { }, 0, 1);

        // Assert
        results.Status.Should().Be(ExecutionStatus.Failed);
        results.ErrorCode.Should().Be("no-effective-replacements");
        results.OutputExists.Should().BeFalse();
        results.Messages.Should().ContainSingle();
        results[0].Should().Contain("❌ Ошибка: ни одна из назначенных замен не была передана в финальную команду");
        _ffmpegRunnerMock.Verify(r => r.RunAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<List<string>>(),
            It.IsAny<List<string>>(),
            It.IsAny<bool>(),
            It.IsAny<double>(),
            It.IsAny<Action<ProgressInfo>>(),
            It.IsAny<System.Threading.CancellationToken>()
        ), Times.Never);
    }
}
