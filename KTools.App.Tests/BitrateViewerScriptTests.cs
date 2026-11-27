// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Models;
using KTools_App.Scripts;
using KTools_App.Services.Contracts;
using KTools_App.Tests.TestHelpers;

namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты для скрипта анализа битрейта BitrateViewerScript.
/// Проверяют ветви успеха, отсутствия данных, исключений и отмены.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class BitrateViewerScriptTests
{
    private Mock<ILogService> _logServiceMock = null!;
    private Mock<ISettingsManager> _settingsManagerMock = null!;
    private Mock<IPathManager> _pathManagerMock = null!;
    private Mock<IBitrateAnalyzerService> _analyzerMock = null!;
    private Mock<IDiskTypeDetectorService> _diskTypeMock = null!;
    private BitrateViewerScript _script = null!;
    private string _tempDir = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _logServiceMock = new Mock<ILogService>();
        _settingsManagerMock = MockBuilders.CreateSettingsManagerMock();
        _pathManagerMock = MockBuilders.CreatePathManagerMock();
        _analyzerMock = new Mock<IBitrateAnalyzerService>();
        _diskTypeMock = new Mock<IDiskTypeDetectorService>();
        _diskTypeMock
            .Setup(d => d.GetDriveTypeForPath(It.IsAny<string>()))
            .Returns(DriveMediaType.SSD);

        _script = new BitrateViewerScript(
            _logServiceMock.Object,
            _settingsManagerMock.Object,
            _pathManagerMock.Object,
            _analyzerMock.Object,
            _diskTypeMock.Object);
        _tempDir = string.Empty;
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (!string.IsNullOrEmpty(_tempDir) && Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch (Exception)
        {
            // Игнорируем транзитные блокировки файлов при очистке
        }
    }

    /// <summary>
    /// Создаёт уникальную временную папку для изоляции файлового состояния теста.
    /// </summary>
    private string CreateTempRoot()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "KToolsBitrateViewerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        return _tempDir;
    }

    /// <summary>
    /// Создаёт типовой результат анализа битрейта для тестовых проверок.
    /// </summary>
    private static BitrateAnalysisResult CreateSampleResult(string filePath)
    {
        return new BitrateAnalysisResult
        {
            FilePath = filePath,
            CodecName = "h264",
            DurationSeconds = 60.0,
            Fps = 25.0,
            TotalFrames = 1500,
            PerSecondMbps = new[] { 1.0, 2.0 },
            FramePackets = Array.Empty<FrameBitrateInfo>(),
            KeyframeTimes = new[] { 0.0, 10.0 },
            MeanMbps = 1.5,
            MinMbps = 1.0,
            MaxMbps = 2.0,
            StdDevMbps = 0.5
        };
    }

    /// <summary>
    /// Настраивает мок анализатора на возврат заданного результата.
    /// </summary>
    private void SetupAnalyzerResult(string inputPath, BitrateAnalysisResult? result)
    {
        _analyzerMock
            .Setup(a => a.AnalyzeBitrateAsync(
                inputPath,
                It.IsAny<Action<double, string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
    }

    /// <summary>
    /// Проверяет базовые метаданные скрипта анализа битрейта.
    /// </summary>
    [TestMethod]
    public void ScriptProperties_ValidMetadata_ReturnsExpectedValues()
    {
        // Assert
        _script.Name.Should().Be(AppConstants.ScriptMetadata.BitrateViewerName);
        _script.Description.Should().Be(AppConstants.ScriptMetadata.BitrateViewerDesc);
        _script.Category.Should().Be(AppConstants.ScriptCategory.Tools);
        _script.IconName.Should().Be("\uE9D2");
        _script.RequiredDependencies.Should().Contain("ffmpeg");
        _script.FileExtensions.Should().NotBeEmpty();
        _script.FileExtensions.Should().Contain(".mkv");
        _script.SettingsSchema.Should().Contain(f => f.Key == "save_png_export");
        _script.GetOutputExtension("movie.mp4").Should().Be(".mp4");
    }

    /// <summary>
    /// Проверяет, что валидный результат анализа даёт успех, привязывается к очереди и рассылает прогресс.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_ValidResult_ReturnsSucceededAndBindsQueueItem()
    {
        // Arrange
        string tempRoot = CreateTempRoot();
        string inputPath = Path.Combine(tempRoot, "movie.mkv");
        File.WriteAllText(inputPath, "input");
        var expected = CreateSampleResult(inputPath);
        var queueItem = new FileQueueItem(inputPath);
        _script.FilesQueue.Add(queueItem);

        var progressCalls = new List<(string Status, double? Percent)>();
        _analyzerMock
            .Setup(a => a.AnalyzeBitrateAsync(
                inputPath,
                It.IsAny<Action<double, string>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, Action<double, string>?, CancellationToken>(
                (path, progress, ct) => progress?.Invoke(50.0, "Половина"))
            .ReturnsAsync(expected);

        // Act
        ExecutionResult result = await _script.ExecuteSingleAsync(
            inputPath,
            new Dictionary<string, object>(),
            null,
            (cur, total, msg, pct, fps, br) => progressCalls.Add((msg, pct)),
            1,
            1);

        // Assert
        result.Status.Should().Be(ExecutionStatus.Succeeded);
        result.Should().Contain(r => r.Contains("Готово"));
        queueItem.BitrateData.Should().BeSameAs(expected);
        queueItem.HasBitrateData.Should().BeTrue();
        progressCalls.Should().Contain(c => c.Status == "Половина" && c.Percent == 50.0);
        progressCalls.Should().Contain(c => c.Status == "Завершено!" && c.Percent == 100.0);
    }

    /// <summary>
    /// Проверяет, что включённый PNG-экспорт фиксируется событием chart_saved без смены статуса.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_SavePngEnabled_WritesChartSavedEvent()
    {
        // Arrange
        string tempRoot = CreateTempRoot();
        string inputPath = Path.Combine(tempRoot, "movie.mkv");
        File.WriteAllText(inputPath, "input");
        _settingsManagerMock
            .Setup(m => m.GetSetting("Script_Анализ_битрейта_видео_и_аудио", "save_png_export", false))
            .Returns(true);
        SetupAnalyzerResult(inputPath, CreateSampleResult(inputPath));

        // Act
        ExecutionResult result = await _script.ExecuteSingleAsync(
            inputPath,
            new Dictionary<string, object>(),
            null,
            (cur, total, msg, pct, fps, br) => { },
            1,
            1);

        // Assert
        result.Status.Should().Be(ExecutionStatus.Succeeded);
        _logServiceMock.VerifyEventId("script.bitrate_viewer.chart_saved", LogLevel.Debug, Times.Once());
    }

    /// <summary>
    /// Проверяет, что пустой результат анализа даёт сбой с возможностью повтора.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_NullResult_ReturnsFailedRetryable()
    {
        // Arrange
        string tempRoot = CreateTempRoot();
        string inputPath = Path.Combine(tempRoot, "movie.mkv");
        File.WriteAllText(inputPath, "input");
        SetupAnalyzerResult(inputPath, null);

        // Act
        ExecutionResult result = await _script.ExecuteSingleAsync(
            inputPath,
            new Dictionary<string, object>(),
            null,
            (cur, total, msg, pct, fps, br) => { },
            1,
            1);

        // Assert
        result.Status.Should().Be(ExecutionStatus.Failed);
        result.ErrorCode.Should().Be("analysis-failed");
        result.Retryable.Should().BeTrue();
        result.Should().Contain(r => r.Contains("Ошибка анализа битрейта"));
    }

    /// <summary>
    /// Проверяет, что исключение анализатора превращается в сбой с именем файла в сообщении.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_AnalyzerThrows_ReturnsFailedWithFileName()
    {
        // Arrange
        string tempRoot = CreateTempRoot();
        string inputPath = Path.Combine(tempRoot, "movie.mkv");
        File.WriteAllText(inputPath, "input");
        _analyzerMock
            .Setup(a => a.AnalyzeBitrateAsync(
                inputPath,
                It.IsAny<Action<double, string>?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("ffprobe недоступен"));

        // Act
        ExecutionResult result = await _script.ExecuteSingleAsync(
            inputPath,
            new Dictionary<string, object>(),
            null,
            (cur, total, msg, pct, fps, br) => { },
            1,
            1);

        // Assert
        result.Status.Should().Be(ExecutionStatus.Failed);
        result.ErrorCode.Should().Be("analysis-failed");
        result.Retryable.Should().BeTrue();
        result.Should().Contain(r => r.Contains("movie.mkv"));
    }

    /// <summary>
    /// Проверяет, что отмена во время анализа возвращает статус Cancelled.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_CancelledDuringAnalysis_ReturnsCancelled()
    {
        // Arrange
        string tempRoot = CreateTempRoot();
        string inputPath = Path.Combine(tempRoot, "movie.mkv");
        File.WriteAllText(inputPath, "input");
        _analyzerMock
            .Setup(a => a.AnalyzeBitrateAsync(
                inputPath,
                It.IsAny<Action<double, string>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, Action<double, string>?, CancellationToken>(
                (path, progress, ct) => _script.Cancel())
            .ReturnsAsync(CreateSampleResult(inputPath));

        // Act
        ExecutionResult result = await _script.ExecuteSingleAsync(
            inputPath,
            new Dictionary<string, object>(),
            null,
            (cur, total, msg, pct, fps, br) => { },
            1,
            1);

        // Assert
        result.Status.Should().Be(ExecutionStatus.Cancelled);
        result.ErrorCode.Should().Be("cancelled");
        result.Should().Contain(r => r.Contains("Прервано пользователем"));
    }
}
