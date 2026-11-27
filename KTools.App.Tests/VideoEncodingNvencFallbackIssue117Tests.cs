// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Encoders;
using KTools_App.Models;
using KTools_App.Scripts;
using KTools_App.Services.Contracts;
using KTools_App.Infrastructure;
using KTools_App.Tests.TestHelpers;

namespace KTools_App.Tests;

/// <summary>
/// Регрессионные тесты фолбэка на CPU при недоступном NVENC (issue #117).
/// Очередь с encoder=nvenc на машине без NVIDIA обязана кодироваться программным CPU
/// с предупреждением в журнале, а не ронять задачу исключением.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class VideoEncodingNvencFallbackIssue117Tests
{
    private RecordingLogService _logService = null!;
    private Mock<ISettingsManager> _settingsManagerMock = null!;
    private Mock<IPathManager> _pathManagerMock = null!;
    private Mock<IFFmpegRunner> _ffmpegRunnerMock = null!;
    private Mock<IMediaProbeService> _mediaProbeServiceMock = null!;
    private Mock<IHardwareCapabilityCache> _hardwareCacheMock = null!;

    [TestInitialize]
    public void Setup()
    {
        _logService = new RecordingLogService();
        _settingsManagerMock = new Mock<ISettingsManager>();
        _pathManagerMock = new Mock<IPathManager>();
        _ffmpegRunnerMock = new Mock<IFFmpegRunner>();
        _mediaProbeServiceMock = new Mock<IMediaProbeService>();
        _hardwareCacheMock = new Mock<IHardwareCapabilityCache>();

        // Машина без NVIDIA: NVENC недоступен ни по кэшу, ни по прямой проверке.
        _hardwareCacheMock.Setup(c => c.IsNvencSupported).Returns(false);
        _ffmpegRunnerMock.Setup(r => r.CheckNvencSupportAsync()).ReturnsAsync(false);
    }

    /// <summary>
    /// Проверяет, что задача с encoder=nvenc (стабильный идентификатор) на машине без NVENC
    /// кодируется программным CPU с предупреждением в журнале вместо исключения.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_NvencStableIdUnsupported_FallsBackToCpuWithWarning()
    {
        await ExecuteSingleAsync_NvencUnsupported_FallsBackToCpuWithWarningCore("nvenc");
    }

    /// <summary>
    /// Проверяет, что задача с encoder=NVENC (отображаемое имя) на машине без NVENC
    /// кодируется программным CPU с предупреждением в журнале вместо исключения.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_NvencDisplayNameUnsupported_FallsBackToCpuWithWarning()
    {
        await ExecuteSingleAsync_NvencUnsupported_FallsBackToCpuWithWarningCore("NVENC");
    }

    private async Task ExecuteSingleAsync_NvencUnsupported_FallsBackToCpuWithWarningCore(string encoderId)
    {
        // Arrange
        var script = CreateScript(new NvencEncoder(), new CpuEncoder());
        string tempSourceFile = Path.GetTempFileName();
        string tempOutputDir = Path.GetDirectoryName(tempSourceFile) ?? AppContext.BaseDirectory;

        var structure = new MediaStructure { FilePath = tempSourceFile, Duration = 60.0 };
        structure.Tracks.Add(new MediaTrack { TrackId = 0, TrackType = "video", Codec = "h264", Name = "Video" });
        _mediaProbeServiceMock.Setup(p => p.ProbeAsync(tempSourceFile)).ReturnsAsync(structure);

        List<string>? capturedExtraArgs = null;
        string? capturedOutputPath = null;
        _ffmpegRunnerMock.Setup(r => r.RunAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<List<string>>(),
            It.IsAny<bool>(), It.IsAny<double>(), It.IsAny<Action<ProgressInfo>>(), It.IsAny<CancellationToken>()
        )).Callback<string, string, List<string>, List<string>, bool, double, Action<ProgressInfo>, CancellationToken, ProcessExecutionContext>(
            (inP, outP, extArgs, inArgs, ovr, dur, prog, ct, processContext) =>
            {
                capturedExtraArgs = extArgs;
                capturedOutputPath = outP;
                // Эмулируем артефакт, созданный реальным FFmpeg при успешном кодировании.
                if (!string.IsNullOrEmpty(outP)) File.WriteAllText(outP, "dummy");
            }
        ).ReturnsAsync(MockBuilders.ProcessSucceeded());

        var settings = new Dictionary<string, object>
        {
            { "encoder", encoderId },
            { "lossless", false },
            { "force_10bit", false }
        };

        try
        {
            // Act
            ExecutionResult result = await script.ExecuteSingleAsync(
                tempSourceFile, settings, tempOutputDir, (idx, total, status, pct, fps, bit) => { }, 0, 1);

            // Assert
            result.Status.Should().Be(ExecutionStatus.Succeeded);
            capturedExtraArgs.Should().NotBeNull();
            capturedExtraArgs.Should().Contain("-c:v");
            capturedExtraArgs.Should().Contain("libx265", "фолбэк обязан кодировать программным CPU (HEVC по умолчанию)");

            IReadOnlyList<RecordedLogEvent> fallbackEvents =
                _logService.EventsById("script.video_encoding.encoder_fallback");
            fallbackEvents.Should().HaveCount(1, "фолбэк обязан фиксироваться предупреждением в журнале");
            fallbackEvents[0].Level.Should().Be(LogLevel.Warning);
            fallbackEvents[0].Status.Should().Be(LogStatus.PartiallySucceeded);
            _logService.Rejections.Should().BeEmpty("событие фолбэка обязано проходить проверку схемы журнала");
        }
        finally
        {
            if (File.Exists(tempSourceFile)) File.Delete(tempSourceFile);
            if (!string.IsNullOrEmpty(capturedOutputPath) && File.Exists(capturedOutputPath)) File.Delete(capturedOutputPath);
        }
    }

    /// <summary>
    /// Проверяет, что при отсутствии даже CPU задача завершается отказом с понятной ошибкой
    /// и без ложного события фолбэка: исключение бросается только если нет и CPU.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_NvencUnsupportedWithoutCpu_ReportsFailureWithoutFallback()
    {
        // Arrange
        var script = CreateScript(new NvencEncoder());
        string tempSourceFile = Path.GetTempFileName();
        string tempOutputDir = Path.GetDirectoryName(tempSourceFile) ?? AppContext.BaseDirectory;

        var structure = new MediaStructure { FilePath = tempSourceFile, Duration = 60.0 };
        structure.Tracks.Add(new MediaTrack { TrackId = 0, TrackType = "video", Codec = "h264", Name = "Video" });
        _mediaProbeServiceMock.Setup(p => p.ProbeAsync(tempSourceFile)).ReturnsAsync(structure);

        var settings = new Dictionary<string, object>
        {
            { "encoder", "nvenc" },
            { "lossless", false },
            { "force_10bit", false }
        };

        try
        {
            // Act
            ExecutionResult result = await script.ExecuteSingleAsync(
                tempSourceFile, settings, tempOutputDir, (idx, total, status, pct, fps, bit) => { }, 0, 1);

            // Assert
            result.Status.Should().Be(ExecutionStatus.Failed, "без доступного CPU задача обязана завершаться отказом");
            result.ErrorCode.Should().Be("video-encoding-exception");
            _logService.EventsById("script.video_encoding.encoder_fallback").Should().BeEmpty(
                "фолбэк без рабочего CPU невозможен и не должен фиксироваться");
        }
        finally
        {
            if (File.Exists(tempSourceFile)) File.Delete(tempSourceFile);
        }
    }

    private VideoEncodingScript CreateScript(params IVideoEncoder[] encoders)
    {
        var registry = new VideoEncoderRegistry(encoders, _hardwareCacheMock.Object);
        return new VideoEncodingScript(
            _logService,
            _settingsManagerMock.Object,
            _pathManagerMock.Object,
            _ffmpegRunnerMock.Object,
            _mediaProbeServiceMock.Object,
            registry);
    }
}
