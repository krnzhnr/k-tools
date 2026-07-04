// -*- coding: utf-8 -*-
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using KTools_App.Infrastructure;

namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты для парсера вывода QAAC.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class QaacOutputParserTests
{
    /// <summary>
    /// Проверяет корректность парсинга нового формата вывода QAAC.
    /// </summary>
    [TestMethod]
    public void ParseLine_NewFormat_ReturnsCorrectProgressInfo()
    {
        // Arrange
        string line = "[50.0%] 0:02.500/0:05.000 (10.0x), ETA 0:00.250";

        // Act
        var result = QaacOutputParser.ParseLine(line, 5.0);

        // Assert
        result.Should().NotBeNull();
        result!.Percent.Should().Be(50.0);
        result.TimeSeconds.Should().Be(2.5);
        result.Speed.Should().Be(10.0);
        result.Eta.Should().Be("0:00.250");
    }

    /// <summary>
    /// Проверяет корректность парсинга старого/альтернативного формата с процентами.
    /// </summary>
    [TestMethod]
    public void ParseLine_PercentFormat_ReturnsCorrectProgressInfo()
    {
        // Arrange
        string line = " 12.5% [14.2x]";

        // Act
        var result = QaacOutputParser.ParseLine(line);

        // Assert
        result.Should().NotBeNull();
        result!.Percent.Should().Be(12.5);
        result.Speed.Should().Be(14.2);
    }

    /// <summary>
    /// Проверяет стандартный временной вывод qaac.
    /// </summary>
    [TestMethod]
    public void ParseLine_TimeFormat_ReturnsCorrectProgressInfo()
    {
        // Arrange
        string line = " 0:10.000 (1.0x)";
        double totalDuration = 20.0;

        // Act
        var result = QaacOutputParser.ParseLine(line, totalDuration);

        // Assert
        result.Should().NotBeNull();
        result!.TimeSeconds.Should().Be(10.0);
        result.Percent.Should().Be(50.0);
        result.Speed.Should().Be(1.0);
        result.Eta.Should().Be("00:10"); // (20.0 - 10.0) / 1.0 = 10.0 сек
    }
}
