// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Encoders;
using KTools_App.Infrastructure;
using KTools_App.Models;
using KTools_App.Scripts;
using KTools_App.Services.Contracts;
using KTools_App.Tests.TestHelpers;

namespace KTools_App.Tests;

/// <summary>
/// Проверки запрещённой связки Lossless + AV1 (NVENC):
/// интерфейс обязан гасить чекбокс, а очередь — падать как неповторяемая ошибка конфигурации.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class VideoEncodingLosslessAv1Tests
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
        _hardwareCacheMock.Setup(c => c.IsNvencSupported).Returns(true);
        _ffmpegRunnerMock.Setup(r => r.CheckNvencSupportAsync()).ReturnsAsync(true);
    }

    private VideoEncodingScript CreateScript()
    {
        var encoders = new List<IVideoEncoder> { new NvencEncoder(), new CpuEncoder() };
        var registry = new VideoEncoderRegistry(encoders, _hardwareCacheMock.Object);
        return new VideoEncodingScript(
            _logService,
            _settingsManagerMock.Object,
            _pathManagerMock.Object,
            _ffmpegRunnerMock.Object,
            _mediaProbeServiceMock.Object,
            registry);
    }

    [TestMethod]
    public void SettingsSchema_LosslessWithAv1_HasDisableCondition()
    {
        // Arrange.
        VideoEncodingScript script = CreateScript();

        // Act.
        List<SettingField> schema = script.SettingsSchema;

        // Assert.
        SettingField? lossless = schema.FirstOrDefault(f => f.Key == "lossless");
        lossless.Should().NotBeNull("чекбокс Lossless обязан существовать в схеме");
        lossless!.DisableConditions.Should().NotBeNull("запрет AV1 обязан быть декларативным");
        lossless.DisableConditions!.Any(c => c.Key == "nvenc_codec" && c.Values.Contains("AV1"))
            .Should().BeTrue("Lossless обязан гаснуть при nvenc_codec=AV1");
    }

    [TestMethod]
    public void SettingConditionEvaluator_LosslessDisabled_WhenAv1Visible()
    {
        // Arrange.
        VideoEncodingScript script = CreateScript();
        SettingField lossless = script.SettingsSchema.First(f => f.Key == "lossless");

        // Act: энкодер NVENC, кодек AV1, контроллер кодека видим.
        bool disabled = SettingConditionEvaluator.IsDisabled(
            lossless,
            key => key == "nvenc_codec" ? "AV1" : null,
            key => true);

        // Assert.
        disabled.Should().BeTrue("видимый AV1 обязан гасить Lossless");
    }

    [TestMethod]
    public async Task ExecuteSingleAsync_LosslessAv1_ReportsNonRetryable()
    {
        // Arrange: очередь с запрещённой связкой lossless + AV1.
        VideoEncodingScript script = CreateScript();
        string tempSourceFile = Path.GetTempFileName();
        string tempOutputDir = Path.GetDirectoryName(tempSourceFile) ?? AppContext.BaseDirectory;
        var structure = new MediaStructure { FilePath = tempSourceFile, Duration = 60.0 };
        structure.Tracks.Add(new MediaTrack { TrackId = 0, TrackType = "video", Codec = "h264", Name = "Video" });
        _mediaProbeServiceMock.Setup(p => p.ProbeAsync(tempSourceFile)).ReturnsAsync(structure);

        var settings = new Dictionary<string, object>
        {
            { "encoder", "nvenc" },
            { "nvenc_codec", "AV1" },
            { "lossless", true },
            { "force_10bit", false }
        };

        try
        {
            // Act.
            ExecutionResult result = await script.ExecuteSingleAsync(
                tempSourceFile, settings, tempOutputDir, (idx, total, status, pct, fps, bit) => { }, 0, 1);

            // Assert.
            result.Status.Should().Be(ExecutionStatus.Failed);
            result.ErrorCode.Should().Be("video-encoding-exception");
            IReadOnlyList<RecordedLogEvent> failures = _logService.EventsById("script.video_encoding.failed");
            failures.Should().NotBeEmpty();
            failures[0].GetProperty<bool>("Retryable").Should().BeFalse(
                "ошибка конфигурации не лечится повтором");
            failures[0].Exception.Should().NotBeNull();
            failures[0].Exception!.Message.Should().Contain("Lossless");
        }
        finally
        {
            if (File.Exists(tempSourceFile))
            {
                File.Delete(tempSourceFile);
            }
        }
    }
}
