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
    private string? _tempDir;

    [TestInitialize]
    public void Setup()
    {
        _logServiceMock = new Mock<ILogService>();
        _mkvmergeRunnerMock = new Mock<IMkvmergeRunner>();
        _ffmpegRunnerMock = new Mock<IFFmpegRunner>();
        _settingsManagerMock = new Mock<ISettingsManager>();
        _tempDir = null;
    }

    [TestCleanup]
    public void Cleanup()
    {
        // Удаляем уникальную временную папку теста, если она была создана.
        if (!string.IsNullOrEmpty(_tempDir) && Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, true);
            }
            catch (IOException)
            {
                // Папка занята другим процессом: не роняем результат теста при очистке.
            }
        }
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

    /// <summary>
    /// Проверяет, что при массовом параллельном зондировании число одновременно
    /// живых проб не превышает выделенный лимит, даже если общая параллельность выше.
    /// </summary>
    [TestMethod]
    public async Task ProbeAsync_ManyConcurrentProbes_LimitsLiveProbesToFour()
    {
        // Arrange: 16 файлов при разрешённой общей параллельности 16.
        _tempDir = Path.Combine(Path.GetTempPath(), "KToolsProbeLimit_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        const int fileCount = 16;
        string[] paths = new string[fileCount];
        for (int i = 0; i < fileCount; i++)
        {
            paths[i] = Path.Combine(_tempDir, $"clip{i:00}.mp4");
            File.WriteAllText(paths[i], "dummy media content");
        }

        _settingsManagerMock.SetupGet(s => s.EnableParallel).Returns(true);
        _settingsManagerMock.SetupGet(s => s.MaxParallelTasks).Returns(16);

        const string jsonWithDuration = """
        {
            "format": { "duration": "123.4" },
            "streams": [
                { "index": 0, "codec_name": "h264", "codec_type": "video", "width": 1920, "height": 1080, "avg_frame_rate": "25/1", "r_frame_rate": "25/1" }
            ]
        }
        """;

        int live = 0;
        int peak = 0;
        _ffmpegRunnerMock
            .Setup(f => f.GetVideoInfoAsync(It.IsAny<string>(), It.IsAny<ProcessExecutionContext>()))
            .Returns(async () =>
            {
                int current = Interlocked.Increment(ref live);
                int observed = Volatile.Read(ref peak);
                while (current > observed && Interlocked.CompareExchange(ref peak, current, observed) != observed)
                {
                    observed = Volatile.Read(ref peak);
                }

                try
                {
                    await Task.Delay(100);
                }
                finally
                {
                    Interlocked.Decrement(ref live);
                }

                return System.Text.Json.JsonDocument.Parse(jsonWithDuration);
            });

        var probeService = new MediaProbeService(
            _logServiceMock.Object,
            _mkvmergeRunnerMock.Object,
            _ffmpegRunnerMock.Object,
            _settingsManagerMock.Object);

        // Act
        var results = await Task.WhenAll(paths.Select(p => probeService.ProbeAsync(p)));

        // Assert
        results.Should().HaveCount(fileCount);
        results.Should().OnlyContain(r => r != null);
        peak.Should().BeLessThanOrEqualTo(4, "одновременно живых проб не должно быть больше выделенного лимита");
    }
}
