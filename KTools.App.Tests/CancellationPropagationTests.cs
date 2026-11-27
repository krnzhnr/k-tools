// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using Moq;
using KTools_App.Core;
using KTools_App.Models;
using KTools_App.Scripts;
using KTools_App.Services.Contracts;
using KTools_App.Tests.TestHelpers;
using KTools_App.ViewModels;
using ExecutionContext = KTools_App.Models.ExecutionContext;

namespace KTools_App.Tests;

/// <summary>
/// Проверки сквозного проброса CancellationToken (ишью #122):
/// отмена флагом заменяется токеном батча и per-item токенами,
/// зонды и одиночные выполнения обязаны наблюдать токен.
/// Все комментарии написаны исключительно на русском языке.
/// </summary>
[TestClass]
public class CancellationPropagationTests
{
    private string? _tempFile;

    [TestCleanup]
    public void Cleanup()
    {
        if (_tempFile is not null && File.Exists(_tempFile))
        {
            File.Delete(_tempFile);
        }

        _tempFile = null;
    }

    private static MediaProbeService CreateProbeService(
        Mock<ILogService> log,
        Mock<IMkvmergeRunner> mkvmerge,
        Mock<IFFmpegRunner> ffmpeg,
        Mock<ISettingsManager> settings)
    {
        return new MediaProbeService(log.Object, mkvmerge.Object, ffmpeg.Object, settings.Object);
    }

    /// <summary>
    /// Зонд с отменённым токеном не запускает внешние процессы и возвращает null.
    /// </summary>
    [TestMethod]
    public async Task ProbeAsync_CancelledToken_ReturnsNull()
    {
        // Arrange
        _tempFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(_tempFile, "dummy media content");

        var log = new Mock<ILogService>();
        var mkvmerge = new Mock<IMkvmergeRunner>();
        var ffmpeg = new Mock<IFFmpegRunner>();
        var settings = new Mock<ISettingsManager>();

        string jsonNoDuration = """
        {
            "format": { "bit_rate": "384000" },
            "streams": [
                {
                    "index": 0,
                    "codec_name": "ac3",
                    "codec_type": "audio",
                    "channels": 6
                }
            ]
        }
        """;
        using var doc = System.Text.Json.JsonDocument.Parse(jsonNoDuration);
        ffmpeg.Setup(f => f.GetVideoInfoAsync(_tempFile, null)).ReturnsAsync(doc);

        MediaProbeService service = CreateProbeService(log, mkvmerge, ffmpeg, settings);

        // Act
        MediaStructure? result = await service.ProbeAsync(_tempFile, new CancellationToken(true));

        // Assert
        result.Should().BeNull("отменённый зонд не выполняет анализ");
        ffmpeg.Verify(f => f.GetVideoInfoAsync(It.IsAny<string>(), null), Times.Never);
    }

    /// <summary>
    /// Одиночное выполнение с отменённым токеном возвращает отмену, а не ошибку.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_CancelledToken_ReturnsCancelled()
    {
        // Arrange
        var script = new MediaDownloaderScript(
            new Mock<ILogService>().Object,
            MockBuilders.CreateSettingsManagerMock().Object,
            MockBuilders.CreatePathManagerMock().Object);
        ExecutionContext context = ExecutionContext.CreateBatch("MediaDownloaderScript", 1).ForItem(0);

        // Act
        ExecutionResult result = await script.ExecuteSingleAsync(
            "https://example.com/video",
            new Dictionary<string, object>(),
            null,
            (_, _, _, _, _, _) => { },
            0,
            1,
            context,
            new CancellationToken(true));

        // Assert
        result.Status.Should().Be(ExecutionStatus.Cancelled);
    }

    /// <summary>
    /// Предварительно отменённый скрипт не запускает процесс и возвращает отмену.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_ScriptCancelledBeforeStart_ReturnsCancelled()
    {
        // Arrange
        var script = new MediaDownloaderScript(
            new Mock<ILogService>().Object,
            MockBuilders.CreateSettingsManagerMock().Object,
            MockBuilders.CreatePathManagerMock().Object);
        script.Cancel();
        ExecutionContext context = ExecutionContext.CreateBatch("MediaDownloaderScript", 1).ForItem(0);

        // Act
        ExecutionResult result = await script.ExecuteSingleAsync(
            "https://example.com/video",
            new Dictionary<string, object>(),
            null,
            (_, _, _, _, _, _) => { },
            0,
            1,
            context);

        // Assert
        result.Status.Should().Be(ExecutionStatus.Cancelled);
    }

    /// <summary>
    /// Отмена очереди во время выполнения элемента фиксирует отмену через per-item токен.
    /// </summary>
    [TestMethod]
    public async Task StartExecution_CancelDuringItem_RecordsCancelledResult()
    {
        // Arrange
        var log = MockBuilders.CreateLogServiceMock();
        var settingsManager = MockBuilders.CreateSettingsManagerMock();
        var pathManager = MockBuilders.CreatePathManagerMock();
        var script = new StubScript(log.Object, settingsManager.Object, pathManager.Object);
        var started = new ManualResetEventSlim(false);
        script.ExecuteHandlerWithToken = (file, _, _, token) =>
        {
            started.Set();
            return Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token);
                return ExecutionResult.Succeeded(
                    ExecutionContext.CreateBatch("StubScript", 1),
                    "Готово");
            });
        };

        var viewModel = new WorkPanelViewModel(
            MockBuilders.CreateNavigationMock().Object,
            MockBuilders.CreateDialogMock().Object,
            settingsManager.Object,
            log.Object,
            MockBuilders.CreateDependencyManagerMock().Object,
            MockBuilders.CreateMediaProbeMock().Object);
        var files = new ObservableCollection<FileQueueItem> { new FileQueueItem("item.mkv") };
        viewModel.Initialize(script, files);

        // Act
        Task execution = viewModel.StartExecutionCommand.ExecuteAsync(new Dictionary<string, object>());
        started.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("элемент начал выполнение");
        viewModel.CancelExecutionCommand.Execute(null);
        await execution;

        // Assert
        viewModel.CancelledCount.Should().Be(1);
    }
}
