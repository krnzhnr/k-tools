// -*- coding: utf-8 -*-
using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Services.Contracts;
using KTools_App.Tests.TestHelpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace KTools_App.Tests.Core;

/// <summary>
/// Юнит-тесты частоты кадров и режима CFR/VFR: разбор дробей ffprobe,
/// эвристика VFR, фолбэк default_duration mkvmerge, форматирование бейджей ru-RU
/// и уведомления FileQueueItem по образцу CropBadgeText.
/// Все комментарии выполнены на русском языке.
/// </summary>
[TestClass]
public class FrameRateBadgeTests
{
    private static MediaProbeService CreateService(
        Mock<ILogService> log,
        Mock<IMkvmergeRunner> mkv,
        Mock<IFFmpegRunner> ffprobe)
    {
        return new MediaProbeService(log.Object, mkv.Object, ffprobe.Object);
    }

    /// <summary>
    /// Дробь 30000/1001 разбирается делением и при равном r даёт CFR.
    /// </summary>
    [TestMethod]
    public async Task ProbeAsync_FractionalFrameRate_ParsesAsDivisionAndMarksCfr()
    {
        // Arrange
        string testPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(testPath, "dummy media content");
            string json = """
            {
                "format": { "duration": "60.0" },
                "streams": [
                    {
                        "index": 0,
                        "codec_name": "h264",
                        "codec_type": "video",
                        "width": 1920,
                        "height": 1080,
                        "avg_frame_rate": "30000/1001",
                        "r_frame_rate": "30000/1001",
                        "nb_frames": "1798"
                    }
                ]
            }
            """;
            var log = new Mock<ILogService>();
            var mkv = new Mock<IMkvmergeRunner>();
            var ffprobe = new Mock<IFFmpegRunner>();
            ffprobe.Setup(f => f.GetVideoInfoAsync(testPath)).ReturnsAsync(System.Text.Json.JsonDocument.Parse(json));
            var service = CreateService(log, mkv, ffprobe);

            // Act
            var result = await service.ProbeAsync(testPath);

            // Assert
            result.Should().NotBeNull();
            var video = result!.GetVideoTracks()[0];
            video.FrameRate.Should().BeApproximately(29.97, 0.001);
            video.IsVariableFrameRate.Should().BeFalse();
        }
        finally
        {
            if (File.Exists(testPath)) File.Delete(testPath);
        }
    }

    /// <summary>
    /// Расхождение avg и r за допуском 0,01 даёт VFR.
    /// </summary>
    [TestMethod]
    public async Task ProbeAsync_AvgDiffersFromR_MarksVfr()
    {
        // Arrange
        string testPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(testPath, "dummy media content");
            string json = """
            {
                "format": { "duration": "100.0" },
                "streams": [
                    {
                        "index": 0,
                        "codec_name": "h264",
                        "codec_type": "video",
                        "width": 1280,
                        "height": 720,
                        "avg_frame_rate": "24000/1001",
                        "r_frame_rate": "24/1"
                    }
                ]
            }
            """;
            var log = new Mock<ILogService>();
            var mkv = new Mock<IMkvmergeRunner>();
            var ffprobe = new Mock<IFFmpegRunner>();
            ffprobe.Setup(f => f.GetVideoInfoAsync(testPath)).ReturnsAsync(System.Text.Json.JsonDocument.Parse(json));
            var service = CreateService(log, mkv, ffprobe);

            // Act
            var result = await service.ProbeAsync(testPath);

            // Assert
            result.Should().NotBeNull();
            var video = result!.GetVideoTracks()[0];
            video.FrameRate.Should().BeApproximately(23.976, 0.001);
            video.IsVariableFrameRate.Should().BeTrue();
        }
        finally
        {
            if (File.Exists(testPath)) File.Delete(testPath);
        }
    }

    /// <summary>
    /// Равные avg и r, но число кадров далеко от duration * avg, дают VFR.
    /// </summary>
    [TestMethod]
    public async Task ProbeAsync_NbFramesMismatch_MarksVfr()
    {
        // Arrange
        string testPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(testPath, "dummy media content");
            string json = """
            {
                "format": { "duration": "100.0" },
                "streams": [
                    {
                        "index": 0,
                        "codec_name": "h264",
                        "codec_type": "video",
                        "width": 1920,
                        "height": 1080,
                        "avg_frame_rate": "25/1",
                        "r_frame_rate": "25/1",
                        "nb_frames": "2000"
                    }
                ]
            }
            """;
            var log = new Mock<ILogService>();
            var mkv = new Mock<IMkvmergeRunner>();
            var ffprobe = new Mock<IFFmpegRunner>();
            ffprobe.Setup(f => f.GetVideoInfoAsync(testPath)).ReturnsAsync(System.Text.Json.JsonDocument.Parse(json));
            var service = CreateService(log, mkv, ffprobe);

            // Act
            var result = await service.ProbeAsync(testPath);

            // Assert
            result.Should().NotBeNull();
            var video = result!.GetVideoTracks()[0];
            video.FrameRate.Should().BeApproximately(25.0, 0.001);
            video.IsVariableFrameRate.Should().BeTrue();
        }
        finally
        {
            if (File.Exists(testPath)) File.Delete(testPath);
        }
    }

    /// <summary>
    /// Мусор 0/0 не выдумывает данные: частота и режим остаются null.
    /// </summary>
    [TestMethod]
    public async Task ProbeAsync_ZeroOverZero_ReturnsNullFrameRate()
    {
        // Arrange
        string testPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(testPath, "dummy media content");
            string json = """
            {
                "format": { "duration": "10.0" },
                "streams": [
                    {
                        "index": 0,
                        "codec_name": "h264",
                        "codec_type": "video",
                        "width": 640,
                        "height": 480,
                        "avg_frame_rate": "0/0",
                        "r_frame_rate": "0/0"
                    }
                ]
            }
            """;
            var log = new Mock<ILogService>();
            var mkv = new Mock<IMkvmergeRunner>();
            var ffprobe = new Mock<IFFmpegRunner>();
            ffprobe.Setup(f => f.GetVideoInfoAsync(testPath)).ReturnsAsync(System.Text.Json.JsonDocument.Parse(json));
            var service = CreateService(log, mkv, ffprobe);

            // Act
            var result = await service.ProbeAsync(testPath);

            // Assert
            result.Should().NotBeNull();
            var video = result!.GetVideoTracks()[0];
            video.FrameRate.Should().BeNull();
            video.IsVariableFrameRate.Should().BeNull();
        }
        finally
        {
            if (File.Exists(testPath)) File.Delete(testPath);
        }
    }

    /// <summary>
    /// Фолбэк MKV: default_duration в нс/кадр даёт FPS = 1e9 / default_duration.
    /// </summary>
    [TestMethod]
    public async Task ProbeAsync_MkvDefaultDuration_FallsBackToFps()
    {
        // Arrange
        string testPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".mkv");
        try
        {
            File.WriteAllText(testPath, "dummy mkv content");
            string mkvJson = """
            {
                "container": { "properties": { "duration": 60000000000 } },
                "tracks": [
                    {
                        "id": 0,
                        "type": "video",
                        "codec": "h264",
                        "properties": { "default_duration": 41708333 }
                    }
                ],
                "attachments": []
            }
            """;
            var log = new Mock<ILogService>();
            var mkv = new Mock<IMkvmergeRunner>();
            var ffprobe = new Mock<IFFmpegRunner>();
            mkv.Setup(f => f.IdentifyAsync(testPath)).ReturnsAsync(System.Text.Json.JsonDocument.Parse(mkvJson));
            ffprobe.Setup(f => f.GetVideoInfoAsync(testPath)).ReturnsAsync((System.Text.Json.JsonDocument?)null);
            var service = CreateService(log, mkv, ffprobe);

            // Act
            var result = await service.ProbeAsync(testPath);

            // Assert
            result.Should().NotBeNull();
            var video = result!.GetVideoTracks()[0];
            video.FrameRate.Should().BeApproximately(23.976, 0.01);
            video.IsVariableFrameRate.Should().BeNull("без ffprobe режим не выдумываем");
        }
        finally
        {
            if (File.Exists(testPath)) File.Delete(testPath);
        }
    }

    /// <summary>
    /// CFR форматируется ru-RU с запятой: 23,976 fps и бейдж CFR.
    /// </summary>
    [TestMethod]
    public void FrameRateBadge_Cfr_FormatsWithComma()
    {
        // Arrange
        var item = new FileQueueItem("C:\\video.mkv");
        var structure = new MediaStructure { FilePath = "C:\\video.mkv" };
        structure.Tracks.Add(new MediaTrack { TrackType = "video", FrameRate = 24000.0 / 1001.0, IsVariableFrameRate = false });

        // Act
        item.MediaInfo = structure;

        // Assert
        item.FrameRateBadgeText.Should().Be("23,976 fps");
        item.FrameModeBadgeText.Should().Be("CFR");
        item.HasFrameRateBadge.Should().BeTrue();
        item.HasFrameModeBadge.Should().BeTrue();
        item.IsVariableFrameRate.Should().BeFalse();
    }

    /// <summary>
    /// VFR отличается префиксом ~ и бейджем VFR.
    /// </summary>
    [TestMethod]
    public void FrameRateBadge_Vfr_AddsTildePrefix()
    {
        // Arrange
        var item = new FileQueueItem("C:\\video.mkv");
        var structure = new MediaStructure { FilePath = "C:\\video.mkv" };
        structure.Tracks.Add(new MediaTrack { TrackType = "video", FrameRate = 29.97, IsVariableFrameRate = true });

        // Act
        item.MediaInfo = structure;

        // Assert
        item.FrameRateBadgeText.Should().Be("~29,97 fps");
        item.FrameModeBadgeText.Should().Be("VFR");
    }

    /// <summary>
    /// Целое значение 25 показывает без дробной части.
    /// </summary>
    [TestMethod]
    public void FrameRateBadge_WholeNumber_OmitsDecimals()
    {
        // Arrange
        var item = new FileQueueItem("C:\\video.mkv");
        var structure = new MediaStructure { FilePath = "C:\\video.mkv" };
        structure.Tracks.Add(new MediaTrack { TrackType = "video", FrameRate = 25.0, IsVariableFrameRate = false });

        // Act
        item.MediaInfo = structure;

        // Assert
        item.FrameRateBadgeText.Should().Be("25 fps");
    }

    /// <summary>
    /// Без данных зонда бейджи скрыты: тексты null.
    /// </summary>
    [TestMethod]
    public void FrameRateBadge_Unknown_HidesBadges()
    {
        // Arrange
        var item = new FileQueueItem("C:\\video.mkv");
        var structure = new MediaStructure { FilePath = "C:\\video.mkv" };
        structure.Tracks.Add(new MediaTrack { TrackType = "video" });

        // Act
        item.MediaInfo = structure;

        // Assert
        item.FrameRateBadgeText.Should().BeNull();
        item.FrameModeBadgeText.Should().BeNull();
        item.HasFrameRateBadge.Should().BeFalse();
        item.HasFrameModeBadge.Should().BeFalse();
        item.IsVariableFrameRate.Should().BeFalse();
    }

    /// <summary>
    /// Установка MediaInfo синхронно уведомляет все бейджевые свойства.
    /// </summary>
    [TestMethod]
    public void MediaInfo_SetValue_NotifiesFrameRateBadges()
    {
        // Arrange
        var item = new FileQueueItem("C:\\video.mkv");
        var recorder = new PropertyChangedRecorder(item);
        var structure = new MediaStructure { FilePath = "C:\\video.mkv" };
        structure.Tracks.Add(new MediaTrack { TrackType = "video", FrameRate = 25.0, IsVariableFrameRate = false });

        // Act
        item.MediaInfo = structure;

        // Assert
        recorder.GetEventsFor(nameof(FileQueueItem.MediaInfo)).Should().HaveCount(1);
        recorder.GetEventsFor(nameof(FileQueueItem.FrameRateBadgeText)).Should().HaveCount(1);
        recorder.GetEventsFor(nameof(FileQueueItem.FrameModeBadgeText)).Should().HaveCount(1);
        recorder.GetEventsFor(nameof(FileQueueItem.HasFrameRateBadge)).Should().HaveCount(1);
        recorder.GetEventsFor(nameof(FileQueueItem.HasFrameModeBadge)).Should().HaveCount(1);
        recorder.GetEventsFor(nameof(FileQueueItem.IsVariableFrameRate)).Should().HaveCount(1);
        recorder.Detach();
    }
}
