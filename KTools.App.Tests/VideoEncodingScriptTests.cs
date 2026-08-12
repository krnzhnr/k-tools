// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Scripts;
using KTools_App.Services.Contracts;
using KTools_App.Infrastructure;

namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты для комплексного скрипта кодирования видео VideoEncodingScript.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class VideoEncodingScriptTests
{
    private Mock<ILogService> _logServiceMock = null!;
    private Mock<ISettingsManager> _settingsManagerMock = null!;
    private Mock<IPathManager> _pathManagerMock = null!;
    private Mock<IFFmpegRunner> _ffmpegRunnerMock = null!;
    private Mock<IMediaProbeService> _mediaProbeServiceMock = null!;
    private VideoEncodingScript _script = null!;

    [TestInitialize]
    public void Setup()
    {
        _logServiceMock = new Mock<ILogService>();
        _settingsManagerMock = new Mock<ISettingsManager>();
        _pathManagerMock = new Mock<IPathManager>();
        _ffmpegRunnerMock = new Mock<IFFmpegRunner>();
        _mediaProbeServiceMock = new Mock<IMediaProbeService>();

        // Инициализируем мок для фонового определения NVENC
        _ffmpegRunnerMock.Setup(r => r.CheckNvencSupportAsync()).ReturnsAsync(true);

        // Инициализируем реестр энкодеров для тестов
        var encoders = new List<KTools_App.Encoders.IVideoEncoder>
        {
            new KTools_App.Encoders.NvencEncoder(),
            new KTools_App.Encoders.X265Encoder()
        };
        var hardwareCacheMock = new Mock<KTools_App.Encoders.IHardwareCapabilityCache>();
        hardwareCacheMock.Setup(c => c.IsNvencSupported).Returns(true);
        var registry = new KTools_App.Encoders.VideoEncoderRegistry(encoders, hardwareCacheMock.Object);

        _script = new VideoEncodingScript(
            _logServiceMock.Object,
            _settingsManagerMock.Object,
            _pathManagerMock.Object,
            _ffmpegRunnerMock.Object,
            _mediaProbeServiceMock.Object,
            registry
        );
    }

    /// <summary>
    /// Проверяет базовые свойства скрипта кодирования видео.
    /// </summary>
    [TestMethod]
    public void ScriptProperties_VerifyCorrectValues()
    {
        _script.Category.Should().Be("Видео");
        _script.IconName.Should().Be(AppConstants.ScriptIcons.VideoEncoding);
        _script.FileExtensions.Should().Contain(".mkv");
        _script.RequiredDependencies.Should().Contain("ffmpeg");
    }

    /// <summary>
    /// Проверяет построение аргументов командной строки для NVENC при включенном режиме Lossless.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_NvencLossless_GeneratesCorrectArgs()
    {
        // Arrange
        string tempSourceFile = Path.GetTempFileName();
        string tempOutputDir = Path.GetDirectoryName(tempSourceFile) ?? AppContext.BaseDirectory;

        // Настраиваем зондирование медиафайла
        var structure = new MediaStructure
        {
            FilePath = tempSourceFile,
            Duration = 120.0
        };
        structure.Tracks.Add(new MediaTrack { TrackId = 0, TrackType = "video", Codec = "h264", Name = "Video stream" });
        _mediaProbeServiceMock.Setup(p => p.ProbeAsync(tempSourceFile)).ReturnsAsync(structure);

        // Перехватываем аргументы запуска FFmpeg
        List<string>? capturedExtraArgs = null;
        _ffmpegRunnerMock.Setup(r => r.RunAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<List<string>>(),
            It.IsAny<List<string>>(),
            It.IsAny<bool>(),
            It.IsAny<double>(),
            It.IsAny<Action<ProgressInfo>>(),
            It.IsAny<CancellationToken>()
        )).Callback<string, string, List<string>, List<string>, bool, double, Action<ProgressInfo>, CancellationToken>(
            (inP, outP, extArgs, inArgs, ovr, dur, prog, ct) => capturedExtraArgs = extArgs
        ).ReturnsAsync(true);

        var settings = new Dictionary<string, object>
        {
            { "encoder", "NVENC (GPU)" },
            { "lossless", true },
            { "v_qp", 0 },
            { "nvenc_preset", "p1" },
            { "force_10bit", false }
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
            capturedExtraArgs.Should().NotBeNull();
            capturedExtraArgs.Should().Contain("-c:v");
            capturedExtraArgs.Should().Contain("hevc_nvenc");
            capturedExtraArgs.Should().Contain("-preset");
            capturedExtraArgs.Should().Contain("p1");
            capturedExtraArgs.Should().Contain("-rc");
            capturedExtraArgs.Should().Contain("constqp");
            capturedExtraArgs.Should().Contain("-tune");
            capturedExtraArgs.Should().Contain("lossless");
        }
        finally
        {
            if (File.Exists(tempSourceFile)) File.Delete(tempSourceFile);
        }
    }

    /// <summary>
    /// Проверяет авторасчет битрейта и буфера при обычном кодировании NVENC (без Lossless).
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_NvencWithAutoBitrate_GeneratesCorrectArgs()
    {
        // Arrange
        string tempSourceFile = Path.GetTempFileName();
        string tempOutputDir = Path.GetDirectoryName(tempSourceFile) ?? AppContext.BaseDirectory;

        var structure = new MediaStructure { FilePath = tempSourceFile, Duration = 60.0 };
        structure.Tracks.Add(new MediaTrack { TrackId = 0, TrackType = "video", Codec = "h264", Name = "Video" });
        _mediaProbeServiceMock.Setup(p => p.ProbeAsync(tempSourceFile)).ReturnsAsync(structure);

        List<string>? capturedExtraArgs = null;
        _ffmpegRunnerMock.Setup(r => r.RunAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<List<string>>(),
            It.IsAny<bool>(), It.IsAny<double>(), It.IsAny<Action<ProgressInfo>>(), It.IsAny<CancellationToken>()
        )).Callback<string, string, List<string>, List<string>, bool, double, Action<ProgressInfo>, CancellationToken>(
            (inP, outP, extArgs, inArgs, ovr, dur, prog, ct) => capturedExtraArgs = extArgs
        ).ReturnsAsync(true);

        var settings = new Dictionary<string, object>
        {
            { "encoder", "NVENC (GPU)" },
            { "lossless", false },
            { "nvenc_rc", "vbr_hq" },
            { "v_bitrate", 5000 },
            { "auto_bitrate", true },
            { "nvenc_preset", "p7" },
            { "force_10bit", true }
        };

        try
        {
            // Act
            await _script.ExecuteSingleAsync(tempSourceFile, settings, tempOutputDir, (idx, total, status, pct, fps, bit) => { }, 0, 1);

            // Assert
            capturedExtraArgs.Should().NotBeNull();
            capturedExtraArgs.Should().Contain("p010le"); // Так как force_10bit = true и NVENC
            capturedExtraArgs.Should().Contain("-b:v");
            capturedExtraArgs.Should().Contain("5000k");
            // Проверка авторасчета: minrate = целевой = 5000, maxrate = 5000*2 = 10000, bufsize = maxrate*2 = 20000
            capturedExtraArgs.Should().Contain("-minrate");
            capturedExtraArgs.Should().Contain("5000k");
            capturedExtraArgs.Should().Contain("-maxrate");
            capturedExtraArgs.Should().Contain("10000k");
            capturedExtraArgs.Should().Contain("-bufsize");
            capturedExtraArgs.Should().Contain("20000k");
        }
        finally
        {
            if (File.Exists(tempSourceFile)) File.Delete(tempSourceFile);
        }
    }

    /// <summary>
    /// Проверяет, что при отключенном Lossless генерируется пользовательский пресет и обычный режим битрейта (не constqp).
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_NvencNonLossless_UsesOriginalPresetAndRc()
    {
        // Arrange
        string tempSourceFile = Path.GetTempFileName();
        string tempOutputDir = Path.GetDirectoryName(tempSourceFile) ?? AppContext.BaseDirectory;

        var structure = new MediaStructure { FilePath = tempSourceFile, Duration = 60.0 };
        structure.Tracks.Add(new MediaTrack { TrackId = 0, TrackType = "video", Codec = "h264", Name = "Video" });
        _mediaProbeServiceMock.Setup(p => p.ProbeAsync(tempSourceFile)).ReturnsAsync(structure);

        List<string>? capturedExtraArgs = null;
        _ffmpegRunnerMock.Setup(r => r.RunAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<List<string>>(),
            It.IsAny<bool>(), It.IsAny<double>(), It.IsAny<Action<ProgressInfo>>(), It.IsAny<CancellationToken>()
        )).Callback<string, string, List<string>, List<string>, bool, double, Action<ProgressInfo>, CancellationToken>(
            (inP, outP, extArgs, inArgs, ovr, dur, prog, ct) => capturedExtraArgs = extArgs
        ).ReturnsAsync(true);

        var settings = new Dictionary<string, object>
        {
            { "encoder", "NVENC (GPU)" },
            { "lossless", false }, // Отключен Lossless
            { "nvenc_rc", "vbr_hq" },
            { "v_bitrate", 6000 },
            { "auto_bitrate", false },
            { "min_bitrate", 4000 },
            { "max_bitrate", 8000 },
            { "bufsize", 16000 },
            { "nvenc_preset", "p5" }, // Пользовательский пресет p5 вместо p1
            { "force_10bit", false }
        };

        try
        {
            // Act
            await _script.ExecuteSingleAsync(tempSourceFile, settings, tempOutputDir, (idx, total, status, pct, fps, bit) => { }, 0, 1);

            // Assert
            capturedExtraArgs.Should().NotBeNull();
            capturedExtraArgs.Should().Contain("-preset");
            capturedExtraArgs.Should().Contain("p5"); // Ожидаем p5
            capturedExtraArgs.Should().Contain("-rc");
            capturedExtraArgs.Should().Contain("vbr");
            capturedExtraArgs.Should().NotContain("lossless");
        }
        finally
        {
            if (File.Exists(tempSourceFile)) File.Delete(tempSourceFile);
        }
    }

    /// <summary>
    /// Проверяет генерацию аргументов для программного кодирования (libx265 CPU).
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_CpuLibx265_GeneratesCorrectArgs()
    {
        // Arrange
        string tempSourceFile = Path.GetTempFileName();
        string tempOutputDir = Path.GetDirectoryName(tempSourceFile) ?? AppContext.BaseDirectory;

        var structure = new MediaStructure { FilePath = tempSourceFile, Duration = 60.0 };
        structure.Tracks.Add(new MediaTrack { TrackId = 0, TrackType = "video", Codec = "h264", Name = "Video" });
        _mediaProbeServiceMock.Setup(p => p.ProbeAsync(tempSourceFile)).ReturnsAsync(structure);

        List<string>? capturedExtraArgs = null;
        _ffmpegRunnerMock.Setup(r => r.RunAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<List<string>>(),
            It.IsAny<bool>(), It.IsAny<double>(), It.IsAny<Action<ProgressInfo>>(), It.IsAny<CancellationToken>()
        )).Callback<string, string, List<string>, List<string>, bool, double, Action<ProgressInfo>, CancellationToken>(
            (inP, outP, extArgs, inArgs, ovr, dur, prog, ct) => capturedExtraArgs = extArgs
        ).ReturnsAsync(true);

        var settings = new Dictionary<string, object>
        {
            { "encoder", "x265 (CPU)" },
            { "lossless", false },
            { "cpu_preset", "slower" },
            { "cpu_rc", "CRF" },
            { "cpu_crf", 18 },
            { "cpu_tune", "animation" },
            { "cpu_aq_mode", "2" },
            { "cpu_lookahead", "20" },
            { "force_10bit", false }
        };

        try
        {
            // Act
            await _script.ExecuteSingleAsync(tempSourceFile, settings, tempOutputDir, (idx, total, status, pct, fps, bit) => { }, 0, 1);

            // Assert
            capturedExtraArgs.Should().NotBeNull();
            capturedExtraArgs.Should().Contain("-c:v");
            capturedExtraArgs.Should().Contain("libx265");
            capturedExtraArgs.Should().Contain("-preset");
            capturedExtraArgs.Should().Contain("slower");
            capturedExtraArgs.Should().Contain("-crf");
            capturedExtraArgs.Should().Contain("18");
            capturedExtraArgs.Should().Contain("-tune");
            capturedExtraArgs.Should().Contain("animation");
            capturedExtraArgs.Should().Contain("-x265-params");
            capturedExtraArgs.Should().Contain(s => s.Contains("aq-mode=2") && s.Contains("rc-lookahead=20"));
        }
        finally
        {
            if (File.Exists(tempSourceFile)) File.Delete(tempSourceFile);
        }
    }

    /// <summary>
    /// Проверяет логику вшивания субтитров и временного извлечения встроенных шрифтов.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_WithSubtitlesAndFonts_ExtractsAndBurnsThem()
    {
        // Arrange
        string tempSourceFile = Path.Combine(Path.GetTempPath(), "test_file.mkv");
        File.WriteAllText(tempSourceFile, "dummy media file");

        string tempOutputDir = Path.GetTempPath();

        var structure = new MediaStructure { FilePath = tempSourceFile, Duration = 60.0 };
        // Добавляем видеодорожку
        structure.Tracks.Add(new MediaTrack { TrackId = 0, TrackType = "video", Codec = "h264", Name = "Video" });
        // Добавляем дорожку субтитров
        structure.Tracks.Add(new MediaTrack { TrackId = 1, TrackType = "subtitles", Codec = "ass", Name = "English Subs", IsDefault = true });
        // Добавляем вложенный шрифт
        structure.Attachments.Add(new MediaAttachment { AttachmentId = 0, FileName = "customfont.ttf", MimeType = "application/x-truetype-font" });

        _mediaProbeServiceMock.Setup(p => p.ProbeAsync(tempSourceFile)).ReturnsAsync(structure);

        // Настраиваем извлечение шрифтов
        _ffmpegRunnerMock.Setup(r => r.ExtractAttachmentAsync(tempSourceFile, It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        // Настраиваем извлечение субтитров (записываем фиктивный файл)
        _ffmpegRunnerMock.Setup(r => r.ExtractSubtitleAsync(tempSourceFile, It.IsAny<int>(), It.IsAny<string>(), true))
            .Callback<string, int, string, bool>((inP, idx, outP, rel) => File.WriteAllText(outP, "[Events]\nDialogue: ..."))
            .ReturnsAsync(true);

        List<string>? capturedExtraArgs = null;
        _ffmpegRunnerMock.Setup(r => r.RunAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<List<string>>(),
            It.IsAny<bool>(), It.IsAny<double>(), It.IsAny<Action<ProgressInfo>>(), It.IsAny<CancellationToken>()
        )).Callback<string, string, List<string>, List<string>, bool, double, Action<ProgressInfo>, CancellationToken>(
            (inP, outP, extArgs, inArgs, ovr, dur, prog, ct) => capturedExtraArgs = extArgs
        ).ReturnsAsync(true);

        var settings = new Dictionary<string, object>
        {
            { "encoder", "NVENC (GPU)" },
            { "lossless", true },
            { "v_qp", 0 },
            { "nvenc_preset", "p1" },
            { "force_10bit", false }
        };

        try
        {
            // Act
            await _script.ExecuteSingleAsync(tempSourceFile, settings, tempOutputDir, (idx, total, status, pct, fps, bit) => { }, 0, 1);

            // Assert
            _ffmpegRunnerMock.Verify(r => r.ExtractAttachmentAsync(tempSourceFile, It.IsAny<int>(), It.IsAny<string>()), Times.Once);
            _ffmpegRunnerMock.Verify(r => r.ExtractSubtitleAsync(tempSourceFile, It.IsAny<int>(), It.IsAny<string>(), true), Times.Once);

            capturedExtraArgs.Should().NotBeNull();
            capturedExtraArgs.Should().Contain("-vf");
            capturedExtraArgs.Should().Contain(s => s.Contains("subtitles=filename=") && s.Contains("fontsdir="));
        }
        finally
        {
            if (File.Exists(tempSourceFile)) File.Delete(tempSourceFile);
        }
    }

    /// <summary>
    /// Проверяет генерацию аргументов для программного кодирования (libx265 CPU) в режиме битрейта (ABR).
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_CpuLibx265_BitrateMode_GeneratesCorrectArgs()
    {
        // Arrange
        string tempSourceFile = Path.GetTempFileName();
        string tempOutputDir = Path.GetDirectoryName(tempSourceFile) ?? AppContext.BaseDirectory;

        var structure = new MediaStructure { FilePath = tempSourceFile, Duration = 60.0 };
        structure.Tracks.Add(new MediaTrack { TrackId = 0, TrackType = "video", Codec = "h264", Name = "Video" });
        _mediaProbeServiceMock.Setup(p => p.ProbeAsync(tempSourceFile)).ReturnsAsync(structure);

        List<string>? capturedExtraArgs = null;
        _ffmpegRunnerMock.Setup(r => r.RunAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<List<string>>(),
            It.IsAny<bool>(), It.IsAny<double>(), It.IsAny<Action<ProgressInfo>>(), It.IsAny<CancellationToken>()
        )).Callback<string, string, List<string>, List<string>, bool, double, Action<ProgressInfo>, CancellationToken>(
            (inP, outP, extArgs, inArgs, ovr, dur, prog, ct) => capturedExtraArgs = extArgs
        ).ReturnsAsync(true);

        var settings = new Dictionary<string, object>
        {
            { "encoder", "x265 (CPU)" },
            { "lossless", false },
            { "cpu_preset", "medium" },
            { "cpu_rc", "Битрейт (ABR)" },
            { "cpu_v_bitrate", 5500 },
            { "cpu_tune", "grain" },
            { "cpu_aq_mode", "1" },
            { "cpu_lookahead", "30" },
            { "force_10bit", false }
        };

        try
        {
            // Act
            await _script.ExecuteSingleAsync(tempSourceFile, settings, tempOutputDir, (idx, total, status, pct, fps, bit) => { }, 0, 1);

            // Assert
            capturedExtraArgs.Should().NotBeNull();
            capturedExtraArgs.Should().Contain("-c:v");
            capturedExtraArgs.Should().Contain("libx265");
            capturedExtraArgs.Should().Contain("-b:v");
            capturedExtraArgs.Should().Contain("5500k");
            capturedExtraArgs.Should().Contain("-maxrate");
            capturedExtraArgs.Should().Contain("11000k");
            capturedExtraArgs.Should().Contain("-bufsize");
            capturedExtraArgs.Should().Contain("22000k");
            capturedExtraArgs.Should().Contain("-tune");
            capturedExtraArgs.Should().Contain("grain");
            capturedExtraArgs.Should().Contain("-x265-params");
            capturedExtraArgs.Should().Contain(s => s.Contains("aq-mode=1") && s.Contains("rc-lookahead=30"));
        }
        finally
        {
            if (File.Exists(tempSourceFile)) File.Delete(tempSourceFile);
        }
    }

    /// <summary>
    /// Проверяет корректность выбора аудиодорожки по приоритету языков,
    /// когда в настройках приоритета указан код 'rus', а в дорожке — 'ru'.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_AudioLangPriority_SelectsRuTrackForRusPriority()
    {
        // Arrange
        string tempSourceFile = Path.GetTempFileName();
        string tempOutputDir = Path.GetDirectoryName(tempSourceFile) ?? AppContext.BaseDirectory;

        var structure = new MediaStructure { FilePath = tempSourceFile, Duration = 60.0 };
        // Видеопоток
        structure.Tracks.Add(new MediaTrack { TrackId = 0, TrackType = "video", Codec = "h264", Name = "Видео" });
        // Первая аудиодорожка: японская, установлена как дорожка по умолчанию
        structure.Tracks.Add(new MediaTrack { TrackId = 1, TrackType = "audio", Codec = "aac", Language = "jpn", IsDefault = true, Name = "Japanese Audio" });
        // Вторая аудиодорожка: русская, не по умолчанию
        structure.Tracks.Add(new MediaTrack { TrackId = 2, TrackType = "audio", Codec = "aac", Language = "ru", IsDefault = false, Name = "Russian Audio" });
        
        _mediaProbeServiceMock.Setup(p => p.ProbeAsync(tempSourceFile)).ReturnsAsync(structure);

        List<string>? capturedExtraArgs = null;
        _ffmpegRunnerMock.Setup(r => r.RunAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<List<string>>(),
            It.IsAny<bool>(), It.IsAny<double>(), It.IsAny<Action<ProgressInfo>>(), It.IsAny<CancellationToken>()
        )).Callback<string, string, List<string>, List<string>, bool, double, Action<ProgressInfo>, CancellationToken>(
            (inP, outP, extArgs, inArgs, ovr, dur, prog, ct) => capturedExtraArgs = extArgs
        ).ReturnsAsync(true);

        var settings = new Dictionary<string, object>
        {
            { "encoder", "x265 (CPU)" },
            { "cpu_preset", "medium" },
            { "cpu_rc", "Битрейт (ABR)" },
            { "cpu_v_bitrate", 2000 },
            { "audio_codec", "copy" },
            { "audio_lang_priority", new List<Dictionary<string, object>>
                {
                    new() { { "word", "rus" }, { "active", true } },
                    new() { { "word", "jpn" }, { "active", false } }
                }
            }
        };

        try
        {
            // Act
            await _script.ExecuteSingleAsync(tempSourceFile, settings, tempOutputDir, (idx, total, status, pct, fps, bit) => { }, 0, 1);

            // Assert
            // Проверяем, что в аргументах запуска FFmpeg для аудиопотока выбран относительный индекс 1 (соответствует второй аудиодорожке, т.е. TrackId=2)
            // Картографирование дорожек в FFmpeg для аудио: "-map 0:a:1"
            capturedExtraArgs.Should().NotBeNull();
            capturedExtraArgs.Should().Contain("-map");
            capturedExtraArgs.Should().Contain("0:a:1?");
        }
        finally
        {
            if (File.Exists(tempSourceFile)) File.Delete(tempSourceFile);
        }
    }

    /// <summary>
    /// Проверяет, что при использовании кодека NVENC HEVC добавляется флаг -tag:v hvc1 для совместимости с MP4.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_NvencHevc_IncludesTagHvc1()
    {
        // Arrange
        string tempSourceFile = Path.GetTempFileName();
        string tempOutputDir = Path.GetDirectoryName(tempSourceFile) ?? AppContext.BaseDirectory;

        var structure = new MediaStructure { FilePath = tempSourceFile, Duration = 30.0 };
        structure.Tracks.Add(new MediaTrack { TrackId = 0, TrackType = "video", Codec = "h264", Name = "Video" });
        _mediaProbeServiceMock.Setup(p => p.ProbeAsync(tempSourceFile)).ReturnsAsync(structure);

        List<string>? capturedExtraArgs = null;
        _ffmpegRunnerMock.Setup(r => r.RunAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<List<string>>(),
            It.IsAny<bool>(), It.IsAny<double>(), It.IsAny<Action<ProgressInfo>>(), It.IsAny<CancellationToken>()
        )).Callback<string, string, List<string>, List<string>, bool, double, Action<ProgressInfo>, CancellationToken>(
            (inP, outP, extArgs, inArgs, ovr, dur, prog, ct) => capturedExtraArgs = extArgs
        ).ReturnsAsync(true);

        var settings = new Dictionary<string, object>
        {
            { "encoder", "NVENC (GPU)" },
            { "lossless", true },
            { "v_qp", 0 }
        };

        try
        {
            // Act
            string targetMp4 = Path.Combine(tempOutputDir, "test_output.mp4");
            await _script.ExecuteSingleAsync(tempSourceFile, settings, targetMp4, (idx, total, status, pct, fps, bit) => { }, 0, 1);

            // Assert
            capturedExtraArgs.Should().NotBeNull();
            capturedExtraArgs.Should().Contain("-tag:v");
            capturedExtraArgs.Should().Contain("hvc1");
        }
        finally
        {
            if (File.Exists(tempSourceFile)) File.Delete(tempSourceFile);
        }
    }
}
