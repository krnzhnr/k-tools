// -*- coding: utf-8 -*-
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using FluentAssertions;
using KTools_App.Infrastructure;
using KTools_App.Services.Contracts;

namespace KTools_App.Tests;

/// <summary>
/// Тесты неанглийской локали для парсеров прогресса (issue 113).
/// Проверяют запятую в дробной части.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class OutputParserLocale113Tests
{
    private Mock<ILogService> _logServiceMock = null!;

    [TestInitialize]
    public void Setup()
    {
        _logServiceMock = new Mock<ILogService>();
    }

    [TestMethod]
    public void ParseLine_CommaDecimalProgress_ReturnsFractionalPercentage()
    {
        // Arrange — неанглийская локаль mkvmerge с запятой
        string line = "Progress: 42,5%";

        // Act
        var result = MkvmergeOutputParser.ParseLine(line);

        // Assert
        result.Should().NotBeNull();
        result!.Value.Should().BeApproximately(42.5, 0.001);
    }

    [TestMethod]
    public void ParseLine_DotDecimalProgress_ReturnsFractionalPercentage()
    {
        // Arrange — английская локаль для регрессии
        string line = "Progress: 42.5%";

        // Act
        var result = MkvmergeOutputParser.ParseLine(line);

        // Assert
        result.Should().NotBeNull();
        result!.Value.Should().BeApproximately(42.5, 0.001);
    }

    [TestMethod]
    public void ParseLine_CommaDecimalProcess_ReturnsFractionalPercentage()
    {
        // Arrange — неанглийская локаль eac3to с запятой
        string line = "process: 73,5%";

        // Act
        var result = Eac3toOutputParser.ParseLine(line);

        // Assert
        result.Should().NotBeNull();
        result!.Value.Should().BeApproximately(73.5, 0.001);
    }

    [TestMethod]
    public void ParseLine_CommaDecimalAnalyze_ReturnsFractionalPercentage()
    {
        // Arrange — фаза analyze с запятой
        string line = "analyze: 15,2%";

        // Act
        var result = Eac3toOutputParser.ParseLine(line);

        // Assert
        result.Should().NotBeNull();
        result!.Value.Should().BeApproximately(15.2, 0.001);
    }

    [TestMethod]
    public void ParseLine_CommaDecimalFpsAndSpeed_ReturnsNormalizedValues()
    {
        // Arrange — локализованная строка ffmpeg с запятыми во всех полях
        string line = "frame=  123 fps= 30,2 q=-0.0 size=    1024kB time=00:01:20,50 bitrate= 100,2kbits/s speed= 2,50x";
        double totalDuration = 200.0;

        // Act
        var result = FFmpegOutputParser.ParseLine(line, totalDuration, _logServiceMock.Object);

        // Assert
        result.Should().NotBeNull();
        result!.TimeSeconds.Should().BeApproximately(80.5, 0.001);
        result.Percent.Should().BeApproximately(40.25, 0.01);
        result.Fps.Should().NotBeNull();
        result.Fps!.Value.Should().BeApproximately(30.2, 0.001);
        result.Speed.Should().NotBeNull();
        result.Speed!.Value.Should().BeApproximately(2.5, 0.001);
    }

    [TestMethod]
    public void ParseHeaderDuration_CommaDecimalSeparator_ReturnsTotalSeconds()
    {
        // Arrange — заголовок длительности с запятой
        string line = "  Duration: 00:02:15,50, start: 0.000000, bitrate: 320 kb/s";

        // Act
        double duration = FFmpegOutputParser.ParseHeaderDuration(line, _logServiceMock.Object);

        // Assert
        duration.Should().BeApproximately(135.5, 0.001);
    }
}
