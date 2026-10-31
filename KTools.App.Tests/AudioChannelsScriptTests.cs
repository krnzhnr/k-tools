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
/// Юнит-тесты для скрипта разделения аудиоканалов AudioChannelsScript.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class AudioChannelsScriptTests
{
    private Mock<ILogService> _logServiceMock = null!;
    private Mock<ISettingsManager> _settingsManagerMock = null!;
    private Mock<IPathManager> _pathManagerMock = null!;
    private Mock<IDependencyManager> _dependencyManagerMock = null!;
    private Mock<IFFmpegRunner> _ffmpegRunnerMock = null!;
    private Mock<IEac3toRunner> _eac3toRunnerMock = null!;
    private AudioChannelsScript _script = null!;

    [TestInitialize]
    public void Setup()
    {
        _logServiceMock = new Mock<ILogService>();
        _settingsManagerMock = new Mock<ISettingsManager>();
        _pathManagerMock = new Mock<IPathManager>();
        _dependencyManagerMock = new Mock<IDependencyManager>();
        _dependencyManagerMock.Setup(d => d.IsInstalled(It.IsAny<string>())).Returns(true);
        _ffmpegRunnerMock = new Mock<IFFmpegRunner>();
        _eac3toRunnerMock = new Mock<IEac3toRunner>();

        _script = new AudioChannelsScript(
            _logServiceMock.Object,
            _settingsManagerMock.Object,
            _pathManagerMock.Object,
            _dependencyManagerMock.Object,
            _ffmpegRunnerMock.Object,
            _eac3toRunnerMock.Object
        );
    }

    /// <summary>
    /// Проверяет свойства скрипта разделения аудиоканалов.
    /// </summary>
    [TestMethod]
    public void ScriptProperties_VerifyCorrectValues()
    {
        _script.Category.Should().Be("Аудио");
        _script.IconName.Should().Be(AppConstants.ScriptIcons.AudioChannels);
    }

    /// <summary>
    /// Проверяет типизированный контракт отсутствующей зависимости.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_MissingDependency_ReturnsTypedFailure()
    {
        using var scope = new TempDirectoryScope();
        string source = scope.CreateFile("movie.wav", "данные");
        _dependencyManagerMock.Setup(d => d.IsInstalled("eac3to")).Returns(false);
        _dependencyManagerMock.Setup(d => d.IsInstalled("ffmpeg")).Returns(true);

        // Act
        ExecutionResult result = await _script.ExecuteSingleAsync(
            source,
            new Dictionary<string, object>(),
            outputPath: null,
            progressCallback: (idx, total, status, pct, fps, bit) => { },
            fileIndex: 0,
            totalCount: 1);

        // Assert
        result.Status.Should().Be(ExecutionStatus.Failed);
        result.ErrorCode.Should().Be("missing-dependency");
        result.OutputExists.Should().BeFalse();
        result.OutputFile.Should().BeNull();
        result.CleanupState.Should().Be(CleanupState.NotRequired);
        result.Messages.Should().Contain(m => m.Contains("eac3to"));
    }

    /// <summary>
    /// Проверяет, что успешный результат указывает на реально найденный файл канала,
    /// а не на номинальный путь movie.wavs, которого на диске не существует.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_ChannelsCreated_ReportsRealOutputFile()
    {
        using var scope = new TempDirectoryScope();
        string source = scope.CreateFile("movie.wav", "данные");
        SetupEac3toSuccess();
        SetupFfmpegMergeSuccess();

        // Act
        ExecutionResult result = await _script.ExecuteSingleAsync(
            source,
            new Dictionary<string, object> { ["MergeStereo"] = true },
            outputPath: null,
            progressCallback: (idx, total, status, pct, fps, bit) => { },
            fileIndex: 0,
            totalCount: 1);

        // Assert
        result.Status.Should().Be(ExecutionStatus.Succeeded);
        result.OutputFile.Should().Be(Path.Combine(scope.RootPath, "movie.LR.wav"),
            "OutputFile обязан указывать на реально созданный файл канала");
        result.OutputExists.Should().BeTrue();
        File.Exists(result.OutputFile!).Should().BeTrue();
        result.OutputFile.Should().NotContain("wavs",
            "номинальный путь eac3to не может быть выходным файлом результата");
    }

    /// <summary>
    /// Проверяет, что частичный набор каналов (неудачная склейка стереопары)
    /// не сообщается как полный успех.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_MergeFailure_IsNotReportedAsSuccess()
    {
        using var scope = new TempDirectoryScope();
        string source = scope.CreateFile("movie.wav", "данные");
        SetupEac3toSuccess();
        _ffmpegRunnerMock.Setup(f => f.RunAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<List<string>>(),
            It.IsAny<List<string>>(),
            It.IsAny<bool>(),
            It.IsAny<double>(),
            It.IsAny<Action<ProgressInfo>>(),
            It.IsAny<System.Threading.CancellationToken>())).ReturnsAsync(MockBuilders.ProcessFailed());

        // Act
        ExecutionResult result = await _script.ExecuteSingleAsync(
            source,
            new Dictionary<string, object> { ["MergeStereo"] = true },
            outputPath: null,
            progressCallback: (idx, total, status, pct, fps, bit) => { },
            fileIndex: 0,
            totalCount: 1);

        // Assert
        result.Status.Should().Be(ExecutionStatus.PartiallySucceeded,
            "частичный набор каналов не является полным успехом");
        result.ErrorCode.Should().Be("channel-merge-failed");
        result.OutputExists.Should().BeTrue("созданные моно-файлы остаются на диске");
        result.CleanupState.Should().Be(CleanupState.Partial);
        result.Messages.Should().Contain(m => m.Contains("Не удалось склеить стереопару"));
    }

    /// <summary>
    /// Проверяет отсутствие ложного успеха: если выходные файлы не созданы,
    /// результат обязан быть Failed с output-missing, а не Succeeded.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_NoChannelArtifacts_ReturnsFailedWithoutOutput()
    {
        using var scope = new TempDirectoryScope();
        string source = scope.CreateFile("movie.wav", "данные");
        _eac3toRunnerMock.Setup(e => e.RunAsync(
            It.IsAny<List<string>>(),
            It.IsAny<string>(),
            It.IsAny<Action<double>>(),
            It.IsAny<System.Threading.CancellationToken>())).ReturnsAsync(MockBuilders.ProcessSucceeded());

        // Act — eac3to «успешен», но каналы не созданы
        ExecutionResult result = await _script.ExecuteSingleAsync(
            source,
            new Dictionary<string, object> { ["MergeStereo"] = false },
            outputPath: null,
            progressCallback: (idx, total, status, pct, fps, bit) => { },
            fileIndex: 0,
            totalCount: 1);

        // Assert
        result.Status.Should().Be(ExecutionStatus.Failed);
        result.ErrorCode.Should().Be("output-missing");
        result.OutputExists.Should().BeFalse();
        result.OutputFile.Should().BeNull();
        result.Messages.Should().NotContain(m => m.StartsWith("✅"),
            "сообщение об успешном разделении не должно появляться без выходных файлов");
    }

    private void SetupEac3toSuccess()
    {
        _eac3toRunnerMock.Setup(e => e.RunAsync(
                It.IsAny<List<string>>(),
                It.IsAny<string>(),
                It.IsAny<Action<double>>(),
                It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync((List<string> args, string workingDir, Action<double> onProgress, System.Threading.CancellationToken token, ProcessExecutionContext? processContext, string expectedArtifact) =>
            {
                string outputPath = args[1].Trim('"');
                string stem = Path.Combine(
                    Path.GetDirectoryName(outputPath) ?? workingDir,
                    Path.GetFileNameWithoutExtension(outputPath));
                File.WriteAllText(stem + ".L.wav", "левый");
                File.WriteAllText(stem + ".R.wav", "правый");
                return MockBuilders.ProcessSucceeded();
            });
    }

    private void SetupFfmpegMergeSuccess()
    {
        _ffmpegRunnerMock.Setup(f => f.RunAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<List<string>>(),
                It.IsAny<List<string>>(),
                It.IsAny<bool>(),
                It.IsAny<double>(),
                It.IsAny<Action<ProgressInfo>>(),
                It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync((string input, string output, List<string> extra, List<string> inputArgs, bool overwrite, double duration, Action<ProgressInfo> onProgress, System.Threading.CancellationToken token, ProcessExecutionContext? processContext) =>
            {
                File.WriteAllText(output, "стерео");
                return MockBuilders.ProcessSucceeded();
            });
    }
}
