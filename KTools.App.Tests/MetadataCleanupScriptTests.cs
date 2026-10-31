// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Infrastructure;
using KTools_App.Models;
using KTools_App.Scripts;
using KTools_App.Services.Contracts;
using KTools_App.Tests.TestHelpers;

namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты для скрипта очистки метаданных MetadataCleanupScript.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class MetadataCleanupScriptTests
{
    private Mock<ILogService> _logServiceMock = null!;
    private Mock<ISettingsManager> _settingsManagerMock = null!;
    private Mock<IPathManager> _pathManagerMock = null!;
    private Mock<IFFmpegRunner> _ffmpegRunnerMock = null!;
    private Mock<IMediaProbeService> _mediaProbeServiceMock = null!;
    private MetadataCleanupScript _script = null!;

    [TestInitialize]
    public void Setup()
    {
        _logServiceMock = new Mock<ILogService>();
        _settingsManagerMock = new Mock<ISettingsManager>();
        _pathManagerMock = new Mock<IPathManager>();
        _ffmpegRunnerMock = new Mock<IFFmpegRunner>();
        _mediaProbeServiceMock = new Mock<IMediaProbeService>();

        _script = new MetadataCleanupScript(
            _logServiceMock.Object,
            _settingsManagerMock.Object,
            _pathManagerMock.Object,
            _ffmpegRunnerMock.Object,
            _mediaProbeServiceMock.Object
        );
    }

    /// <summary>
    /// Проверяет базовые свойства скрипта (имя, категорию, иконку, расширения).
    /// </summary>
    [TestMethod]
    public void ScriptProperties_VerifyCorrectValues()
    {
        // Assert
        _script.Category.Should().Be("Видео");
        _script.IconName.Should().Be(AppConstants.ScriptIcons.MetadataCleanup);
        _script.FileExtensions.Should().Contain(new[] { ".mkv", ".mp4", ".avi", ".mp3", ".flac" });
    }

    /// <summary>
    /// Проверяет валидность схемы настроек скрипта.
    /// </summary>
    [TestMethod]
    public void SettingsSchema_ContainsOverwriteAndOptionToDelete()
    {
        // Act
        var schema = _script.SettingsSchema;

        // Assert
        schema.Should().NotBeNull();
        schema.Should().HaveCount(2);

        var overwriteField = schema.Find(f => f.Key == "overwrite_source");
        overwriteField.Should().NotBeNull();
        overwriteField!.Type.Should().Be(SettingType.Checkbox);
        overwriteField.DefaultValue.Should().Be(false);

        var deleteOriginalField = schema.Find(f => f.Key == "delete_source");
        deleteOriginalField.Should().NotBeNull();
        deleteOriginalField!.Type.Should().Be(SettingType.Checkbox);
        deleteOriginalField.DefaultValue.Should().Be(false);
    }

    /// <summary>
    /// Проверяет выполнение успешной очистки метаданных через IFFmpegRunner.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_SuccessfulRun_ReturnsSuccess()
    {
        // Arrange
        string tempSourceFile = Path.GetTempFileName();
        string tempOutputDir = Path.GetDirectoryName(tempSourceFile) ?? AppContext.BaseDirectory;

        _settingsManagerMock.Setup(s => s.GetSetting("General", "OverwriteExisting", false)).Returns(true);
        _ffmpegRunnerMock.Setup(f => f.RunAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<List<string>>(),
            It.IsAny<List<string>>(),
            It.IsAny<bool>(),
            It.IsAny<double>(),
            It.IsAny<Action<ProgressInfo>>(),
            It.IsAny<System.Threading.CancellationToken>()
        )).ReturnsAsync(MockBuilders.ProcessSucceeded());

        var settings = new Dictionary<string, object>
        {
            { "overwrite_source", false },
            { "delete_source", false }
        };

        try
        {
            // Act
            var results = await _script.ExecuteSingleAsync(
                tempSourceFile,
                settings,
                tempOutputDir,
                (idx, total, status, pct, fps, bit) => { },
                0,
                1
            );

            // Assert
            results.Messages.Should().Contain(s => s.Contains("Очищены метаданные"));
        }
        finally
        {
            if (File.Exists(tempSourceFile)) File.Delete(tempSourceFile);
        }
    }

    /// <summary>
    /// Проверяет, что при неудачной подмене оригинала (файл результата не создан)
    /// скрипт НЕ сообщает об успехе: результат остаётся частичным с кодом
    /// source-replacement-failed, а исходный файл сохраняется на диске.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_SourceReplacementFails_ReportsPartialAndKeepsSource()
    {
        // Arrange
        using var scope = new TempDirectoryScope();
        string source = scope.CreateFile("movie.mkv", "оригинал");
        _settingsManagerMock.Setup(s => s.GetSetting("General", "OverwriteExisting", false)).Returns(true);
        _ffmpegRunnerMock.Setup(f => f.RunAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<List<string>>(),
            It.IsAny<List<string>>(),
            It.IsAny<bool>(),
            It.IsAny<double>(),
            It.IsAny<Action<ProgressInfo>>(),
            It.IsAny<System.Threading.CancellationToken>()
        )).ReturnsAsync(MockBuilders.ProcessSucceeded());

        var settings = new Dictionary<string, object>
        {
            { "overwrite_source", true },
            { "delete_source", false }
        };

        // Act — внешний процесс «успешен», но выходной файл не создан
        ExecutionResult result = await _script.ExecuteSingleAsync(
            source,
            settings,
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
        result.OutputFile.Should().NotBeNullOrWhiteSpace("результат обязан указывать ожидаемый выходной файл");
        result.Messages.Should().Contain(
            m => m.Contains(Path.GetFileName(result.OutputFile!), StringComparison.Ordinal),
            "результат должен сообщать, какой именно выходной файл не найден");
        _logServiceMock.Verify(
            l => l.Write(
                "script.source.replaced_failed",
                LogLevel.Error,
                LogStatus.PartiallySucceeded,
                It.IsAny<string>(),
                It.IsAny<Exception>(),
                AppConstants.ScriptMetadata.MetadataCleanName,
                It.IsAny<LogContext?>(),
                It.Is<IReadOnlyDictionary<string, object?>>(p =>
                    Equals(p["ErrorCode"], "SOURCE_REPLACE_FAILED")
                    && Equals(p["CleanupState"], "SourcePreserved"))),
            Times.Once,
            "неудачная подмена оригинала фиксируется структурированной ошибкой с состоянием очистки");
    }

    /// <summary>
    /// Проверяет, что при отсутствии результата подмены не остаётся никаких
    /// временных файлов и артефактов рядом с исходником.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_SourceReplacementFails_LeavesNoArtifacts()
    {
        // Arrange
        using var scope = new TempDirectoryScope();
        string source = scope.CreateFile("clean.mkv", "оригинал");
        _settingsManagerMock.Setup(s => s.GetSetting("General", "OverwriteExisting", false)).Returns(true);
        _ffmpegRunnerMock.Setup(f => f.RunAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<List<string>>(),
            It.IsAny<List<string>>(),
            It.IsAny<bool>(),
            It.IsAny<double>(),
            It.IsAny<Action<ProgressInfo>>(),
            It.IsAny<System.Threading.CancellationToken>()
        )).ReturnsAsync(MockBuilders.ProcessSucceeded());

        // Act
        await _script.ExecuteSingleAsync(
            source,
            new Dictionary<string, object> { { "overwrite_source", true } },
            outputPath: null,
            progressCallback: (idx, total, status, pct, fps, bit) => { },
            fileIndex: 0,
            totalCount: 1);

        // Assert
        Directory.GetFiles(scope.RootPath).Should().ContainSingle(
            "на диске остаётся только исходный файл, без резервных копий и частичных результатов");
    }

    /// <summary>
    /// Проверяет успешную подмену оригинала: результат оказывается по пути исходника,
    /// OutputFile указывает на реально существующий файл, статус — Succeeded.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_SourceReplacementSucceeds_ReturnsSucceeded()
    {
        // Arrange
        using var scope = new TempDirectoryScope();
        string source = scope.CreateFile("replace.mkv", "оригинал");
        _settingsManagerMock.Setup(s => s.GetSetting("General", "OverwriteExisting", false)).Returns(true);
        _ffmpegRunnerMock.Setup(f => f.RunAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<List<string>>(),
            It.IsAny<List<string>>(),
            It.IsAny<bool>(),
            It.IsAny<double>(),
            It.IsAny<Action<ProgressInfo>>(),
            It.IsAny<System.Threading.CancellationToken>()
        )).ReturnsAsync((string input, string output, List<string> extra, List<string> inputArgs, bool overwrite, double duration, Action<ProgressInfo> onProgress, System.Threading.CancellationToken token, ProcessExecutionContext? processContext) =>
        {
            File.WriteAllText(output, "очищено");
            return MockBuilders.ProcessSucceeded();
        });

        // Act
        ExecutionResult result = await _script.ExecuteSingleAsync(
            source,
            new Dictionary<string, object> { { "overwrite_source", true } },
            outputPath: null,
            progressCallback: (idx, total, status, pct, fps, bit) => { },
            fileIndex: 0,
            totalCount: 1);

        // Assert
        result.Status.Should().Be(ExecutionStatus.Succeeded);
        result.ErrorCode.Should().NotBe("source-replacement-failed");
        result.OutputFile.Should().Be(source);
        result.OutputExists.Should().BeTrue();
        File.Exists(source).Should().BeTrue();
        File.ReadAllText(source).Should().Be("очищено");
    }

    /// <summary>
    /// Проверяет типизированный контракт пропуска: файл уже существует — Skipped с кодом output-exists.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_OutputExists_ReturnsTypedSkipped()
    {
        // Arrange
        using var scope = new TempDirectoryScope();
        string source = scope.CreateFile(Path.Combine("input", "exists.mkv"), "оригинал");
        string outputDir = Path.Combine(scope.RootPath, "output");
        Directory.CreateDirectory(outputDir);
        File.WriteAllText(Path.Combine(outputDir, "exists.mkv"), "старый результат");
        _settingsManagerMock.Setup(s => s.GetSetting("General", "OverwriteExisting", false)).Returns(false);

        // Act — файл результата уже существует, перезапись запрещена
        ExecutionResult result = await _script.ExecuteSingleAsync(
            source,
            new Dictionary<string, object>(),
            outputPath: outputDir,
            progressCallback: (idx, total, status, pct, fps, bit) => { },
            fileIndex: 0,
            totalCount: 1);

        // Assert
        result.Status.Should().Be(ExecutionStatus.Skipped);
        result.ErrorCode.Should().Be("output-exists");
        result.OutputExists.Should().BeTrue();
    }
}

