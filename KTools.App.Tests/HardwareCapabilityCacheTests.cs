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
    private string _deviceFingerprint = null!;

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
        _deviceFingerprint = "NVIDIA GeForce RTX 4070|pci\\ven_10de&dev_2684";
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
            deviceFingerprintProvider: () => _deviceFingerprint,
            cacheFilePath: _cacheFile);
    }

    private void WriteCacheFile(bool nvenc, bool temporalAq, string ffmpeg, string driver, string? device = null, bool av1 = false)
    {
        using var stream = File.Create(_cacheFile);
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteBoolean("Nvenc", nvenc);
        writer.WriteBoolean("TemporalAq", temporalAq);
        writer.WriteBoolean("Av1", av1);
        writer.WriteString("FfmpegVersion", ffmpeg);
        writer.WriteString("DriverVersion", driver);
        writer.WriteString("DeviceFingerprint", device ?? _deviceFingerprint);
        writer.WriteString("Timestamp", DateTimeOffset.UtcNow);
        writer.WriteEndObject();
    }

    /// <summary>
    /// Кэш старого формата: без отпечатка оборудования и флага AV1.
    /// Такой файл обязан считаться недействительным и вызывать повторный опрос.
    /// </summary>
    private void WriteLegacyCacheFile(bool nvenc, bool temporalAq, string ffmpeg, string driver)
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

    [TestMethod]
    public async Task InitializeAsync_GpuSwappedSameDriver_Reprobes()
    {
        // Arrange: драйвер и FFmpeg совпали, но видеокарта заменена (другое имя/PCI-ID).
        WriteCacheFile(nvenc: true, temporalAq: true, ffmpeg: _ffmpegVersion, driver: _driverVersion, device: "NVIDIA GeForce RTX 3090|pci\\ven_10de&dev_2204");
        _runnerMock.Setup(r => r.CheckNvencSupportAsync()).ReturnsAsync(true);
        _runnerMock.Setup(r => r.CheckNvencTemporalAqSupportAsync()).ReturnsAsync(true);
        _runnerMock.Setup(r => r.CheckNvencAv1SupportAsync()).ReturnsAsync(true);
        var cache = CreateCache();

        // Act.
        await cache.InitializeAsync();

        // Assert: ложного попадания нет, опрос выполнен заново и кэш перезаписан новым отпечатком.
        _runnerMock.Verify(r => r.CheckNvencSupportAsync(), Times.Once);
        cache.IsNvencSupported.Should().BeTrue();
        cache.IsNvencAv1Supported.Should().BeTrue();
        ReadCacheField("DeviceFingerprint").Should().Be(_deviceFingerprint);
    }

    [TestMethod]
    public async Task InitializeAsync_LegacyCacheWithoutDeviceFingerprint_Reprobes()
    {
        // Arrange: кэш старого формата без отпечатка оборудования и флага AV1.
        WriteLegacyCacheFile(nvenc: true, temporalAq: true, ffmpeg: _ffmpegVersion, driver: _driverVersion);
        _runnerMock.Setup(r => r.CheckNvencSupportAsync()).ReturnsAsync(true);
        _runnerMock.Setup(r => r.CheckNvencTemporalAqSupportAsync()).ReturnsAsync(false);
        _runnerMock.Setup(r => r.CheckNvencAv1SupportAsync()).ReturnsAsync(false);
        var cache = CreateCache();

        // Act.
        await cache.InitializeAsync();

        // Assert: старый формат недействителен, опрос выполнен заново в новом формате.
        _runnerMock.Verify(r => r.CheckNvencSupportAsync(), Times.Once);
        ReadCacheField("DeviceFingerprint").Should().Be(_deviceFingerprint);
    }

    [TestMethod]
    public async Task InitializeAsync_Av1Supported_CachesFlagAndHitsCache()
    {
        // Arrange: кэша нет, видеокарта с AV1-блоком.
        _runnerMock.Setup(r => r.CheckNvencSupportAsync()).ReturnsAsync(true);
        _runnerMock.Setup(r => r.CheckNvencTemporalAqSupportAsync()).ReturnsAsync(true);
        _runnerMock.Setup(r => r.CheckNvencAv1SupportAsync()).ReturnsAsync(true);
        var cache = CreateCache();

        // Act.
        await cache.InitializeAsync();

        // Assert: флаг AV1 определён и записан на диск.
        cache.IsNvencAv1Supported.Should().BeTrue();
        ReadCacheBool("Av1").Should().BeTrue();

        // Act: повторная инициализация обязана брать всё из кэша без запуска процессов.
        var cached = CreateCache();
        await cached.InitializeAsync();

        // Assert.
        cached.IsNvencAv1Supported.Should().BeTrue();
        _runnerMock.Verify(r => r.CheckNvencSupportAsync(), Times.Once);
        _runnerMock.Verify(r => r.CheckNvencAv1SupportAsync(), Times.Once);
    }

    [TestMethod]
    public async Task InitializeAsync_Av1BlockMissing_ReportsFalse()
    {
        // Arrange: NVENC есть, но AV1-блока нет (старая видеокарта).
        _runnerMock.Setup(r => r.CheckNvencSupportAsync()).ReturnsAsync(true);
        _runnerMock.Setup(r => r.CheckNvencTemporalAqSupportAsync()).ReturnsAsync(true);
        _runnerMock.Setup(r => r.CheckNvencAv1SupportAsync()).ReturnsAsync(false);
        var cache = CreateCache();

        // Act.
        await cache.InitializeAsync();

        // Assert: AV1 недоступен и это зафиксировано в кэше.
        cache.IsNvencSupported.Should().BeTrue();
        cache.IsNvencAv1Supported.Should().BeFalse();
        ReadCacheBool("Av1").Should().BeFalse();
    }

    [TestMethod]
    public async Task InitializeAsync_NvencMissing_SkipsAv1Probe()
    {
        // Arrange: NVENC отсутствует.
        _runnerMock.Setup(r => r.CheckNvencSupportAsync()).ReturnsAsync(false);
        var cache = CreateCache();

        // Act.
        await cache.InitializeAsync();

        // Assert: проба AV1 не запускается, флаг сброшен.
        _runnerMock.Verify(r => r.CheckNvencAv1SupportAsync(), Times.Never);
        cache.IsNvencAv1Supported.Should().BeFalse();
    }

    [TestMethod]
    public async Task Invalidate_ClearsAv1Flag()
    {
        // Arrange: опрос определил поддержку AV1.
        _runnerMock.Setup(r => r.CheckNvencSupportAsync()).ReturnsAsync(true);
        _runnerMock.Setup(r => r.CheckNvencTemporalAqSupportAsync()).ReturnsAsync(true);
        _runnerMock.Setup(r => r.CheckNvencAv1SupportAsync()).ReturnsAsync(true);
        var cache = CreateCache();
        await cache.InitializeAsync();
        cache.IsNvencAv1Supported.Should().BeTrue();

        // Act.
        cache.Invalidate();

        // Assert: флаг AV1 сброшен вместе с остальным кэшем.
        cache.IsNvencAv1Supported.Should().BeFalse();
        File.Exists(_cacheFile).Should().BeFalse();
    }

    private string ReadCacheField(string field)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(_cacheFile));
        return document.RootElement.GetProperty(field).GetString() ?? string.Empty;
    }

    private bool ReadCacheBool(string field)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(_cacheFile));
        return document.RootElement.GetProperty(field).GetBoolean();
    }
}
