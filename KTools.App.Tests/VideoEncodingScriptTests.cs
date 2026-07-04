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

        _script = new VideoEncodingScript(
            _logServiceMock.Object,
            _settingsManagerMock.Object,
            _pathManagerMock.Object,
            _ffmpegRunnerMock.Object,
            _mediaProbeServiceMock.Object
        );
    }

    /// <summary>
    /// Проверяет базовые свойства скрипта кодирования видео.
    /// </summary>
    [TestMethod]
    public void ScriptProperties_VerifyCorrectValues()
    {
        _script.Category.Should().Be("Видео");
        _script.IconName.Should().Be("video");
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
            capturedExtraArgs.Should().Contain("vbr_hq");
            capturedExtraArgs.Should().NotContain("lossless");
        }
        finally
        {
            if (File.Exists(tempSourceFile)) File.Delete(tempSourceFile);
        }
    }
}
