// -*- coding: utf-8 -*-
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using KTools_App.Infrastructure;

namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты для парсера вывода eac3to.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class Eac3toOutputParserTests
{
    [TestMethod]
    public void ParseLine_ProcessProgress_ReturnsPercentage()
    {
        // Arrange
        string line = "process: 73%";

        // Act
        var result = Eac3toOutputParser.ParseLine(line);

        // Assert
        result.Should().Be(73.0);
    }

    [TestMethod]
    public void ParseLine_AnalyzeProgress_ReturnsPercentage()
    {
        // Arrange
        string line = "analyze: 15%";

        // Act
        var result = Eac3toOutputParser.ParseLine(line);

        // Assert
        result.Should().Be(15.0);
    }

    [TestMethod]
    public void ParseLine_InvalidLine_ReturnsNull()
    {
        // Arrange
        string line = "eac3to v3.52, command line tool";

        // Act
        var result = Eac3toOutputParser.ParseLine(line);

        // Assert
        result.Should().BeNull();
    }
}
