// -*- coding: utf-8 -*-
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using KTools_App.Core;
using KTools_App.Encoders;
using KTools_App.Scripts;
using KTools_App.Services.Contracts;

namespace KTools_App.Tests;

/// <summary>
/// Гонка схемы при холодном старте из контекстного меню (ишью #56):
/// схема строится до завершения фоновой пробы NVENC и содержит только CPU,
/// после пробы набор ключей меняется и интерфейс обязан пересобраться.
/// Все комментарии выполнены на русском языке.
/// </summary>
[TestClass]
public class VideoEncodingSchemaRaceTests
{
    private Mock<ILogService> _logServiceMock = null!;
    private Mock<ISettingsManager> _settingsManagerMock = null!;
    private Mock<IPathManager> _pathManagerMock = null!;
    private Mock<IFFmpegRunner> _ffmpegRunnerMock = null!;
    private Mock<IMediaProbeService> _mediaProbeServiceMock = null!;
    private Mock<IHardwareCapabilityCache> _hardwareCacheMock = null!;

    [TestInitialize]
    public void Setup()
    {
        _logServiceMock = new Mock<ILogService>();
        _settingsManagerMock = new Mock<ISettingsManager>();
        _pathManagerMock = new Mock<IPathManager>();
        _ffmpegRunnerMock = new Mock<IFFmpegRunner>();
        _mediaProbeServiceMock = new Mock<IMediaProbeService>();
        _hardwareCacheMock = new Mock<IHardwareCapabilityCache>();
    }

    private VideoEncodingScript CreateScript()
    {
        var encoders = new List<IVideoEncoder>
        {
            new NvencEncoder(),
            new CpuEncoder()
        };
        var registry = new VideoEncoderRegistry(encoders, _hardwareCacheMock.Object);
        return new VideoEncodingScript(
            _logServiceMock.Object,
            _settingsManagerMock.Object,
            _pathManagerMock.Object,
            _ffmpegRunnerMock.Object,
            _mediaProbeServiceMock.Object,
            registry);
    }

    [TestMethod]
    public void GetSettingsSchema_BeforeNvencInit_ContainsOnlyCpu()
    {
        // Arrange: проба ещё не завершена, NVENC недоступен.
        _hardwareCacheMock.Setup(c => c.IsNvencSupported).Returns(false);
        var script = CreateScript();

        // Act.
        var schema = script.GetSettingsSchema(null);

        // Assert: только CPU, полей NVENC нет.
        var encoderField = schema.FirstOrDefault(f => f.Key == "encoder");
        encoderField.Should().NotBeNull();
        encoderField!.Options.Should().ContainSingle().Which.Should().Be("CPU");
        schema.Should().NotContain(f => f.Key.StartsWith("nvenc_") || f.Key.StartsWith("v_bitrate"));
    }

    [TestMethod]
    public void GetSettingsSchema_AfterNvencInit_ContainsNvencKeys()
    {
        // Arrange: фоновая проба завершена, NVENC доступен.
        _hardwareCacheMock.Setup(c => c.IsNvencSupported).Returns(true);
        var script = CreateScript();

        // Act.
        var schema = script.GetSettingsSchema(null);

        // Assert: оба энкодера и поля NVENC на месте.
        var encoderField = schema.FirstOrDefault(f => f.Key == "encoder");
        encoderField.Should().NotBeNull();
        encoderField!.Options.Should().Contain("CPU");
        encoderField.Options.Should().Contain("NVENC");
        schema.Should().Contain(f => f.Key == "nvenc_rc");
    }

    [TestMethod]
    public void Schemas_BeforeAndAfterNvencInit_HaveDifferentKeys()
    {
        // Arrange: ранняя схема построена до пробы.
        _hardwareCacheMock.Setup(c => c.IsNvencSupported).Returns(false);
        var earlyScript = CreateScript();
        var earlySchema = earlyScript.GetFullSettingsSchema(null);
        var earlyKeys = SettingsSchemaComparer.GetFlatKeys(earlySchema);

        // Act: проба завершена, схема перестроена.
        _hardwareCacheMock.Setup(c => c.IsNvencSupported).Returns(true);
        var lateScript = CreateScript();
        var lateSchema = lateScript.GetFullSettingsSchema(null);

        // Assert: набор ключей изменился — проход видимости обязан пересобрать дерево.
        SettingsSchemaComparer.HasSameKeys(lateSchema, earlyKeys).Should().BeFalse(
            "иначе интерфейс останется с пустой вкладкой битрейта до ручного переключения");
    }
}
