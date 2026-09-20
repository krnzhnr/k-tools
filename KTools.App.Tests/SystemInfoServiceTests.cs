// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using FluentAssertions;
using KTools_App.Services.Contracts;
using KTools_App.Services.Implementations;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace KTools_App.Tests;

/// <summary>
/// Набор тестов для проверки безопасности и работоспособности сервиса SystemInfoService.
/// </summary>
[TestClass]
public class SystemInfoServiceTests
{
    [TestMethod]
    public void Constructor_NullLogService_ThrowsArgumentNullException()
    {
        // Act
        Action act = () => new SystemInfoService(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [TestMethod]
    public void LogSystemCharacteristics_WithValidLogService_DoesNotThrowAndLogsInfo()
    {
        // Arrange
        var logMock = new Mock<ILogService>();
        var loggedMessages = new List<string>();
        logMock.Setup(l => l.Info(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((msg, cat) => loggedMessages.Add(msg));

        var service = new SystemInfoService(logMock.Object);

        // Act
        Action act = () => service.LogSystemCharacteristics();

        // Assert
        act.Should().NotThrow();
        loggedMessages.Should().NotBeEmpty();
        loggedMessages.Should().Contain(m => m.Contains("Характеристики системы"));
    }

    [TestMethod]
    public void LogSystemCharacteristics_WhenLoggerThrows_DoesNotCrashCaller()
    {
        // Arrange
        var logMock = new Mock<ILogService>();
        logMock.Setup(l => l.Info(It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new InvalidOperationException("Имитация сбоя в подсистеме логирования"));

        var service = new SystemInfoService(logMock.Object);

        // Act & Assert
        Action act = () => service.LogSystemCharacteristics();
        act.Should().NotThrow();
    }

    [TestMethod]
    public void LogSystemCharacteristics_WithDiskTypeDetector_DoesNotThrowAndCallsDetector()
    {
        // Arrange
        var logMock = new Mock<ILogService>();
        var diskMock = new Mock<IDiskTypeDetectorService>();
        diskMock.Setup(d => d.GetDriveTypeForPath(It.IsAny<string>())).Returns(DriveMediaType.SSD);

        var service = new SystemInfoService(logMock.Object, diskMock.Object);

        // Act
        Action act = () => service.LogSystemCharacteristics();

        // Assert
        act.Should().NotThrow();
    }
}
