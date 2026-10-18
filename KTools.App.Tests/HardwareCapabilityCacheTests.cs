// -*- coding: utf-8 -*-
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using KTools_App.Encoders;
using KTools_App.Services.Contracts;

namespace KTools_App.Tests;

/// <summary>
/// Проверки отложенной NVENC-пробы с дисковым кэшем (PERF-05).
/// Раннер процессов подменяется моками: попадание в кэш обязано
/// обходиться вообще без обращений к нему.
/// </summary>
[TestClass]
public class HardwareCapabilityCacheTests
{
    private Mock<IFFmpegRunner> _runnerMock = null!;
    private Mock<ILogService> _logServiceMock = null!;
    private Mock<IPathManager> _pathManagerMock = null!;
    private string _tempDir = null!;
    private string _cacheFile = null!;
    private string _ffmpegVersion = null!;
    private string _driverVersion = null!;

    [TestInitialize]
    public void Setup()
    {
        _runnerMock = new Mock<IFFmpegRunner>();
        _logServiceMock = new Mock<ILogService>();
        _pathManagerMock = new Mock<IPathManager>();

        // Уникальная временная папка для дискового кэша.
        _tempDir = Path.Combine(Path.GetTempPath(), "KTools_NvencCacheTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _cacheFile = Path.Combine(_tempDir, "nvenc-capabilities.json");
        _pathManagerMock.Setup(pm => pm.GetSettingsDirectory()).Returns(_tempDir);

        _ffmpegVersion = "7.1";
        _driverVersion = "560.94";
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, true);
            }
            catch
            {
            }
        }
    }

    [TestMethod]
    public async Task InitializeAsync_ValidCache_NoProcessSpawn()
    {
        // Arrange: кэш совпадает с текущими версиями.
        WriteCacheFile(nvenc: true, temporalAq: true, ffmpeg: _ffmpegVersion, driver: _driverVersion);
        _runnerMock.Setup(r => r.CheckNvencSupportAsync()).ReturnsAsync(true);
        _runnerMock.Setup(r => r.CheckNvencTemporalAqSupportAsync()).ReturnsAsync(true);
        var cache = CreateCache();

        // Act.
        await cache.InitializeAsync();

        // Assert: результат из кэша, раннер не вызывался ни разу.
        cache.IsNvencSupported.Should().BeTrue();
        cache.IsNvencTemporalAqSupported.Should().BeTrue();
        _runnerMock.Verify(r => r.CheckNvencSupportAsync(), Times.Never);
        _runnerMock.Verify(r => r.CheckNvencTemporalAqSupportAsync(), Times.Never);
    }

    [TestMethod]
    public async Task InitializeAsync_DriverChanged_Reprobes()
    {
        // Arrange: в кэше старая версия драйвера.
        WriteCacheFile(nvenc: true, temporalAq: true, ffmpeg: _ffmpegVersion, driver: "535.98");
        _driverVersion = "560.94";
        _runnerMock.Setup(r => r.CheckNvencSupportAsync()).ReturnsAsync(true);
        _runnerMock.Setup(r => r.CheckNvencTemporalAqSupportAsync()).ReturnsAsync(false);
        var cache = CreateCache();

        // Act.
        await cache.InitializeAsync();

        // Assert: выполнен повторный опрос, кэш перезаписан новой версией драйвера.
        _runnerMock.Verify(r => r.CheckNvencSupportAsync(), Times.Once);
        _runnerMock.Verify(r => r.CheckNvencTemporalAqSupportAsync(), Times.Once);
        cache.IsNvencSupported.Should().BeTrue();
        cache.IsNvencTemporalAqSupported.Should().BeFalse();
        ReadCacheField("DriverVersion").Should().Be("560.94");
        ReadCacheField("FfmpegVersion").Should().Be(_ffmpegVersion);
    }

    [TestMethod]
    public async Task InitializeAsync_FfmpegChanged_Reprobes()
    {
        // Arrange: в кэше старая версия FFmpeg.
        WriteCacheFile(nvenc: false, temporalAq: false, ffmpeg: "6.0", driver: _driverVersion);
        _ffmpegVersion = "7.1";
        _runnerMock.Setup(r => r.CheckNvencSupportAsync()).ReturnsAsync(false);
        var cache = CreateCache();

        // Act.
        await cache.InitializeAsync();

        // Assert: выполнен повторный опрос без Temporal AQ, кэш перезаписан.
        _runnerMock.Verify(r => r.CheckNvencSupportAsync(), Times.Once);
        _runnerMock.Verify(r => r.CheckNvencTemporalAqSupportAsync(), Times.Never);
        cache.IsNvencSupported.Should().BeFalse();
        cache.IsNvencTemporalAqSupported.Should().BeFalse();
        ReadCacheField("FfmpegVersion").Should().Be("7.1");
    }

    [TestMethod]
    public async Task InitializeAsync_ProbeSuccess_WritesDiskCache()
    {
        // Arrange: кэша нет, опрос успешен.
        _runnerMock.Setup(r => r.CheckNvencSupportAsync()).ReturnsAsync(true);
        _runnerMock.Setup(r => r.CheckNvencTemporalAqSupportAsync()).ReturnsAsync(true);
        var cache = CreateCache();

        // Act.
        await cache.InitializeAsync();

        // Assert: результат записан на диск с текущими версиями.
        File.Exists(_cacheFile).Should().BeTrue();
        ReadCacheField("FfmpegVersion").Should().Be(_ffmpegVersion);
        ReadCacheField("DriverVersion").Should().Be(_driverVersion);
        cache.IsNvencSupported.Should().BeTrue();
        cache.IsNvencTemporalAqSupported.Should().BeTrue();
    }

    [TestMethod]
    public async Task InitializeAsync_RunnerThrows_FallsBackToCpuWithoutCache()
    {
        // Arrange: опрос бросает исключение (нет NVIDIA / битый раннер).
        _runnerMock.Setup(r => r.CheckNvencSupportAsync()).ThrowsAsync(new InvalidOperationException("проба невозможна"));
        var cache = CreateCache();

        // Act.
        await cache.InitializeAsync();

        // Assert: тихий переход на программный путь, кэш не записан.
        cache.IsNvencSupported.Should().BeFalse();
        cache.IsNvencTemporalAqSupported.Should().BeFalse();
        File.Exists(_cacheFile).Should().BeFalse();
    }

    [TestMethod]
    public async Task Invalidate_ClearsDiskCache()
    {
        // Arrange: кэш загружен из файла.
        WriteCacheFile(nvenc: true, temporalAq: false, ffmpeg: _ffmpegVersion, driver: _driverVersion);
        _runnerMock.Setup(r => r.CheckNvencSupportAsync()).ReturnsAsync(true);
        var cache = CreateCache();
        await cache.InitializeAsync();
        cache.IsNvencSupported.Should().BeTrue();

        // Act.
        cache.Invalidate();

        // Assert: память сброшена и файл кэша удалён.
        cache.IsNvencSupported.Should().BeFalse();
        cache.IsNvencTemporalAqSupported.Should().BeFalse();
        File.Exists(_cacheFile).Should().BeFalse();
    }

    private HardwareCapabilityCache CreateCache()
    {
        return new HardwareCapabilityCache(
            _runnerMock.Object,
            _logServiceMock.Object,
            _pathManagerMock.Object,
            ffmpegVersionProvider: () => _ffmpegVersion,
            driverVersionProvider: () => _driverVersion,
            cacheFilePath: _cacheFile);
    }

    private void WriteCacheFile(bool nvenc, bool temporalAq, string ffmpeg, string driver)
    {
        using var stream = File.Create(_cacheFile);
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteBoolean("Nvenc", nvenc);
        writer.WriteBoolean("TemporalAq", temporalAq);
        writer.WriteString("FfmpegVersion", ffmpeg);
        writer.WriteString("DriverVersion", driver);
        writer.WriteString("Timestamp", DateTimeOffset.UtcNow);
        writer.WriteEndObject();
    }

    private string ReadCacheField(string field)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(_cacheFile));
        return document.RootElement.GetProperty(field).GetString() ?? string.Empty;
    }
}
