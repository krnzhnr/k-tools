// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using Moq;
using KTools_App.Core;
using KTools_App.Infrastructure;
using KTools_App.Models;
using KTools_App.Scripts;
using KTools_App.Services.Contracts;
using KTools_App.Tests.TestHelpers;

namespace KTools_App.Tests;

/// <summary>
/// Регрессионные тесты инъекции опций через названия дорожек (issue #85).
/// Злоумышленник контролирует метаданные чужого медиафайла (название дорожки),
/// кавычка в названии не должна разрывать аргумент и внедрять чужие флаги.
/// Все комментарии выполнены исключительно на русском языке.
/// </summary>
[TestClass]
public class TrackTitleInjectionTests
{
    private const string EvilTitle = "Комментарий \"режиссёра\"";

    private Mock<ILogService> _logServiceMock = null!;
    private Mock<ISettingsManager> _settingsManagerMock = null!;
    private Mock<IPathManager> _pathManagerMock = null!;
    private Mock<IMediaProbeService> _mediaProbeServiceMock = null!;
    private Mock<IFFmpegRunner> _ffmpegRunnerMock = null!;
    private Mock<IMkvmergeRunner> _mkvmergeRunnerMock = null!;

    [TestInitialize]
    public void Setup()
    {
        _logServiceMock = new Mock<ILogService>();
        _settingsManagerMock = new Mock<ISettingsManager>();
        _pathManagerMock = new Mock<IPathManager>();
        _mediaProbeServiceMock = new Mock<IMediaProbeService>();
        _ffmpegRunnerMock = new Mock<IFFmpegRunner>();
        _mkvmergeRunnerMock = new Mock<IMkvmergeRunner>();
        _settingsManagerMock.Setup(s => s.GetSetting("General", "OverwriteExisting", false)).Returns(true);
    }

    /// <summary>
    /// Проверяет, что квотирование экранирует внутреннюю кавычку названием вида 'Комментарий "режиссёра"'.
    /// </summary>
    [TestMethod]
    public void Quote_TitleWithDirectorComment_EscapesInnerQuotes()
    {
        // Act
        string quoted = CliArgument.Quote(EvilTitle);

        // Assert
        quoted.Should().Be("\"Комментарий \\\"режиссёра\\\"\"");
    }

    /// <summary>
    /// Проверяет, что сборка командной строки держит название с кавычкой одним токеном.
    /// </summary>
    [TestMethod]
    public void Join_ArgsWithDirectorCommentTitle_KeepsSingleToken()
    {
        // Act
        string commandLine = CliArgument.Join(new[] { "--track-name", $"0:{EvilTitle}" });

        // Assert
        commandLine.Should().Be("--track-name \"0:Комментарий \\\"режиссёра\\\"\"");
    }

    /// <summary>
    /// Проверяет, что устаревшее наивное квотирование нормализуется, а не дублируется.
    /// </summary>
    [TestMethod]
    public void Join_LegacyNaivelyQuotedPath_NormalizesToSingleToken()
    {
        // Act
        string commandLine = CliArgument.Join(new[] { "-i", "\"C:\\Видео\\файл с пробелами.mkv\"" });

        // Assert
        commandLine.Should().Be("-i \"C:\\Видео\\файл с пробелами.mkv\"");
    }

    /// <summary>
    /// Проверяет, что подмена MKV передаёт название с кавычкой сырым значением,
    /// а централизованное квотирование держит его одним аргументом.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_TrackTitleWithDirectorComment_PassesRawValueAsSingleToken()
    {
        // Arrange
        using var scope = new TempDirectoryScope();
        string sourceFile = scope.CreateFile("чужой.mkv", "оригинал");
        string replacementFile = scope.CreateFile("замена.mka", "замена");
        var structure = new MediaStructure { FilePath = sourceFile, Duration = 100 };
        structure.Tracks.Add(new MediaTrack { TrackId = 0, TrackType = "video" });
        structure.Tracks.Add(new MediaTrack { TrackId = 1, TrackType = "audio", Language = "rus", Name = EvilTitle });
        _mediaProbeServiceMock.Setup(p => p.ProbeAsync(sourceFile)).ReturnsAsync(structure);

        var script = new StreamReplacementScript(
            _logServiceMock.Object,
            _settingsManagerMock.Object,
            _pathManagerMock.Object,
            _mediaProbeServiceMock.Object,
            _ffmpegRunnerMock.Object,
            _mkvmergeRunnerMock.Object);

        List<MkvInputSource>? capturedInputs = null;
        _mkvmergeRunnerMock.Setup(m => m.RunAsync(
                It.IsAny<string>(),
                It.IsAny<List<MkvInputSource>>(),
                It.IsAny<string?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<Action<double>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<ProcessExecutionContext?>()))
            .Callback<string, List<MkvInputSource>, string?, List<string>?, Action<double>?, CancellationToken, ProcessExecutionContext?>(
                (_, inputs, _, _, _, _, _) => capturedInputs = inputs)
            .ReturnsAsync(MockBuilders.ProcessSucceeded());

        var settings = new Dictionary<string, object>
        {
            ["replacements"] = new Dictionary<string, object>
            {
                [sourceFile] = new Dictionary<string, object>
                {
                    ["1"] = new Dictionary<string, object> { ["path"] = replacementFile, ["src_id"] = 0 }
                }
            }
        };

        // Act
        await script.ExecuteSingleAsync(sourceFile, settings, null, (_, _, _, _, _, _) => { }, 0, 1);

        // Assert
        capturedInputs.Should().NotBeNull();
        MkvInputSource replacement = capturedInputs!.First(i => i.Path == replacementFile);
        replacement.Args.Should().Contain($"0:{EvilTitle}", "название передаётся сырым значением без ручных кавычек");
        string commandLine = CliArgument.Join(replacement.Args!);
        commandLine.Should().Contain("\"0:Комментарий \\\"режиссёра\\\"\"");
        commandLine.Should().NotContain("--track-name \"\"");
    }

    /// <summary>
    /// Проверяет, что подмена MP4 передаёт название с кавычкой сырым значением метаданных ffmpeg.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_Mp4TitleWithDirectorComment_PassesRawMetadataValue()
    {
        // Arrange
        using var scope = new TempDirectoryScope();
        string sourceFile = scope.CreateFile("чужой.mp4", "оригинал");
        string replacementFile = scope.CreateFile("замена.m4a", "замена");
        var structure = new MediaStructure { FilePath = sourceFile, Duration = 100 };
        structure.Tracks.Add(new MediaTrack { TrackId = 0, TrackType = "video" });
        structure.Tracks.Add(new MediaTrack { TrackId = 1, TrackType = "audio", Language = "rus", Name = EvilTitle });
        _mediaProbeServiceMock.Setup(p => p.ProbeAsync(sourceFile)).ReturnsAsync(structure);

        var script = new StreamReplacementScript(
            _logServiceMock.Object,
            _settingsManagerMock.Object,
            _pathManagerMock.Object,
            _mediaProbeServiceMock.Object,
            _ffmpegRunnerMock.Object,
            _mkvmergeRunnerMock.Object);

        List<string>? capturedExtraArgs = null;
        _ffmpegRunnerMock.Setup(f => f.RunAsync(
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
                (_, _, extraArgs, _, _, _, _, _, _) => { capturedExtraArgs = extraArgs; })
            .ReturnsAsync(MockBuilders.ProcessSucceeded());

        var settings = new Dictionary<string, object>
        {
            ["replacements"] = new Dictionary<string, object>
            {
                [sourceFile] = new Dictionary<string, object>
                {
                    ["1"] = new Dictionary<string, object> { ["path"] = replacementFile, ["src_id"] = 0 }
                }
            }
        };

        // Act
        await script.ExecuteSingleAsync(sourceFile, settings, null, (_, _, _, _, _, _) => { }, 0, 1);

        // Assert
        capturedExtraArgs.Should().NotBeNull();
        capturedExtraArgs.Should().Contain($"title={EvilTitle}", "метаданные передаются сырым значением без ручных кавычек");
        int inputFlagIndex = capturedExtraArgs!.IndexOf("-i");
        inputFlagIndex.Should().BeGreaterThanOrEqualTo(0, "входной файл передаётся флагом -i");
        capturedExtraArgs[inputFlagIndex + 1].Should().Be(replacementFile, "входной файл передаётся сырым путём без ручных кавычек");
        string commandLine = CliArgument.Join(capturedExtraArgs!);
        commandLine.Should().Contain($"\"title={EvilTitle.Replace("\"", "\\\"")}\"");
    }

    /// <summary>
    /// Проверяет, что заголовок субтитров сборки MKV с кавычкой передаётся сырым значением.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_SubsTitleWithDirectorComment_PassesRawValueAsSingleToken()
    {
        // Arrange
        using var scope = new TempDirectoryScope();
        string videoPath = scope.CreateFile("сериал.mkv", "видео");
        string subsPath = scope.CreateFile("сериал.srt", "субтитры");

        var script = new MkvAssemblyScript(
            _logServiceMock.Object,
            _settingsManagerMock.Object,
            _pathManagerMock.Object,
            _mkvmergeRunnerMock.Object,
            _mediaProbeServiceMock.Object,
            _ffmpegRunnerMock.Object);
        script.FilesQueue.Add(new FileQueueItem(videoPath));
        script.FilesQueue.Add(new FileQueueItem(subsPath));

        List<MkvInputSource>? capturedInputs = null;
        _mkvmergeRunnerMock.Setup(m => m.RunAsync(
                It.IsAny<string>(),
                It.IsAny<List<MkvInputSource>>(),
                It.IsAny<string?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<Action<double>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<ProcessExecutionContext?>()))
            .Callback<string, List<MkvInputSource>, string?, List<string>?, Action<double>?, CancellationToken, ProcessExecutionContext?>(
                (_, inputs, _, _, _, _, _) => capturedInputs = inputs)
            .ReturnsAsync(MockBuilders.ProcessSucceeded());

        var settings = new Dictionary<string, object>
        {
            ["clean_tracks"] = true,
            ["subs_full_title"] = EvilTitle,
            ["subs_signs_title"] = "Надписи"
        };

        // Act
        await script.ExecuteSingleAsync(videoPath, settings, null, (_, _, _, _, _, _) => { }, 0, 1);

        // Assert
        capturedInputs.Should().NotBeNull();
        MkvInputSource subs = capturedInputs!.First(i => i.Path == subsPath);
        subs.Args.Should().Contain($"0:{EvilTitle}", "заголовок субтитров передаётся сырым значением без ручных кавычек");
        string commandLine = CliArgument.Join(subs.Args!);
        commandLine.Should().Contain("\"0:Комментарий \\\"режиссёра\\\"\"");
    }
}
