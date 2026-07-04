// -*- coding: utf-8 -*-
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using KTools_App.Infrastructure;

namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты для парсера вывода mkvmerge.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class MkvmergeOutputParserTests
{
    [TestMethod]
    public void ParseLine_ValidProgress_ReturnsPercentage()
    {
        // Arrange
        string line = "Progress: 45%";

        // Act
        var result = MkvmergeOutputParser.ParseLine(line);

        // Assert
        result.Should().Be(45.0);
    }

    [TestMethod]
    public void ParseLine_InvalidLine_ReturnsNull()
    {
        // Arrange
        string line = "mkvmerge v88.0.0 ('The Great Gig In The Sky') 64-bit";

        // Act
        var result = MkvmergeOutputParser.ParseLine(line);

        // Assert
        result.Should().BeNull();
    }
}
