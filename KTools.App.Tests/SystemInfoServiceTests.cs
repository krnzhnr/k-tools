// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Services.Contracts;
using KTools_App.Services.Implementations;
using KTools_App.Tests.TestHelpers;
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
        object sync = new();
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
                    lock (sync)
                    {
                        loggedEventIds.Add(eventId);
                        loggedMessages.Add(message);
                    }
                });

        var service = new SystemInfoService(logMock.Object);

        // Act
        Action act = () => service.LogSystemCharacteristics();

        // Assert
        act.Should().NotThrow();
        bool completed = SpinWait.SpinUntil(
            () =>
            {
                lock (sync)
                {
                    return loggedEventIds.Contains("system.snapshot.completed");
                }
            },
            TimeSpan.FromSeconds(15));
        completed.Should().BeTrue("расширенная часть дожимается фоном и завершается тем же маркером");
        List<string> messagesSnapshot;
        List<string> eventSnapshot;
        lock (sync)
        {
            messagesSnapshot = loggedMessages.ToList();
            eventSnapshot = loggedEventIds.ToList();
        }

        messagesSnapshot.Should().NotBeEmpty();
        eventSnapshot.Should().Contain("system.snapshot.completed",
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
        var loggedEventIds = new List<string>();
        object sync = new();
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
                (eventId, _, _, _, _, _, _, _) =>
                {
                    lock (sync)
                    {
                        loggedEventIds.Add(eventId);
                    }
                });
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
        bool completed = SpinWait.SpinUntil(
            () =>
            {
                lock (sync)
                {
                    return loggedEventIds.Contains("system.snapshot.completed");
                }
            },
            TimeSpan.FromSeconds(15));
        completed.Should().BeTrue("сбой отдельного раздела не отменяет фоновое завершение снимка");
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
        int detectorCalls = 0;
        diskMock.Setup(d => d.GetDriveTypeForPath(It.IsAny<string>()))
            .Callback(() => Interlocked.Increment(ref detectorCalls))
            .Returns(DriveMediaType.SSD);

        var service = new SystemInfoService(logMock.Object, diskMock.Object);

        // Act
        Action act = () => service.LogSystemCharacteristics();

        // Assert
        act.Should().NotThrow();
        bool called = SpinWait.SpinUntil(
            () => Volatile.Read(ref detectorCalls) > 0,
            TimeSpan.FromSeconds(15));
        called.Should().BeTrue("фоновая часть обязана опросить типы носителей через детектор");
        diskMock.Verify(
            d => d.GetDriveTypeForPath(It.IsAny<string>()),
            Times.AtLeastOnce,
            "опрос дисков не обходит DiskTypeDetectorService");
    }

    [TestMethod]
    public void LogEssential_DoesNotTouchDrives()
    {
        // Arrange
        var logMock = new Mock<ILogService>();
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
                (eventId, _, _, _, _, _, _, _) => loggedEventIds.Add(eventId));

        // Строгий мок: любое обращение к детектору роняет тест исключением мок-фреймворка
        var diskMock = new Mock<IDiskTypeDetectorService>(MockBehavior.Strict);

        var service = new SystemInfoService(logMock.Object, diskMock.Object);

        // Act — точечный замер дешёвой части щедрым бюджетом (триггер регресса, не бенчмарк)
        TimeSpan elapsed = PerfTiming.CompleteWithin(
            () => service.LogEssentialCharacteristics(),
            TimeSpan.FromSeconds(5),
            "LogEssentialCharacteristics");
        Console.WriteLine($"Быстрая часть снепшота заняла {elapsed.TotalMilliseconds:F0} мс при бюджете 5000 мс.");

        // Assert
        loggedEventIds.Should().Contain(
            e => e.StartsWith("system.os.", StringComparison.Ordinal),
            "быстрая часть пишет параметры ОС");
        loggedEventIds.Should().Contain(
            e => e.StartsWith("system.runtime.", StringComparison.Ordinal),
            "быстрая часть пишет параметры среды выполнения");
        loggedEventIds.Should().Contain(
            e => e.StartsWith("system.processor.", StringComparison.Ordinal),
            "быстрая часть пишет параметры процессора");
        loggedEventIds.Should().Contain(
            e => e.StartsWith("system.memory.", StringComparison.Ordinal),
            "быстрая часть пишет параметры памяти");
        loggedEventIds.Should().Contain(
            e => e.StartsWith("system.encoding.", StringComparison.Ordinal),
            "быстрая часть пишет региональные настройки");
        loggedEventIds.Should().NotContain(
            e => e.StartsWith("system.drive.", StringComparison.Ordinal),
            "быстрая часть не опрашивает диски");
        loggedEventIds.Should().NotContain(
            e => e.StartsWith("system.graphics.", StringComparison.Ordinal),
            "реестр видеоадаптеров читается только фоном");
        loggedEventIds.Should().NotContain(
            "system.snapshot.completed",
            "маркер полного снимка пишет только расширенная часть");
        diskMock.Verify(
            d => d.GetDriveTypeForPath(It.IsAny<string>()),
            Times.Never,
            "определитель типа носителя не вызывается из быстрой части");
    }

    [TestMethod]
    public async Task LogExtended_SleepingDrive_DoesNotThrow()
    {
        // Arrange — детектор падает на любом пути, имитируя спящий или недоступный накопитель
        var logMock = new Mock<ILogService>();
        var loggedEventIds = new List<string>();
        object sync = new();
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
                (eventId, _, _, _, _, _, _, _) =>
                {
                    lock (sync)
                    {
                        loggedEventIds.Add(eventId);
                    }
                });

        var diskMock = new Mock<IDiskTypeDetectorService>();
        diskMock.Setup(d => d.GetDriveTypeForPath(It.IsAny<string>()))
            .Throws(new IOException("Имитация недоступного спящего накопителя"));

        var service = new SystemInfoService(logMock.Object, diskMock.Object);

        // Act — сбой диска и детектора не роняет остальные; замер щедрым бюджетом (триггер регресса, не бенчмарк)
        Func<Task> act = () => service.LogExtendedCharacteristicsAsync(CancellationToken.None);
        TimeSpan elapsed = await PerfTiming.CompleteWithinAsync(
            () => act.Should().NotThrowAsync("сбой одного диска не должен ронять расширенный снимок"),
            TimeSpan.FromSeconds(30),
            "LogExtendedCharacteristicsAsync").ConfigureAwait(false);
        Console.WriteLine($"Расширенная часть снепшота заняла {elapsed.TotalMilliseconds:F0} мс при бюджете 30000 мс.");

        // Assert
        List<string> eventSnapshot;
        lock (sync)
        {
            eventSnapshot = loggedEventIds.ToList();
        }

        eventSnapshot.Should().Contain(
            "system.snapshot.completed",
            "расширенная часть завершается тем же маркером полного снимка");
        eventSnapshot.Should().Contain(
            e => e.StartsWith("system.drive.", StringComparison.Ordinal)
                || e.StartsWith("system.graphics.", StringComparison.Ordinal),
            "фоновая часть опрашивает диски и видеоадаптеры");
    }
}
