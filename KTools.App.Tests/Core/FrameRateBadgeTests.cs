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
    /// CFR форматируется ru-RU с запятой: 23,976 FPS и бейдж CFR.
    /// </summary>
    [TestMethod]
    public void FrameRateBadge_Cfr_FormatsWithComma()
    {
        // Arrange
        var track = new MediaTrack { TrackType = "video", FrameRate = 24000.0 / 1001.0, IsVariableFrameRate = false };

        // Act
        string? fpsText = FileQueueItem.FormatFrameRateBadge(track);
        string? modeText = FileQueueItem.FormatFrameModeBadge(track);

        // Assert
        fpsText.Should().Be("23,976 FPS");
        modeText.Should().Be("CFR");
    }

    /// <summary>
    /// VFR отличается префиксом ~ и бейджем VFR.
    /// </summary>
    [TestMethod]
    public void FrameRateBadge_Vfr_AddsTildePrefix()
    {
        // Arrange
        var track = new MediaTrack { TrackType = "video", FrameRate = 29.97, IsVariableFrameRate = true };

        // Act
        string? fpsText = FileQueueItem.FormatFrameRateBadge(track);
        string? modeText = FileQueueItem.FormatFrameModeBadge(track);

        // Assert
        fpsText.Should().Be("~29,97 FPS");
        modeText.Should().Be("VFR");
    }

    /// <summary>
    /// Целое значение 25 показывает без дробной части.
    /// </summary>
    [TestMethod]
    public void FrameRateBadge_WholeNumber_OmitsDecimals()
    {
        // Arrange
        var track = new MediaTrack { TrackType = "video", FrameRate = 25.0, IsVariableFrameRate = false };

        // Act
        string? fpsText = FileQueueItem.FormatFrameRateBadge(track);

        // Assert
        fpsText.Should().Be("25 FPS");
    }

    /// <summary>
    /// Без данных зонда форматтеры возвращают null, свойства скрывают бейджи.
    /// </summary>
    [TestMethod]
    public void FrameRateBadge_Unknown_HidesBadges()
    {
        // Arrange
        var track = new MediaTrack { TrackType = "video" };
        var item = new FileQueueItem("C:\\video.mkv");
        var structure = new MediaStructure { FilePath = "C:\\video.mkv" };
        structure.Tracks.Add(track);

        // Act
        item.MediaInfo = structure;

        // Assert
        FileQueueItem.FormatFrameRateBadge(track).Should().BeNull();
        FileQueueItem.FormatFrameModeBadge(track).Should().BeNull();
        item.FrameRateBadgeText.Should().BeNull();
        item.FrameModeBadgeText.Should().BeNull();
        item.HasFrameRateBadge.Should().BeFalse();
        item.HasFrameModeBadge.Should().BeFalse();
        item.IsVariableFrameRate.Should().BeFalse();
    }

    /// <summary>
    /// Признак VFR первой видеодорожки читается без учёта настроек отображения.
    /// </summary>
    [TestMethod]
    public void IsVariableFrameRate_ReflectsFirstVideoTrack()
    {
        // Arrange
        var item = new FileQueueItem("C:\\video.mkv");
        var structure = new MediaStructure { FilePath = "C:\\video.mkv" };
        structure.Tracks.Add(new MediaTrack { TrackType = "video", FrameRate = 29.97, IsVariableFrameRate = true });

        // Act
        item.MediaInfo = structure;

        // Assert
        item.IsVariableFrameRate.Should().BeTrue();
    }

    /// <summary>
    /// Без зарегистрированных сервисов показ пилюль запрещён по умолчанию.
    /// </summary>
    [TestMethod]
    public void ShowBadges_WithoutServices_DefaultsToFalse()
    {
        // Arrange
        var item = new FileQueueItem("C:\\video.mkv");

        // Act & Assert
        item.ShowFrameRateBadge.Should().BeFalse();
        item.ShowFrameModeBadge.Should().BeFalse();
    }

    /// <summary>
    /// Оповещение о смене настроек поднимает события видимости пилюль.
    /// </summary>
    [TestMethod]
    public void NotifyBadgeSettingsChanged_RaisesBadgeVisibilityEvents()
    {
        // Arrange
        var item = new FileQueueItem("C:\\video.mkv");
        var recorder = new PropertyChangedRecorder(item);

        // Act
        item.NotifyBadgeSettingsChanged();

        // Assert
        recorder.GetEventsFor(nameof(FileQueueItem.ShowFrameRateBadge)).Should().HaveCount(1);
        recorder.GetEventsFor(nameof(FileQueueItem.ShowFrameModeBadge)).Should().HaveCount(1);
        recorder.GetEventsFor(nameof(FileQueueItem.FrameRateBadgeText)).Should().HaveCount(1,
            "XAML привязан именно к тексту пилюли, AccentBadge скрывается по пустому тексту");
        recorder.GetEventsFor(nameof(FileQueueItem.FrameModeBadgeText)).Should().HaveCount(1,
            "XAML привязан именно к тексту пилюли, AccentBadge скрывается по пустому тексту");
        recorder.GetEventsFor(nameof(FileQueueItem.HasFrameRateBadge)).Should().HaveCount(1);
        recorder.GetEventsFor(nameof(FileQueueItem.HasFrameModeBadge)).Should().HaveCount(1);
        recorder.Detach();
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

    /// <summary>
    /// Замер около стандарта пишется как есть, без подмены номиналом: 24,0006 даёт 24,001 FPS.
    /// </summary>
    [TestMethod]
    public void FormatFrameRateBadge_NearStandardCfr_ShowsMeasuredValue()
    {
        // Arrange
        var track = new MediaTrack { TrackType = "video", FrameRate = 24.0006, IsVariableFrameRate = false };

        // Act
        string? fpsText = FileQueueItem.FormatFrameRateBadge(track);

        // Assert
        fpsText.Should().Be("24,001 FPS");
    }

    /// <summary>
    /// Дробный CFR пишется как есть: 23,97648 даёт 23,976 FPS.
    /// </summary>
    [TestMethod]
    public void FormatFrameRateBadge_FractionalCfr_ShowsMeasuredValue()
    {
        // Arrange
        var track = new MediaTrack { TrackType = "video", FrameRate = 23.97648, IsVariableFrameRate = false };

        // Act
        string? fpsText = FileQueueItem.FormatFrameRateBadge(track);

        // Assert
        fpsText.Should().Be("23,976 FPS");
    }

    /// <summary>
    /// Нестандартный CFR пишется как есть, без замены на похожий стандарт.
    /// </summary>
    [TestMethod]
    public void FormatFrameRateBadge_NonStandardCfr_KeepsAsIs()
    {
        // Arrange
        var track = new MediaTrack { TrackType = "video", FrameRate = 24.01, IsVariableFrameRate = false };

        // Act
        string? fpsText = FileQueueItem.FormatFrameRateBadge(track);

        // Assert
        fpsText.Should().Be("24,01 FPS");
    }

    /// <summary>
    /// Околостандартный VFR не снапится: показывает среднее как есть с тильдой.
    /// </summary>
    [TestMethod]
    public void FormatFrameRateBadge_NearStandardVfr_KeepsRawWithTilde()
    {
        // Arrange
        var track = new MediaTrack { TrackType = "video", FrameRate = 932832.0 / 38867.0, IsVariableFrameRate = true };

        // Act
        string? fpsText = FileQueueItem.FormatFrameRateBadge(track);

        // Assert
        fpsText.Should().Be("~24,001 FPS");
    }

    /// <summary>
    /// Строка размера объединяет размер, FPS и режим разделителем « • ».
    /// </summary>
    [TestMethod]
    public void FormatFileSizeDetails_BothSegments_JoinsWithBullets()
    {
        // Arrange
        var track = new MediaTrack { TrackType = "video", FrameRate = 24000.0 / 1001.0, IsVariableFrameRate = false };

        // Act
        string line = FileQueueItem.FormatFileSizeDetails("12,4 МБ", track, showFps: true, showMode: true);

        // Assert
        line.Should().Be("12,4 МБ • 23,976 FPS • CFR");
    }

    /// <summary>
    /// Строка размера с одним включённым сегментом показывает только его, без лишнего разделителя.
    /// </summary>
    [TestMethod]
    public void FormatFileSizeDetails_OnlyFps_ReturnsSingleSegment()
    {
        // Arrange
        var track = new MediaTrack { TrackType = "video", FrameRate = 25.0, IsVariableFrameRate = false };

        // Act
        string line = FileQueueItem.FormatFileSizeDetails("12,4 МБ", track, showFps: true, showMode: false);

        // Assert
        line.Should().Be("12,4 МБ • 25 FPS");
    }

    /// <summary>
    /// Строка размера с одним включённым сегментом режима показывает только его.
    /// </summary>
    [TestMethod]
    public void FormatFileSizeDetails_OnlyMode_ReturnsSingleSegment()
    {
        // Arrange
        var track = new MediaTrack { TrackType = "video", FrameRate = 25.0, IsVariableFrameRate = true };

        // Act
        string line = FileQueueItem.FormatFileSizeDetails("12,4 МБ", track, showFps: false, showMode: true);

        // Assert
        line.Should().Be("12,4 МБ • VFR");
    }

    /// <summary>
    /// Строка размера без данных зонда или сегментов возвращает чистый размер, ничего не выдумывает.
    /// </summary>
    [TestMethod]
    public void FormatFileSizeDetails_Unknown_ReturnsSizeOnly()
    {
        // Arrange
        var track = new MediaTrack { TrackType = "video" };

        // Act & Assert
        FileQueueItem.FormatFileSizeDetails("12,4 МБ", track, showFps: true, showMode: true).Should().Be("12,4 МБ");
        FileQueueItem.FormatFileSizeDetails("12,4 МБ", null, showFps: true, showMode: true).Should().Be("12,4 МБ");
        FileQueueItem.FormatFileSizeDetails(
            "12,4 МБ",
            new MediaTrack { TrackType = "video", FrameRate = 25.0, IsVariableFrameRate = false },
            showFps: false,
            showMode: false).Should().Be("12,4 МБ");
    }

    /// <summary>
    /// Без зарегистрированных сервисов строка размера — чистый размер (режим «Скрыто»).
    /// </summary>
    [TestMethod]
    public void FileSizeDetailsText_WithoutServices_ReturnsSizeOnly()
    {
        // Arrange
        var item = new FileQueueItem("C:\\video.mkv");
        var structure = new MediaStructure { FilePath = "C:\\video.mkv" };
        structure.Tracks.Add(new MediaTrack { TrackType = "video", FrameRate = 24000.0 / 1001.0, IsVariableFrameRate = false });

        // Act
        item.MediaInfo = structure;

        // Assert
        item.QueueBadgeMode.Should().Be(0);
        item.FileSizeDetailsText.Should().Be(item.FileSizeStr);
    }

    /// <summary>
    /// Оповещение о смене настроек поднимает событие строки размера для XAML.
    /// </summary>
    [TestMethod]
    public void NotifyBadgeSettingsChanged_RaisesDetailsTextEvent()
    {
        // Arrange
        var item = new FileQueueItem("C:\\video.mkv");
        var recorder = new PropertyChangedRecorder(item);

        // Act
        item.NotifyBadgeSettingsChanged();

        // Assert
        recorder.GetEventsFor(nameof(FileQueueItem.FileSizeDetailsText)).Should().HaveCount(1,
            "XAML строки размера привязан именно к FileSizeDetailsText");
        recorder.Detach();
    }

    /// <summary>
    /// Установка MediaInfo уведомляет и о строке размера, а не только о пилюлях.
    /// </summary>
    [TestMethod]
    public void MediaInfo_SetValue_NotifiesFileSizeDetailsText()
    {
        // Arrange
        var item = new FileQueueItem("C:\\video.mkv");
        var recorder = new PropertyChangedRecorder(item);
        var structure = new MediaStructure { FilePath = "C:\\video.mkv" };
        structure.Tracks.Add(new MediaTrack { TrackType = "video", FrameRate = 25.0, IsVariableFrameRate = false });

        // Act
        item.MediaInfo = structure;

        // Assert
        recorder.GetEventsFor(nameof(FileQueueItem.FileSizeDetailsText)).Should().HaveCount(1);
        recorder.Detach();
    }
}
