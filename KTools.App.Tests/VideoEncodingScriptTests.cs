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
using KTools_App.Models;
using KTools_App.Scripts;
using KTools_App.Services.Contracts;
using KTools_App.Infrastructure;
using KTools_App.Tests.TestHelpers;

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
        )).Callback<string, string, List<string>, List<string>, bool, double, Action<ProgressInfo>, CancellationToken, ProcessExecutionContext>(
            (inP, outP, extArgs, inArgs, ovr, dur, prog, ct, processContext) =>capturedExtraArgs = extArgs
        ).ReturnsAsync(MockBuilders.ProcessSucceeded());

        var settings = new Dictionary<string, object>
        {
            { "encoder", "NVENC" },
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
        )).Callback<string, string, List<string>, List<string>, bool, double, Action<ProgressInfo>, CancellationToken, ProcessExecutionContext>(
            (inP, outP, extArgs, inArgs, ovr, dur, prog, ct, processContext) =>capturedExtraArgs = extArgs
        ).ReturnsAsync(MockBuilders.ProcessSucceeded());

        var settings = new Dictionary<string, object>
        {
            { "encoder", "NVENC" },
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
        )).Callback<string, string, List<string>, List<string>, bool, double, Action<ProgressInfo>, CancellationToken, ProcessExecutionContext>(
            (inP, outP, extArgs, inArgs, ovr, dur, prog, ct, processContext) =>capturedExtraArgs = extArgs
        ).ReturnsAsync(MockBuilders.ProcessSucceeded());

        var settings = new Dictionary<string, object>
        {
            { "encoder", "NVENC" },
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
        )).Callback<string, string, List<string>, List<string>, bool, double, Action<ProgressInfo>, CancellationToken, ProcessExecutionContext>(
            (inP, outP, extArgs, inArgs, ovr, dur, prog, ct, processContext) =>capturedExtraArgs = extArgs
        ).ReturnsAsync(MockBuilders.ProcessSucceeded());

        var settings = new Dictionary<string, object>
        {
            { "encoder", "x265" },
            { "lossless", false },
            { "x265_preset", "slower" },
            { "x265_rc", "CRF" },
            { "x265_crf", 18 },
            { "x265_tune", "animation" },
            { "x265_aq_mode", "2" },
            { "x265_lookahead", "20" },
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
            capturedExtraArgs.Should().Contain(s => s.Contains("aq-mode=2"));
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
        // Добавляем дорожку субтитров надписей
        structure.Tracks.Add(new MediaTrack { TrackId = 1, TrackType = "subtitles", Codec = "ass", Name = "Надписи", IsDefault = true });
        // Добавляем вложенный шрифт
        structure.Attachments.Add(new MediaAttachment { AttachmentId = 0, FileName = "customfont.ttf", MimeType = "application/x-truetype-font" });

        _mediaProbeServiceMock.Setup(p => p.ProbeAsync(tempSourceFile)).ReturnsAsync(structure);

        // Настраиваем извлечение шрифтов: пакетный запуск ничего не извлекает (fallback по одному файлу)
        _ffmpegRunnerMock.Setup(r => r.ExtractAttachmentsBatchAsync(tempSourceFile, It.IsAny<List<(int StreamIndex, string OutputPath)>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string>());

        _ffmpegRunnerMock.Setup(r => r.ExtractAttachmentAsync(tempSourceFile, It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MockBuilders.ProcessSucceeded());

        // Настраиваем извлечение субтитров (записываем фиктивный файл)
        _ffmpegRunnerMock.Setup(r => r.ExtractSubtitleAsync(tempSourceFile, It.IsAny<int>(), It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
            .Callback<string, int, string, bool, CancellationToken, ProcessExecutionContext>((inP, idx, outP, rel, ct, processContext) =>File.WriteAllText(outP, "[Events]\nDialogue: ..."))
            .ReturnsAsync(MockBuilders.ProcessSucceeded());

        List<string>? capturedExtraArgs = null;
        _ffmpegRunnerMock.Setup(r => r.RunAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<List<string>>(),
            It.IsAny<bool>(), It.IsAny<double>(), It.IsAny<Action<ProgressInfo>>(), It.IsAny<CancellationToken>()
        )).Callback<string, string, List<string>, List<string>, bool, double, Action<ProgressInfo>, CancellationToken, ProcessExecutionContext>(
            (inP, outP, extArgs, inArgs, ovr, dur, prog, ct, processContext) =>capturedExtraArgs = extArgs
        ).ReturnsAsync(MockBuilders.ProcessSucceeded());

        var settings = new Dictionary<string, object>
        {
            { "encoder", "NVENC" },
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
            _ffmpegRunnerMock.Verify(r => r.ExtractAttachmentAsync(tempSourceFile, It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
            _ffmpegRunnerMock.Verify(r => r.ExtractSubtitleAsync(tempSourceFile, It.IsAny<int>(), It.IsAny<string>(), true, It.IsAny<CancellationToken>()), Times.Once);

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
        )).Callback<string, string, List<string>, List<string>, bool, double, Action<ProgressInfo>, CancellationToken, ProcessExecutionContext>(
            (inP, outP, extArgs, inArgs, ovr, dur, prog, ct, processContext) =>capturedExtraArgs = extArgs
        ).ReturnsAsync(MockBuilders.ProcessSucceeded());

        var settings = new Dictionary<string, object>
        {
            { "encoder", "x265" },
            { "lossless", false },
            { "x265_preset", "medium" },
            { "x265_rc", "Битрейт (ABR)" },
            { "x265_v_bitrate", 5500 },
            { "x265_tune", "grain" },
            { "x265_aq_mode", "1" },
            { "x265_lookahead", "30" },
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
        )).Callback<string, string, List<string>, List<string>, bool, double, Action<ProgressInfo>, CancellationToken, ProcessExecutionContext>(
            (inP, outP, extArgs, inArgs, ovr, dur, prog, ct, processContext) =>capturedExtraArgs = extArgs
        ).ReturnsAsync(MockBuilders.ProcessSucceeded());

        var settings = new Dictionary<string, object>
        {
            { "encoder", "x265" },
            { "x265_preset", "medium" },
            { "x265_rc", "Битрейт (ABR)" },
            { "x265_v_bitrate", 2000 },
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
        )).Callback<string, string, List<string>, List<string>, bool, double, Action<ProgressInfo>, CancellationToken, ProcessExecutionContext>(
            (inP, outP, extArgs, inArgs, ovr, dur, prog, ct, processContext) =>capturedExtraArgs = extArgs
        ).ReturnsAsync(MockBuilders.ProcessSucceeded());

        var settings = new Dictionary<string, object>
        {
            { "encoder", "nvenc" },
            { "output_container", ".mp4" },
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

    /// <summary>
    /// Проверяет, что при burn_in_subtitles = false надписи не извлекаются и не добавлены в фильтр -vf.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_BurnInDisabled_DoesNotAddSubtitlesFilter()
    {
        // Arrange
        string tempSourceFile = Path.GetTempFileName();
        string tempOutputDir = Path.GetDirectoryName(tempSourceFile) ?? AppContext.BaseDirectory;

        var structure = new MediaStructure { FilePath = tempSourceFile, Duration = 30.0 };
        structure.Tracks.Add(new MediaTrack { TrackId = 0, TrackType = "video", Codec = "h264", Name = "Video" });
        structure.Tracks.Add(new MediaTrack { TrackId = 1, TrackType = "subtitle", Codec = "ass", Name = "Надписи" });
        _mediaProbeServiceMock.Setup(p => p.ProbeAsync(tempSourceFile)).ReturnsAsync(structure);

        List<string>? capturedExtraArgs = null;
        _ffmpegRunnerMock.Setup(r => r.RunAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<List<string>>(),
            It.IsAny<bool>(), It.IsAny<double>(), It.IsAny<Action<ProgressInfo>>(), It.IsAny<CancellationToken>()
        )).Callback<string, string, List<string>, List<string>, bool, double, Action<ProgressInfo>, CancellationToken, ProcessExecutionContext>(
            (inP, outP, extArgs, inArgs, ovr, dur, prog, ct, processContext) =>capturedExtraArgs = extArgs
        ).ReturnsAsync(MockBuilders.ProcessSucceeded());

        var settings = new Dictionary<string, object>
        {
            { "encoder", "x265" },
            { "burn_in_subtitles", false }
        };

        try
        {
            // Act
            await _script.ExecuteSingleAsync(tempSourceFile, settings, tempOutputDir, (idx, total, status, pct, fps, bit) => { }, 0, 1);

            // Assert
            capturedExtraArgs.Should().NotBeNull();
            capturedExtraArgs.Should().NotContain("-vf");
        }
        finally
        {
            if (File.Exists(tempSourceFile)) File.Delete(tempSourceFile);
        }
    }

    /// <summary>
    /// Проверяет, что выбранный кастомный контейнер output_container (.mkv) учитывается при сборке пути.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_CustomContainer_GeneratesCorrectExtension()
    {
        // Arrange
        string tempSourceFile = Path.GetTempFileName();
        string tempOutputDir = Path.GetDirectoryName(tempSourceFile) ?? AppContext.BaseDirectory;

        var structure = new MediaStructure { FilePath = tempSourceFile, Duration = 30.0 };
        structure.Tracks.Add(new MediaTrack { TrackId = 0, TrackType = "video", Codec = "h264", Name = "Video" });
        _mediaProbeServiceMock.Setup(p => p.ProbeAsync(tempSourceFile)).ReturnsAsync(structure);

        string? capturedOutputPath = null;
        _ffmpegRunnerMock.Setup(r => r.RunAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<List<string>>(),
            It.IsAny<bool>(), It.IsAny<double>(), It.IsAny<Action<ProgressInfo>>(), It.IsAny<CancellationToken>()
        )).Callback<string, string, List<string>, List<string>, bool, double, Action<ProgressInfo>, CancellationToken, ProcessExecutionContext>(
            (inP, outP, extArgs, inArgs, ovr, dur, prog, ct, processContext) =>capturedOutputPath = outP
        ).ReturnsAsync(MockBuilders.ProcessSucceeded());

        var settings = new Dictionary<string, object>
        {
            { "encoder", "x265" },
            { "output_container", ".mkv" }
        };

        try
        {
            // Act
            await _script.ExecuteSingleAsync(tempSourceFile, settings, tempOutputDir, (idx, total, status, pct, fps, bit) => { }, 0, 1);

            // Assert
            capturedOutputPath.Should().NotBeNull();
            capturedOutputPath.Should().EndWith(".mkv");
        }
        finally
        {
            if (File.Exists(tempSourceFile)) File.Delete(tempSourceFile);
        }
    }

    /// <summary>
    /// Проверяет наличие всех необходимых полей параметров для фильтра автоматической обрезки в схеме настроек (Expander).
    /// </summary>
    [TestMethod]
    public void SettingsSchema_ContainsAutoCropFields()
    {
        // Act
        var schema = _script.SettingsSchema;

        // Assert
        var cropEnabled = schema.FirstOrDefault(f => f.Key == "autocrop_enabled");
        cropEnabled.Should().NotBeNull();
        cropEnabled!.Type.Should().Be(SettingType.Expander);
        cropEnabled.Group.Should().Be("Видео:Фильтры");
        cropEnabled.ChildFields.Should().NotBeNull();
        cropEnabled.ChildFields.Should().HaveCount(8);

        var cropLimit = cropEnabled.ChildFields.FirstOrDefault(f => f.Key == "autocrop_limit");
        cropLimit.Should().NotBeNull();
        cropLimit!.Type.Should().Be(SettingType.Float);

        var cropRound = cropEnabled.ChildFields.FirstOrDefault(f => f.Key == "autocrop_round");
        cropRound.Should().NotBeNull();
        cropRound!.Type.Should().Be(SettingType.Int);

        var cropMode = cropEnabled.ChildFields.FirstOrDefault(f => f.Key == "autocrop_mode");
        cropMode.Should().NotBeNull();
        cropMode!.Type.Should().Be(SettingType.Combo);
        cropMode.Options.Should().Contain(new[] { "black", "mvedges" });

        var cropProbe = cropEnabled.ChildFields.FirstOrDefault(f => f.Key == "autocrop_probe_frames");
        cropProbe.Should().NotBeNull();

        var cropSkip = cropEnabled.ChildFields.FirstOrDefault(f => f.Key == "autocrop_skip_frames");
        cropSkip.Should().NotBeNull();

        var cropReset = cropEnabled.ChildFields.FirstOrDefault(f => f.Key == "autocrop_reset_frames");
        cropReset.Should().NotBeNull();

        var cropPoints = cropEnabled.ChildFields.FirstOrDefault(f => f.Key == "autocrop_probe_points");
        cropPoints.Should().NotBeNull();
        cropPoints!.Type.Should().Be(SettingType.Int);

        var cropTolerance = cropEnabled.ChildFields.FirstOrDefault(f => f.Key == "autocrop_tolerance");
        cropTolerance.Should().NotBeNull();
        cropTolerance!.Type.Should().Be(SettingType.Int);
    }

    /// <summary>
    /// Проверяет, что при включенном AutoCrop выполняется вызов DetectCropAsync и формируется фильтр crop в -vf.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_AutoCropEnabled_AppliesCropFilter()
    {
        // Arrange
        string tempSourceFile = Path.GetTempFileName();
        string tempOutputDir = Path.GetDirectoryName(tempSourceFile) ?? AppContext.BaseDirectory;

        var structure = new MediaStructure { FilePath = tempSourceFile, Duration = 60.0 };
        structure.Tracks.Add(new MediaTrack { TrackId = 0, TrackType = "video", Codec = "h264", Resolution = "1920x1080", Name = "Video" });
        _mediaProbeServiceMock.Setup(p => p.ProbeAsync(tempSourceFile)).ReturnsAsync(structure);

        _ffmpegRunnerMock.Setup(r => r.DetectCropAsync(
            tempSourceFile,
            It.IsAny<double>(),
            It.IsAny<int>(),
            It.IsAny<double>(),
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()
        )).ReturnsAsync("1920:800:0:140");

        List<string>? capturedExtraArgs = null;
        _ffmpegRunnerMock.Setup(r => r.RunAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<List<string>>(),
            It.IsAny<bool>(), It.IsAny<double>(), It.IsAny<Action<ProgressInfo>>(), It.IsAny<CancellationToken>()
        )).Callback<string, string, List<string>, List<string>, bool, double, Action<ProgressInfo>, CancellationToken, ProcessExecutionContext>(
            (inP, outP, extArgs, inArgs, ovr, dur, prog, ct, processContext) =>capturedExtraArgs = extArgs
        ).ReturnsAsync(MockBuilders.ProcessSucceeded());

        var settings = new Dictionary<string, object>
        {
            { "encoder", "x265" },
            { "autocrop_enabled", true },
            { "autocrop_limit", 0.094 },
            { "autocrop_round", 16 },
            { "burn_in_subtitles", false }
        };

        try
        {
            // Act
            await _script.ExecuteSingleAsync(tempSourceFile, settings, tempOutputDir, (idx, total, status, pct, fps, bit) => { }, 0, 1);

            // Assert
            capturedExtraArgs.Should().NotBeNull();
            int vfIdx = capturedExtraArgs!.IndexOf("-vf");
            vfIdx.Should().BeGreaterThanOrEqualTo(0);
            capturedExtraArgs[vfIdx + 1].Should().Be("crop=1920:800:0:140");
        }
        finally
        {
            if (File.Exists(tempSourceFile)) File.Delete(tempSourceFile);
        }
    }

    /// <summary>
    /// Проверяет установку текста бейджика кадрирования на элементе очереди при обнаружении черных полос.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_AutoCropDetected_SetsCropBadgeOnQueueItem()
    {
        // Arrange
        string tempSourceFile = Path.GetTempFileName();
        string tempOutputDir = Path.GetDirectoryName(tempSourceFile) ?? AppContext.BaseDirectory;

        var structure = new MediaStructure { FilePath = tempSourceFile, Duration = 60.0 };
        structure.Tracks.Add(new MediaTrack { TrackId = 0, TrackType = "video", Codec = "hevc", Resolution = "1920x1080", Name = "Video" });
        _mediaProbeServiceMock.Setup(p => p.ProbeAsync(tempSourceFile)).ReturnsAsync(structure);

        _ffmpegRunnerMock.Setup(r => r.DetectCropAsync(
            tempSourceFile,
            It.IsAny<double>(),
            It.IsAny<int>(),
            It.IsAny<double>(),
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()
        )).ReturnsAsync("1920:816:0:132");

        _ffmpegRunnerMock.Setup(r => r.RunAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<List<string>>(),
            It.IsAny<bool>(), It.IsAny<double>(), It.IsAny<Action<ProgressInfo>>(), It.IsAny<CancellationToken>()
        )).ReturnsAsync(MockBuilders.ProcessSucceeded());

        var queueItem = new FileQueueItem(tempSourceFile);
        _script.FilesQueue.Add(queueItem);

        var settings = new Dictionary<string, object>
        {
            { "encoder", "x265" },
            { "autocrop_enabled", true },
            { "burn_in_subtitles", false }
        };

        try
        {
            // Act
            await _script.ExecuteSingleAsync(tempSourceFile, settings, tempOutputDir, (idx, total, status, pct, fps, bit) => { }, 0, 1);

            // Assert
            queueItem.CropBadgeText.Should().Be("1920x1080 ➔ 1920x816");
            queueItem.HasCropBadge.Should().BeTrue();
        }
        finally
        {
            if (File.Exists(tempSourceFile)) File.Delete(tempSourceFile);
        }
    }

    /// <summary>
    /// Проверяет отмену микрообрезки по ширине или высоте, если разница не превышает порог tolerance.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_AutoCropTolerance_CancelsMicroCrop()
    {
        // Arrange
        string tempSourceFile = Path.GetTempFileName();
        string tempOutputDir = Path.GetDirectoryName(tempSourceFile) ?? AppContext.BaseDirectory;

        var structure = new MediaStructure { FilePath = tempSourceFile, Duration = 60.0 };
        structure.Tracks.Add(new MediaTrack { TrackId = 0, TrackType = "video", Codec = "hevc", Resolution = "1920x1080", Name = "Video" });
        _mediaProbeServiceMock.Setup(p => p.ProbeAsync(tempSourceFile)).ReturnsAsync(structure);

        // Имитируем, что детектор отрезал 8px с боков (1904px) и полосы по высоте (800px)
        _ffmpegRunnerMock.Setup(r => r.DetectCropAsync(
            tempSourceFile,
            It.IsAny<double>(),
            It.IsAny<int>(),
            It.IsAny<double>(),
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()
        )).ReturnsAsync("1904:800:8:140");

        List<string>? capturedExtraArgs = null;
        _ffmpegRunnerMock.Setup(r => r.RunAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<List<string>>(),
            It.IsAny<bool>(), It.IsAny<double>(), It.IsAny<Action<ProgressInfo>>(), It.IsAny<CancellationToken>()
        )).Callback<string, string, List<string>, List<string>, bool, double, Action<ProgressInfo>, CancellationToken, ProcessExecutionContext>(
            (inP, outP, extArgs, inArgs, ovr, dur, prog, ct, processContext) =>capturedExtraArgs = extArgs
        ).ReturnsAsync(MockBuilders.ProcessSucceeded());

        var queueItem = new FileQueueItem(tempSourceFile);
        _script.FilesQueue.Add(queueItem);

        var settings = new Dictionary<string, object>
        {
            { "encoder", "x265" },
            { "autocrop_enabled", true },
            { "autocrop_tolerance", 16 }, // Порог 16px: разница 1920 - 1904 = 16px должна сброситься в 1920
            { "burn_in_subtitles", false }
        };

        try
        {
            // Act
            await _script.ExecuteSingleAsync(tempSourceFile, settings, tempOutputDir, (idx, total, status, pct, fps, bit) => { }, 0, 1);

            // Assert: ширина восстановлена до 1920, центрирование cropX = 0, высота 800 (полосы 140px)
            capturedExtraArgs.Should().NotBeNull();
            int vfIdx = capturedExtraArgs!.IndexOf("-vf");
            vfIdx.Should().BeGreaterThanOrEqualTo(0);
            capturedExtraArgs[vfIdx + 1].Should().Be("crop=1920:800:0:140");
            queueItem.CropBadgeText.Should().Be("1920x1080 ➔ 1920x800");
        }
        finally
        {
            if (File.Exists(tempSourceFile)) File.Delete(tempSourceFile);
        }
    }

    /// <summary>
    /// Проверяет отмену кадрирования, если в одной из контрольных точек обнаружен полнокадровый фрагмент (IMAX / Open Matte).
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_AutoCropImax_CancelsCropWhenFullScreenDetectedInAnyPoint()
    {
        // Arrange
        string tempSourceFile = Path.GetTempFileName();
        string tempOutputDir = Path.GetDirectoryName(tempSourceFile) ?? AppContext.BaseDirectory;

        var structure = new MediaStructure { FilePath = tempSourceFile, Duration = 60.0 };
        structure.Tracks.Add(new MediaTrack { TrackId = 0, TrackType = "video", Codec = "hevc", Resolution = "1920x1080", Name = "Video" });
        _mediaProbeServiceMock.Setup(p => p.ProbeAsync(tempSourceFile)).ReturnsAsync(structure);

        // В первой точке (15s) - 1920x800, во второй точке (30s) - IMAX полноэкранный 1920x1080
        _ffmpegRunnerMock.SetupSequence(r => r.DetectCropAsync(
            tempSourceFile,
            It.IsAny<double>(),
            It.IsAny<int>(),
            It.IsAny<double>(),
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()
        ))
        .ReturnsAsync("1920:800:0:140")
        .ReturnsAsync("1920:1080:0:0")
        .ReturnsAsync("1920:800:0:140");

        List<string>? capturedExtraArgs = null;
        _ffmpegRunnerMock.Setup(r => r.RunAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<List<string>>(),
            It.IsAny<bool>(), It.IsAny<double>(), It.IsAny<Action<ProgressInfo>>(), It.IsAny<CancellationToken>()
        )).Callback<string, string, List<string>, List<string>, bool, double, Action<ProgressInfo>, CancellationToken, ProcessExecutionContext>(
            (inP, outP, extArgs, inArgs, ovr, dur, prog, ct, processContext) =>capturedExtraArgs = extArgs
        ).ReturnsAsync(MockBuilders.ProcessSucceeded());

        var queueItem = new FileQueueItem(tempSourceFile);
        _script.FilesQueue.Add(queueItem);

        var settings = new Dictionary<string, object>
        {
            { "encoder", "x265" },
            { "autocrop_enabled", true },
            { "autocrop_probe_points", 3 },
            { "burn_in_subtitles", false }
        };

        try
        {
            // Act
            await _script.ExecuteSingleAsync(tempSourceFile, settings, tempOutputDir, (idx, total, status, pct, fps, bit) => { }, 0, 1);

            // Assert: кроп не должен применяться вовсе
            if (capturedExtraArgs != null && capturedExtraArgs.Contains("-vf"))
            {
                int vfIdx = capturedExtraArgs.IndexOf("-vf");
                capturedExtraArgs[vfIdx + 1].Should().NotContain("crop=");
            }
            queueItem.HasCropBadge.Should().BeFalse();
        }
        finally
        {
            if (File.Exists(tempSourceFile)) File.Delete(tempSourceFile);
        }
    }

    /// <summary>
    /// Проверяет, что неудачная подмена оригинала не сообщается как успех:
    /// результат частичный с кодом source-replacement-failed, исходник сохраняется.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_SourceReplacementFails_ReportsPartialAndKeepsSource()
    {
        // Arrange
        using var scope = new TempDirectoryScope();
        string source = scope.CreateFile("encode.mkv", "оригинал");
        SetupStructure(source);
        _ffmpegRunnerMock.Setup(r => r.RunAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<List<string>>(),
            It.IsAny<bool>(), It.IsAny<double>(), It.IsAny<Action<ProgressInfo>>(), It.IsAny<CancellationToken>()
        )).ReturnsAsync(MockBuilders.ProcessSucceeded());

        var settings = new Dictionary<string, object>
        {
            { "encoder", "x265" },
            { "overwrite_source", true }
        };

        // Act — FFmpeg «успешен», но файл результата не создан
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
    }

    /// <summary>
    /// Проверяет, что неудачная подмена не оставляет временных артефактов на диске.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_SourceReplacementFails_LeavesNoArtifacts()
    {
        // Arrange
        using var scope = new TempDirectoryScope();
        string source = scope.CreateFile("encode-artifacts.mkv", "оригинал");
        SetupStructure(source);
        _ffmpegRunnerMock.Setup(r => r.RunAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<List<string>>(),
            It.IsAny<bool>(), It.IsAny<double>(), It.IsAny<Action<ProgressInfo>>(), It.IsAny<CancellationToken>()
        )).ReturnsAsync(MockBuilders.ProcessSucceeded());

        var settings = new Dictionary<string, object>
        {
            { "encoder", "x265" },
            { "overwrite_source", true }
        };

        // Act
        await _script.ExecuteSingleAsync(
            source,
            settings,
            outputPath: null,
            progressCallback: (idx, total, status, pct, fps, bit) => { },
            fileIndex: 0,
            totalCount: 1);

        // Assert
        Directory.GetFiles(scope.RootPath).Should().ContainSingle("на диске остаётся только исходный файл");
    }

    /// <summary>
    /// Проверяет успешную подмену оригинала: результат оказывается по пути исходника.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_SourceReplacementSucceeds_ReturnsSucceeded()
    {
        // Arrange
        using var scope = new TempDirectoryScope();
        string source = scope.CreateFile("encode-good.mkv", "оригинал");
        SetupStructure(source);
        _ffmpegRunnerMock.Setup(r => r.RunAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<List<string>>(),
            It.IsAny<bool>(), It.IsAny<double>(), It.IsAny<Action<ProgressInfo>>(), It.IsAny<CancellationToken>()
        )).ReturnsAsync((string input, string output, List<string> extra, List<string> inputArgs, bool overwrite, double duration, Action<ProgressInfo> onProgress, CancellationToken token, ProcessExecutionContext? processContext) =>
        {
            File.WriteAllText(output, "перекодировано");
            return MockBuilders.ProcessSucceeded();
        });

        var settings = new Dictionary<string, object>
        {
            { "encoder", "x265" },
            { "overwrite_source", true }
        };

        // Act
        ExecutionResult result = await _script.ExecuteSingleAsync(
            source,
            settings,
            outputPath: null,
            progressCallback: (idx, total, status, pct, fps, bit) => { },
            fileIndex: 0,
            totalCount: 1);

        // Assert
        result.Status.Should().Be(ExecutionStatus.Succeeded);
        result.OutputFile.Should().Be(source);
        result.OutputExists.Should().BeTrue();
        File.ReadAllText(source).Should().Be("перекодировано");
    }

    private void SetupStructure(string path)
    {
        MediaStructure structure = new() { FilePath = path, Duration = 10.0 };
        structure.Tracks.Add(new MediaTrack { TrackId = 0, TrackType = "video", Codec = "h264", Name = "Video" });
        _mediaProbeServiceMock.Setup(p => p.ProbeAsync(path)).ReturnsAsync(structure);
    }

    /// <summary>
    /// Проверяет присутствие поля настройки sub_not_found_action в блоке и вкладке Субтитры.
    /// </summary>
    [TestMethod]
    public void SettingsSchema_ContainsSubNotFoundActionField()
    {
        var schema = _script.SettingsSchema;
        schema.Should().NotBeNull();

        var field = schema.Find(f => f.Key == "sub_not_found_action");
        field.Should().NotBeNull();
        field!.Group.Should().Be("Субтитры");
        field.Type.Should().Be(SettingType.Combo);
        field.DefaultValue.Should().Be("Пропускать хардсаб");
        field.Options.Should().Contain("Пропускать хардсаб");
        field.Options.Should().Contain("Спрашивать");
    }

    /// <summary>
    /// Проверяет, что при отсутствии совпадений по ключевым словам и режиме 'Пропускать хардсаб' сторонние субтитры не вшиваются.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_SubtitlesNotFound_SkipHardsubMode_DoesNotExtractOrBurnSubtitles()
    {
        // Arrange
        string tempSourceFile = Path.Combine(Path.GetTempPath(), "test_nosubs.mkv");
        File.WriteAllText(tempSourceFile, "dummy media");

        var structure = new MediaStructure { FilePath = tempSourceFile, Duration = 30.0 };
        structure.Tracks.Add(new MediaTrack { TrackId = 0, TrackType = "video", Codec = "h264", Name = "Video" });
        // Дорожка не содержит ключевого слова "Надписи"
        structure.Tracks.Add(new MediaTrack { TrackId = 1, TrackType = "subtitles", Codec = "ass", Name = "Full Dialogue", IsDefault = true });
        _mediaProbeServiceMock.Setup(p => p.ProbeAsync(tempSourceFile)).ReturnsAsync(structure);

        List<string>? capturedExtraArgs = null;
        _ffmpegRunnerMock.Setup(r => r.RunAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<List<string>>(),
            It.IsAny<bool>(), It.IsAny<double>(), It.IsAny<Action<ProgressInfo>>(), It.IsAny<CancellationToken>()
        )).Callback<string, string, List<string>, List<string>, bool, double, Action<ProgressInfo>, CancellationToken, ProcessExecutionContext>(
            (inP, outP, extArgs, inArgs, ovr, dur, prog, ct, ctx) => capturedExtraArgs = extArgs
        ).ReturnsAsync(MockBuilders.ProcessSucceeded());

        var settings = new Dictionary<string, object>
        {
            { "encoder", "NVENC" },
            { "burn_in_subtitles", true },
            { "sub_not_found_action", "Пропускать хардсаб" }
        };

        try
        {
            // Act
            await _script.ExecuteSingleAsync(tempSourceFile, settings, Path.GetTempPath(), (idx, total, status, pct, fps, bit) => { }, 0, 1);

            // Assert: субтитры не извлекались и не передавались в фильтр -vf
            _ffmpegRunnerMock.Verify(r => r.ExtractSubtitleAsync(tempSourceFile, It.IsAny<int>(), It.IsAny<string>(), true, It.IsAny<CancellationToken>(), It.IsAny<ProcessExecutionContext>()), Times.Never);
            capturedExtraArgs.Should().NotBeNull();
            capturedExtraArgs.Should().NotContain(s => s.Contains("subtitles="));
        }
        finally
        {
            if (File.Exists(tempSourceFile)) File.Delete(tempSourceFile);
        }
    }

    /// <summary>
    /// Проверяет, что при режиме 'Спрашивать' пользователю предлагается выбор дорожки через диалог, и выбранная дорожка вшивается.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_SubtitlesNotFound_AskMode_UserChoosesTrack_ExtractsAndBurnsChosenTrack()
    {
        // Arrange
        string tempSourceFile = Path.Combine(Path.GetTempPath(), "test_ask_subs.mkv");
        File.WriteAllText(tempSourceFile, "dummy media");

        var chosenTrack = new MediaTrack { TrackId = 2, TrackType = "subtitles", Codec = "ass", Name = "User Chosen Subs" };
        var structure = new MediaStructure { FilePath = tempSourceFile, Duration = 30.0 };
        structure.Tracks.Add(new MediaTrack { TrackId = 0, TrackType = "video", Codec = "h264", Name = "Video" });
        structure.Tracks.Add(new MediaTrack { TrackId = 1, TrackType = "subtitles", Codec = "ass", Name = "Full Dialogue" });
        structure.Tracks.Add(chosenTrack);
        _mediaProbeServiceMock.Setup(p => p.ProbeAsync(tempSourceFile)).ReturnsAsync(structure);

        var dialogMock = new Mock<IDialogService>();
        dialogMock.Setup(d => d.ChooseSubtitleTrackAsync(
                Path.GetFileName(tempSourceFile),
                It.IsAny<IReadOnlyList<MediaTrack>>()))
            .ReturnsAsync(chosenTrack);

        var encoders = new List<KTools_App.Encoders.IVideoEncoder>
        {
            new KTools_App.Encoders.NvencEncoder(),
            new KTools_App.Encoders.X265Encoder()
        };
        var hardwareCacheMock = new Mock<KTools_App.Encoders.IHardwareCapabilityCache>();
        hardwareCacheMock.Setup(c => c.IsNvencSupported).Returns(true);
        var registry = new KTools_App.Encoders.VideoEncoderRegistry(encoders, hardwareCacheMock.Object);

        var scriptWithDialog = new VideoEncodingScript(
            _logServiceMock.Object,
            _settingsManagerMock.Object,
            _pathManagerMock.Object,
            _ffmpegRunnerMock.Object,
            _mediaProbeServiceMock.Object,
            registry,
            dialogMock.Object);

        _ffmpegRunnerMock.Setup(r => r.ExtractSubtitleAsync(tempSourceFile, It.IsAny<int>(), It.IsAny<string>(), true, It.IsAny<CancellationToken>(), It.IsAny<ProcessExecutionContext>()))
            .Callback<string, int, string, bool, CancellationToken, ProcessExecutionContext>((inP, idx, outP, rel, ct, ctx) => File.WriteAllText(outP, "[Events]\nDialogue: ..."))
            .ReturnsAsync(MockBuilders.ProcessSucceeded());

        List<string>? capturedExtraArgs = null;
        _ffmpegRunnerMock.Setup(r => r.RunAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<List<string>>(),
            It.IsAny<bool>(), It.IsAny<double>(), It.IsAny<Action<ProgressInfo>>(), It.IsAny<CancellationToken>()
        )).Callback<string, string, List<string>, List<string>, bool, double, Action<ProgressInfo>, CancellationToken, ProcessExecutionContext>(
            (inP, outP, extArgs, inArgs, ovr, dur, prog, ct, ctx) => capturedExtraArgs = extArgs
        ).ReturnsAsync(MockBuilders.ProcessSucceeded());

        var settings = new Dictionary<string, object>
        {
            { "encoder", "NVENC" },
            { "burn_in_subtitles", true },
            { "sub_not_found_action", "Спрашивать" }
        };

        try
        {
            // Act
            await scriptWithDialog.ExecuteSingleAsync(tempSourceFile, settings, Path.GetTempPath(), (idx, total, status, pct, fps, bit) => { }, 0, 1);

            // Assert
            dialogMock.Verify(d => d.ChooseSubtitleTrackAsync(Path.GetFileName(tempSourceFile), It.IsAny<IReadOnlyList<MediaTrack>>()), Times.Once);
            _ffmpegRunnerMock.Verify(r => r.ExtractSubtitleAsync(tempSourceFile, 1, It.IsAny<string>(), true, It.IsAny<CancellationToken>(), It.IsAny<ProcessExecutionContext>()), Times.Once);
            capturedExtraArgs.Should().NotBeNull();
            capturedExtraArgs.Should().Contain(s => s.Contains("subtitles="));
        }
        finally
        {
            if (File.Exists(tempSourceFile)) File.Delete(tempSourceFile);
        }
    }

    /// <summary>
    /// Проверяет, что при режиме 'Спрашивать', если пользователь отменил диалог (null), хардсаб пропускается.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_SubtitlesNotFound_AskMode_UserSkips_DoesNotBurnSubtitles()
    {
        // Arrange
        string tempSourceFile = Path.Combine(Path.GetTempPath(), "test_ask_skip.mkv");
        File.WriteAllText(tempSourceFile, "dummy media");

        var structure = new MediaStructure { FilePath = tempSourceFile, Duration = 30.0 };
        structure.Tracks.Add(new MediaTrack { TrackId = 0, TrackType = "video", Codec = "h264", Name = "Video" });
        structure.Tracks.Add(new MediaTrack { TrackId = 1, TrackType = "subtitles", Codec = "ass", Name = "Full Dialogue" });
        _mediaProbeServiceMock.Setup(p => p.ProbeAsync(tempSourceFile)).ReturnsAsync(structure);

        var dialogMock = new Mock<IDialogService>();
        dialogMock.Setup(d => d.ChooseSubtitleTrackAsync(
                Path.GetFileName(tempSourceFile),
                It.IsAny<IReadOnlyList<MediaTrack>>()))
            .ReturnsAsync((MediaTrack?)null); // Пользователь нажал "Пропустить хардсаб"

        var encoders = new List<KTools_App.Encoders.IVideoEncoder>
        {
            new KTools_App.Encoders.NvencEncoder(),
            new KTools_App.Encoders.X265Encoder()
        };
        var hardwareCacheMock = new Mock<KTools_App.Encoders.IHardwareCapabilityCache>();
        hardwareCacheMock.Setup(c => c.IsNvencSupported).Returns(true);
        var registry = new KTools_App.Encoders.VideoEncoderRegistry(encoders, hardwareCacheMock.Object);

        var scriptWithDialog = new VideoEncodingScript(
            _logServiceMock.Object,
            _settingsManagerMock.Object,
            _pathManagerMock.Object,
            _ffmpegRunnerMock.Object,
            _mediaProbeServiceMock.Object,
            registry,
            dialogMock.Object);

        List<string>? capturedExtraArgs = null;
        _ffmpegRunnerMock.Setup(r => r.RunAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<List<string>>(),
            It.IsAny<bool>(), It.IsAny<double>(), It.IsAny<Action<ProgressInfo>>(), It.IsAny<CancellationToken>()
        )).Callback<string, string, List<string>, List<string>, bool, double, Action<ProgressInfo>, CancellationToken, ProcessExecutionContext>(
            (inP, outP, extArgs, inArgs, ovr, dur, prog, ct, ctx) => capturedExtraArgs = extArgs
        ).ReturnsAsync(MockBuilders.ProcessSucceeded());

        var settings = new Dictionary<string, object>
        {
            { "encoder", "NVENC" },
            { "burn_in_subtitles", true },
            { "sub_not_found_action", "Спрашивать" }
        };

        try
        {
            // Act
            await scriptWithDialog.ExecuteSingleAsync(tempSourceFile, settings, Path.GetTempPath(), (idx, total, status, pct, fps, bit) => { }, 0, 1);

            // Assert
            dialogMock.Verify(d => d.ChooseSubtitleTrackAsync(Path.GetFileName(tempSourceFile), It.IsAny<IReadOnlyList<MediaTrack>>()), Times.Once);
            _ffmpegRunnerMock.Verify(r => r.ExtractSubtitleAsync(tempSourceFile, It.IsAny<int>(), It.IsAny<string>(), true, It.IsAny<CancellationToken>(), It.IsAny<ProcessExecutionContext>()), Times.Never);
            capturedExtraArgs.Should().NotBeNull();
            capturedExtraArgs.Should().NotContain(s => s.Contains("subtitles="));
        }
        finally
        {
            if (File.Exists(tempSourceFile)) File.Delete(tempSourceFile);
        }
    }
}

