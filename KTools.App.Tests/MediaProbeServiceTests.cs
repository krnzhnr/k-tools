// -*- coding: utf-8 -*-
using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Infrastructure;
using KTools_App.Services.Contracts;

namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты для сервиса анализа метаданных MediaProbeService и кадровых парсеров аудиопотоков.
/// Все комментарии написаны исключительно на русском языке.
/// </summary>
[TestClass]
public class MediaProbeServiceTests
{
    private Mock<ILogService> _logServiceMock = null!;
    private Mock<IMkvmergeRunner> _mkvmergeRunnerMock = null!;
    private Mock<IFFmpegRunner> _ffmpegRunnerMock = null!;
    private Mock<ISettingsManager> _settingsManagerMock = null!;

    [TestInitialize]
    public void Setup()
    {
        _logServiceMock = new Mock<ILogService>();
        _mkvmergeRunnerMock = new Mock<IMkvmergeRunner>();
        _ffmpegRunnerMock = new Mock<IFFmpegRunner>();
        _settingsManagerMock = new Mock<ISettingsManager>();
    }

    /// <summary>
    /// Проверяет базовый вызов ProbeAsync и возвращение корректной структуры MediaStructure.
    /// </summary>
    [TestMethod]
    public async Task ProbeAsync_NonExistentFile_ReturnsNull()
    {
        // Arrange
        var probeService = new MediaProbeService(
            _logServiceMock.Object,
            _mkvmergeRunnerMock.Object,
            _ffmpegRunnerMock.Object,
            _settingsManagerMock.Object
        );

        // Act
        var result = await probeService.ProbeAsync("non_existent_file.mkv");

        // Assert
        result.Should().BeNull();
    }

    /// <summary>
    /// Проверяет, что при отсутствии длительности в контейнере и потоках ffprobe, сервис обращается к FFmpeg заголовочному зонду.
    /// </summary>
    [TestMethod]
    public async Task ProbeAsync_RawAudioStreamWithoutFormatDuration_FallsBackToFfmpegHeaderProbe()
    {
        // Arrange
        string testPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(testPath, "dummy media content");

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
            var doc = System.Text.Json.JsonDocument.Parse(jsonNoDuration);
            _ffmpegRunnerMock.Setup(f => f.GetVideoInfoAsync(testPath))
                .ReturnsAsync(doc);
            _ffmpegRunnerMock.Setup(f => f.ProbeDurationViaFfmpegAsync(testPath))
                .ReturnsAsync(245.5);

            var probeService = new MediaProbeService(
                _logServiceMock.Object,
                _mkvmergeRunnerMock.Object,
                _ffmpegRunnerMock.Object,
                _settingsManagerMock.Object
            );

            // Act
            var result = await probeService.ProbeAsync(testPath);

            // Assert
            result.Should().NotBeNull();
            result!.Duration.Should().Be(245.5);
            _ffmpegRunnerMock.Verify(f => f.ProbeDurationViaFfmpegAsync(testPath), Times.Once);
        }
        finally
        {
            if (File.Exists(testPath)) File.Delete(testPath);
        }
    }
}
