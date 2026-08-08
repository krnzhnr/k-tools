// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using Moq;
using KTools_App.Scripts;
using KTools_App.Core;
using KTools_App.Services.Contracts;
using KTools_App.Infrastructure;

namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты для скрипта сборки MKV MkvAssemblyScript.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class MkvAssemblyScriptTests
{
    private Mock<ILogService> _logServiceMock = null!;
    private Mock<ISettingsManager> _settingsManagerMock = null!;
    private Mock<IPathManager> _pathManagerMock = null!;
    private Mock<IMkvmergeRunner> _mkvmergeRunnerMock = null!;
    private Mock<IMediaProbeService> _mediaProbeServiceMock = null!;
    private Mock<IFFmpegRunner> _ffmpegRunnerMock = null!;
    private MkvAssemblyScript _script = null!;

    [TestInitialize]
    public void Setup()
    {
        _logServiceMock = new Mock<ILogService>();
        _settingsManagerMock = new Mock<ISettingsManager>();
        _pathManagerMock = new Mock<IPathManager>();
        _mkvmergeRunnerMock = new Mock<IMkvmergeRunner>();
        _mediaProbeServiceMock = new Mock<IMediaProbeService>();
        _ffmpegRunnerMock = new Mock<IFFmpegRunner>();

        _script = new MkvAssemblyScript(
            _logServiceMock.Object,
            _settingsManagerMock.Object,
            _pathManagerMock.Object,
            _mkvmergeRunnerMock.Object,
            _mediaProbeServiceMock.Object,
            _ffmpegRunnerMock.Object
        );
    }

    /// <summary>
    /// Проверяет базовые свойства сборщика MKV.
    /// </summary>
    [TestMethod]
    public void ScriptProperties_VerifyCorrectValues()
    {
        _script.Category.Should().Be("Контейнеры");
        _script.IconName.Should().Be(AppConstants.ScriptIcons.MkvAssembly);
    }

    /// <summary>
    /// Проверяет генерацию аргументов с правильным порядком дорожек при включении position_before_builtin.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_WithPositionBeforeBuiltin_GeneratesCorrectTrackOrder()
    {
        // Arrange
        // Используем реальную временную директорию ОС для теста, чтобы Directory.GetFiles работал корректно
        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        
        string videoPath = Path.Combine(tempDir, "video.mp4");
        string audioPath = Path.Combine(tempDir, "video.mka");
        
        // Создаем пустые файлы, чтобы Directory.GetFiles их нашел
        File.WriteAllText(videoPath, "");
        File.WriteAllText(audioPath, "");
        
        var settings = new Dictionary<string, object>
        {
            { "clean_tracks", false },
            { "position_before_builtin", true }
        };

        try
        {
            // Заполняем очередь
            _script.FilesQueue.Add(new FileQueueItem(videoPath));
            _script.FilesQueue.Add(new FileQueueItem(audioPath));

        var structure = new MediaStructure { FilePath = videoPath };
        structure.Tracks.Add(new MediaTrack { TrackId = 0, TrackType = "video" });
        structure.Tracks.Add(new MediaTrack { TrackId = 1, TrackType = "audio" }); // Встроенное аудио

        _mediaProbeServiceMock.Setup(m => m.ProbeAsync(videoPath))
            .ReturnsAsync(structure);

        _settingsManagerMock.Setup(s => s.GetSetting("General", "OverwriteExisting", false))
            .Returns(true);

        List<string>? capturedExtraArgs = null;
        _mkvmergeRunnerMock.Setup(m => m.RunAsync(
                It.IsAny<string>(),
                It.IsAny<List<MkvInputSource>>(),
                It.IsAny<string>(),
                It.IsAny<List<string>>(),
                It.IsAny<Action<double>>(),
                It.IsAny<System.Threading.CancellationToken>()
            ))
            .Callback<string, List<MkvInputSource>, string, List<string>, Action<double>, System.Threading.CancellationToken>(
                (outPath, inputs, title, extraArgs, onProgress, ct) => capturedExtraArgs = extraArgs)
            .ReturnsAsync(true);

        // Act
        var results = await _script.ExecuteSingleAsync(
            videoPath,
            settings,
            null,
            (fIdx, total, status, progress, fps, bitrate) => { },
            0,
            1
        );

        // Assert
        results.Should().NotBeNull();
        capturedExtraArgs.Should().NotBeNull();
        capturedExtraArgs.Should().Contain("--track-order");
        
        // Вход 0 - видеофайл, Вход 1 - внешний mka аудиофайл.
        // Ожидаемый порядок: видео (0:0), потом новое аудио (1:0), потом встроенное аудио (0:1)
        int orderIdx = capturedExtraArgs.IndexOf("--track-order");
        capturedExtraArgs[orderIdx + 1].Should().Be("0:0,1:0,0:1");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    /// <summary>
    /// Проверяет, что при выборе формата MP4 вызывается FFmpegRunner и отфильтровываются несовместимые файлы (FLAC/ASS).
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_Mp4Container_UsesFFmpegRunnerAndFiltersIncompatibleTracks()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), "MkvAssembly_Mp4Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            string videoPath = Path.Combine(tempDir, "video.mp4");
            string flacAudioPath = Path.Combine(tempDir, "video.flac");
            string assSubsPath = Path.Combine(tempDir, "video.ass");

            await File.WriteAllTextAsync(videoPath, "dummy video content");
            await File.WriteAllTextAsync(flacAudioPath, "dummy flac content");
            await File.WriteAllTextAsync(assSubsPath, "dummy ass content");

            var settings = new Dictionary<string, object>
            {
                { "output_container", "MP4" },
                { "clean_tracks", true }
            };

            _ffmpegRunnerMock.Setup(f => f.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<List<string>>(),
                    It.IsAny<List<string>>(),
                    It.IsAny<bool>(),
                    It.IsAny<double>(),
                    It.IsAny<Action<Infrastructure.ProgressInfo>>(),
                    It.IsAny<System.Threading.CancellationToken>()
                ))
                .ReturnsAsync(true);

            // Act
            var results = await _script.ExecuteSingleAsync(
                videoPath,
                settings,
                null,
                (fIdx, total, status, progress, fps, bitrate) => { },
                0,
                1
            );

            // Assert
            results.Should().NotBeNull();
            results.Should().Contain(r => r.Contains("FLAC") && r.Contains("пропущен"));
            results.Should().Contain(r => r.Contains("ASS/SSA") && r.Contains("пропущены"));
            results.Should().Contain(r => r.Contains("Собран контейнер MP4"));
            _ffmpegRunnerMock.Verify(f => f.RunAsync(
                videoPath,
                It.Is<string>(s => s.EndsWith(".mp4")),
                It.IsAny<List<string>>(),
                null,
                false,
                0.0,
                It.IsAny<Action<Infrastructure.ProgressInfo>>(),
                It.IsAny<System.Threading.CancellationToken>()
            ), Times.Once);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }
}
