// -*- coding: utf-8 -*-
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using FluentAssertions;
using KTools_App.Infrastructure;
using KTools_App.Services.Contracts;

namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты для парсера вывода FFmpeg.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class FFmpegOutputParserTests
{
    private Mock<ILogService> _logServiceMock = null!;

    [TestInitialize]
    public void Setup()
    {
        _logServiceMock = new Mock<ILogService>();
    }

    /// <summary>
    /// Проверяет корректность парсинга стандартной строки прогресса FFmpeg.
    /// </summary>
    [TestMethod]
    public void ParseLine_ValidLine_ReturnsCorrectProgressInfo()
    {
        // Arrange
        string line = "frame=  123 fps= 30.2 q=-0.0 size=    1024kB time=00:01:20.50 bitrate= 100.2kbits/s speed= 2.50x";
        double totalDuration = 200.0; // 200 секунд

        // Act
        var result = FFmpegOutputParser.ParseLine(line, totalDuration, _logServiceMock.Object);

        // Assert
        result.Should().NotBeNull();
        result!.TimeSeconds.Should().Be(80.5);
        result.Percent.Should().BeApproximately(40.25, 0.01);
        result.Fps.Should().Be(30.2);
        result.Bitrate.Should().Be("100.2kbits/s");
        result.Speed.Should().Be(2.50);
        result.Eta.Should().Be("00:47"); // (200 - 80.5) / 2.5 = 47.8 сек -> 47 секунд
    }

    /// <summary>
    /// Проверяет, что при парсинге некорректной или мусорной строки возвращается null.
    /// </summary>
    [TestMethod]
    public void ParseLine_InvalidLine_ReturnsNull()
    {
        // Arrange
        string line = "some random log message from ffmpeg init";
        double totalDuration = 100.0;

        // Act
        var result = FFmpegOutputParser.ParseLine(line, totalDuration, _logServiceMock.Object);

        // Assert
        result.Should().BeNull();
    }

    /// <summary>
    /// Проверяет, что при пустой строке возвращается null.
    /// </summary>
    [TestMethod]
    public void ParseLine_EmptyLine_ReturnsNull()
    {
        // Arrange
        string line = "   ";
        double totalDuration = 100.0;

        // Act
        var result = FFmpegOutputParser.ParseLine(line, totalDuration, _logServiceMock.Object);

        // Assert
        result.Should().BeNull();
    }
}
