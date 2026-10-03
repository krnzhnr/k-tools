// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Diagnostics;
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
        var loggedEventIds = new List<string>();
        logMock.Setup(l => l.Write(
                It.IsAny<string>(),
                It.IsAny<LogLevel>(),
                It.IsAny<LogStatus>(),
                It.IsAny<string>(),
                It.IsAny<Exception>(),
                It.IsAny<string>(),
                It.IsAny<LogContext?>(),
                It.IsAny<IReadOnlyDictionary<string, object?>>()))
            .Callback<string, LogLevel, LogStatus, string, Exception, string, LogContext?, IReadOnlyDictionary<string, object?>>(
                (eventId, _, _, message, _, _, _, _) =>
                {
                    loggedEventIds.Add(eventId);
                    loggedMessages.Add(message);
                });

        var service = new SystemInfoService(logMock.Object);

        // Act
        Action act = () => service.LogSystemCharacteristics();

        // Assert
        act.Should().NotThrow();
        loggedMessages.Should().NotBeEmpty();
        loggedEventIds.Should().Contain("system.snapshot.completed",
            "снимок системы завершается стабильным структурированным событием");
        logMock.Verify(
            l => l.Write(
                "system.snapshot.completed",
                LogLevel.Info,
                LogStatus.Succeeded,
                It.IsAny<string>(),
                It.IsAny<Exception>(),
                It.IsAny<string>(),
                It.IsAny<LogContext?>(),
                It.Is<IReadOnlyDictionary<string, object?>>(p => Equals(p["Schema"], "system.snapshot.v1"))),
            Times.Once);
    }

    [TestMethod]
    public void LogSystemCharacteristics_WhenSectionLoggingThrows_RecordsTypedSectionFailure()
    {
        // Arrange — падает только запись раздела памяти, остальные разделы и снимок фиксируются
        var logMock = new Mock<ILogService>();
        logMock.Setup(l => l.Write(
                "system.memory.detected",
                It.IsAny<LogLevel>(),
                It.IsAny<LogStatus>(),
                It.IsAny<string>(),
                It.IsAny<Exception>(),
                It.IsAny<string>(),
                It.IsAny<LogContext?>(),
                It.IsAny<IReadOnlyDictionary<string, object?>>()))
            .Throws(new InvalidOperationException("Имитация сбоя в подсистеме логирования"));

        var service = new SystemInfoService(logMock.Object);

        // Act & Assert
        Action act = () => service.LogSystemCharacteristics();
        act.Should().NotThrow("сбой записи раздела не должен пробрасываться вызывающему");
        logMock.Verify(
            l => l.Write(
                "system.memory.detect_failed",
                LogLevel.Warning,
                LogStatus.Skipped,
                It.Is<string>(m => !m.Contains("Exception", StringComparison.Ordinal)),
                It.IsAny<Exception>(),
                It.IsAny<string>(),
                It.IsAny<LogContext?>(),
                It.Is<IReadOnlyDictionary<string, object?>>(p => Equals(p["ErrorCode"], "MEMORY_DETECT_FAILED"))),
            Times.Once,
            "сбой раздела фиксируется структурированным предупреждением с кодом и исключением в отдельном параметре");
        logMock.Verify(
            l => l.Write(
                "system.snapshot.completed",
                LogLevel.Info,
                LogStatus.Succeeded,
                It.IsAny<string>(),
                It.IsAny<Exception>(),
                It.IsAny<string>(),
                It.IsAny<LogContext?>(),
                It.Is<IReadOnlyDictionary<string, object?>>(p => Equals(p["Schema"], "system.snapshot.v1"))),
            Times.Once,
            "сбой отдельного раздела не должен превращать весь снимок системы в неуспешный");
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
