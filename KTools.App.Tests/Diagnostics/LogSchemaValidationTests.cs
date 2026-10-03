// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Infrastructure;
using KTools_App.Services.Contracts;
using KTools_App.Services.Implementations;
using KTools_App.Tests.TestHelpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace KTools_App.Tests.Diagnostics;

/// <summary>
/// Проверки условной схемы журнала: тестовый двойник отклоняет те же события,
/// что и реальный сервис, а проверки Moq видят обе формы вызова Write.
/// </summary>
[TestClass]
public sealed class LogSchemaValidationTests
{
    [TestMethod]
    public void RealLogService_ReservedItemEventWithoutContext_RejectsEvent()
    {
        // Arrange
        using var scope = new TempDirectoryScope();
        LogServiceOptions options = new()
        {
            CustomLogDirectory = scope.GetFullPath("logs"),
            EmergencyDirectory = scope.GetFullPath("emergency"),
            FlushIntervalMilliseconds = 60000
        };
        using LogService log = new(options);

        // Act
        log.Write(
            "exec.item.failed",
            LogLevel.Error,
            LogStatus.Failed,
            "Элемент очереди не обработан");

        // Assert
        log.Flush(TimeSpan.FromSeconds(10)).Should().BeTrue();
        log.RejectedEventCount.Should().Be(1, "событие зарезервированной схемы без контекста обязано быть отклонено");
        log.ReadRecentEvents(10)
            .Should().ContainSingle()
            .Which.EventId.Should().Be(LogEventMarkerNames.RejectedEvent);
    }

    [TestMethod]
    public void RealLogService_ReservedItemEventWithContext_KeepsEvent()
    {
        // Arrange
        using var scope = new TempDirectoryScope();
        LogServiceOptions options = new()
        {
            CustomLogDirectory = scope.GetFullPath("logs"),
            EmergencyDirectory = scope.GetFullPath("emergency"),
            FlushIntervalMilliseconds = 60000
        };
        using LogService log = new(options);
        LogContext context = LogContext.Empty
            .WithOperation("operation-schema-1")
            .WithItem("item-schema-1");

        // Act
        log.Write(
            "exec.item.failed",
            LogLevel.Error,
            LogStatus.Failed,
            "Элемент очереди не обработан",
            context: context);

        // Assert
        log.Flush(TimeSpan.FromSeconds(10)).Should().BeTrue();
        log.RejectedEventCount.Should().Be(0);
        LogEvent persisted = log.ReadRecentEvents(10).Should().ContainSingle().Subject;
        persisted.EventId.Should().Be("exec.item.failed");
        persisted.OperationId.Should().Be("operation-schema-1");
        persisted.ItemId.Should().Be("item-schema-1");
    }

    [TestMethod]
    public void RecordingLogService_ReservedItemEventWithoutContext_IsRejectedLikeRealService()
    {
        // Arrange
        RecordingLogService log = new();

        // Act
        log.Write(
            "exec.item.failed",
            LogLevel.Error,
            LogStatus.Failed,
            "Элемент очереди не обработан");

        // Assert
        log.RejectedEventCount.Should().Be(1,
            "тестовый двойник обязан отклонять события по той же схеме, что и реальный сервис");
        RecordedRejection rejection = log.Rejections.Should().ContainSingle().Subject;
        rejection.EventId.Should().Be("exec.item.failed");
        rejection.ErrorCode.Should().Be("exec-item-missing-operation-id");
        log.Events.Should().ContainSingle()
            .Which.EventId.Should().Be(LogEventMarkerNames.RejectedEvent,
                "отклонённое событие не должно выдаваться за записанное");
    }

    [TestMethod]
    public void RecordingLogService_ReservedItemEventWithoutItemId_IsRejected()
    {
        // Arrange
        RecordingLogService log = new();
        LogContext context = LogContext.Empty.WithOperation("operation-schema-2");

        // Act
        log.Write(
            "exec.item.succeeded",
            LogLevel.Info,
            LogStatus.Succeeded,
            "Элемент очереди завершён",
            context: context);

        // Assert
        log.RejectedEventCount.Should().Be(1);
        log.Rejections.Should().ContainSingle()
            .Which.ErrorCode.Should().Be("exec-item-missing-item-id");
    }

    [TestMethod]
    public void RecordingLogService_ReservedItemEventWithContext_IsAccepted()
    {
        // Arrange
        RecordingLogService log = new();
        LogContext context = LogContext.Empty
            .WithOperation("operation-schema-3")
            .WithItem("item-schema-3");

        // Act
        log.Write(
            "exec.item.partial",
            LogLevel.Warning,
            LogStatus.PartiallySucceeded,
            "Элемент очереди завершён частично",
            context: context);

        // Assert
        log.RejectedEventCount.Should().Be(0);
        RecordedLogEvent recorded = log.Events.Should().ContainSingle().Subject;
        recorded.EventId.Should().Be("exec.item.partial");
        recorded.GetProperty<string>("OperationId").Should().Be("operation-schema-3");
        recorded.GetProperty<string>("ItemId").Should().Be("item-schema-3");
    }

    [TestMethod]
    public void RecordingLogService_ProcessEventWithoutTool_IsRejected()
    {
        // Arrange
        RecordingLogService log = new();
        LogContext context = LogContext.Empty
            .WithOperation("operation-schema-4")
            .WithProcess("process-schema-4");

        // Act
        log.Write(
            "process.started",
            LogLevel.Info,
            LogStatus.Running,
            "Внешний процесс запущен",
            context: context);

        // Assert
        log.RejectedEventCount.Should().Be(1);
        log.Rejections.Should().ContainSingle()
            .Which.ErrorCode.Should().Be("process-missing-tool");
    }

    [TestMethod]
    public void RecordingLogService_QueueEventWithoutOperationId_IsRejected()
    {
        // Arrange
        RecordingLogService log = new();

        // Act
        log.Write(
            "exec.queue.ended",
            LogLevel.Error,
            LogStatus.Failed,
            "Очередь завершена с ошибкой");

        // Assert
        log.RejectedEventCount.Should().Be(1);
        log.Rejections.Should().ContainSingle()
            .Which.ErrorCode.Should().Be("batch-missing-operation-id");
    }

    [TestMethod]
    public void RecordingLogService_Status_ExposesCounters()
    {
        // Arrange
        RecordingLogService log = new();

        // Act
        log.Write(
            "exec.queue.ended",
            LogLevel.Error,
            LogStatus.Failed,
            "Очередь завершена с ошибкой");
        log.Write("app.diagnostics.sample", LogLevel.Info, "Обычное событие");

        // Assert
        LogServiceStatus status = log.Status;
        status.RejectedEvents.Should().Be(1);
        status.HasLoss.Should().BeTrue();
        status.Describe().Should().Contain("rejected=1");
        log.Status.MinLevel.Should().Be(LogLevel.Debug);
    }

    [TestMethod]
    public void RenamedEventIds_RealLogService_AreAcceptedWithoutProcessContext()
    {
        // Arrange
        using var scope = new TempDirectoryScope();
        LogServiceOptions options = new()
        {
            CustomLogDirectory = scope.GetFullPath("logs"),
            EmergencyDirectory = scope.GetFullPath("emergency"),
            FlushIntervalMilliseconds = 60000
        };
        using LogService log = new(options);
        string filePath = scope.CreateFile("locked.mkv", "data");

        // Act
        double duration = FFmpegOutputParser.ParseHeaderDuration("  Duration: 99999999999:10:20, start: 0.000000", log);
        string locking = KTools_App.Core.FileLockDetector.GetLockingProcessesInfo(filePath, log);

        // Assert
        log.Flush(TimeSpan.FromSeconds(10)).Should().BeTrue();
        log.RejectedEventCount.Should().Be(0,
            "события определения блокировки и разбора вывода не являются жизненным циклом процесса и не должны отклоняться");
        duration.Should().Be(0.0);
        locking.Should().NotBeNull();

        IReadOnlyList<LogEvent> persisted = log.ReadRecentEvents(20);
        persisted.Should().Contain(e => e.EventId == "ffmpeg_parser.duration.parse.failed");
        persisted.Should().NotContain(e => e.EventId.StartsWith("log.event.rejected", StringComparison.Ordinal));
    }

    [TestMethod]
    public void LogVerify_MatchesEventWrittenThroughLogEventOverload()
    {
        // Arrange
        Mock<ILogService> log = MockBuilders.CreateLogServiceMock();

        // Act
        log.Object.Write(new LogEvent
        {
            EventId = "app.diagnostics.sample",
            Level = LogLevel.Warning,
            Status = LogStatus.Failed,
            Source = "Test",
            Message = "Диагностическое событие"
        });

        // Assert
        log.VerifyEventId("app.diagnostics.sample", LogLevel.Warning, Moq.Times.Once());
        log.VerifyEvent(LogLevel.Warning, "Диагностическое", Moq.Times.Once());
    }

    [TestMethod]
    public void LogVerify_MatchesEventWrittenThroughExplicitOverload()
    {
        // Arrange
        Mock<ILogService> log = MockBuilders.CreateLogServiceMock();

        // Act
        log.Object.Write(
            "app.diagnostics.sample",
            LogLevel.Error,
            LogStatus.Failed,
            "Диагностическое событие",
            new InvalidOperationException("причина"),
            "Test");

        // Assert
        log.VerifyEventId("app.diagnostics.sample", LogLevel.Error, Moq.Times.Once());
        log.VerifyEventWithException(LogLevel.Error, "Диагностическое");
    }

    [TestMethod]
    public void LogVerify_DoesNotMatchEventWithDifferentEventId()
    {
        // Arrange
        Mock<ILogService> log = MockBuilders.CreateLogServiceMock();
        log.Object.Write("app.diagnostics.one", LogLevel.Info, LogStatus.None, "Первое событие");

        // Act / Assert
        Action verify = () => log.VerifyEventId("app.diagnostics.two", LogLevel.Info, Moq.Times.Once());
        verify.Should().Throw<Exception>("проверка обязана отличать события по идентификатору");
    }

    [TestMethod]
    public void LogVerify_VerifyNoEvents_PassesWhenNothingWasWritten()
    {
        // Arrange
        Mock<ILogService> log = MockBuilders.CreateLogServiceMock();

        // Act / Assert
        Action verify = () => log.VerifyNoEvents();
        verify.Should().NotThrow("отсутствие записей журнала не является вхолостую пройденной проверкой");
    }

    [TestMethod]
    public void LogVerify_VerifyNoEventAtOrAbove_ChecksBothOverloads()
    {
        // Arrange
        Mock<ILogService> log = MockBuilders.CreateLogServiceMock();
        log.Object.Write(new LogEvent
        {
            EventId = "app.diagnostics.sample",
            Level = LogLevel.Error,
            Message = "Ошибка диагностики"
        });
        log.Object.Write("app.diagnostics.other", LogLevel.Debug, "Отладочная запись");

        // Act / Assert
        Action missing = () => log.VerifyNoEventAtOrAbove(LogLevel.Info, "Отладочная запись");
        missing.Should().NotThrow();

        Action present = () => log.VerifyNoEventAtOrAbove(LogLevel.Info, "Ошибка диагностики");
        present.Should().Throw<Exception>("событие LogEvent тоже должно попадать под проверку уровня");
    }

    [TestMethod]
    public void LogService_ValidateEventSchema_MatchesProductionReservedContracts()
    {
        // Arrange
        LogEvent validItem = new LogEvent
        {
            EventId = "exec.item.failed",
            Level = LogLevel.Error,
            Status = LogStatus.Failed,
            Source = "Test",
            Message = "Ошибка",
            OperationId = "operation-validate",
            ItemId = "item-validate"
        };
        LogEvent withoutItem = validItem with { ItemId = null };

        // Act / Assert
        LogService.ValidateEventSchema(validItem).Should().BeNull();
        LogService.ValidateEventSchema(withoutItem).Should().Be("exec-item-missing-item-id");
        LogService.ValidateEventSchema(new LogEvent
        {
            EventId = "filelock.query.failed",
            Level = LogLevel.Debug,
            Source = "Test",
            Message = "Блокировка не определена"
        }).Should().BeNull("имена вне зарезервированных префиксов не требуют контекста операции");
        LogService.ValidateEventSchema(new LogEvent
        {
            EventId = "ffmpeg_parser.progress.parse.failed",
            Level = LogLevel.Debug,
            Source = "Test",
            Message = "Строка прогресса не распознана"
        }).Should().BeNull("парсер вывода не заявляет контракт жизненного цикла процесса");
    }

    [TestMethod]
    public void LogServiceStatus_Describe_ContainsAllCounters()
    {
        // Arrange
        LogServiceStatus status = new(
            LogLevel.Info,
            1,
            2,
            3,
            4,
            5,
            6,
            7,
            8,
            9,
            10);

        // Act
        string described = status.Describe();

        // Assert
        described.Should().Contain("minLevel=Info");
        described.Should().Contain("queued=1");
        described.Should().Contain("queuedBytes=2");
        described.Should().Contain("dropped=3");
        described.Should().Contain("writeErrors=4");
        described.Should().Contain("rotationErrors=5");
        described.Should().Contain("rejected=6");
        described.Should().Contain("subscriberErrors=7");
        described.Should().Contain("subscriberDropped=8");
        described.Should().Contain("disposeErrors=9");
        described.Should().Contain("readErrors=10");
        status.HasLoss.Should().BeTrue();
    }

    [TestMethod]
    public void App_ResolveStartupLogLevel_ReleaseIsReleaseSafeAndDebugIsOptIn()
    {
        // Arrange / Act / Assert
        KTools_App.App.ResolveStartupLogLevel(diagnosticBuild: false, rawOverride: null)
            .Should().Be(LogLevel.Info, "release-сборка не должна писать Debug-диагностику по умолчанию");
        KTools_App.App.ResolveStartupLogLevel(diagnosticBuild: true, rawOverride: null)
            .Should().Be(LogLevel.Debug, "диагностическая сборка включает Debug");
        KTools_App.App.ResolveStartupLogLevel(diagnosticBuild: false, rawOverride: "debug")
            .Should().Be(LogLevel.Debug, "явное переопределение включает Debug-диагностику");
        KTools_App.App.ResolveStartupLogLevel(diagnosticBuild: true, rawOverride: " warning ")
            .Should().Be(LogLevel.Warning, "явное переопределение выше Debug должно применяться");
        KTools_App.App.ResolveStartupLogLevel(diagnosticBuild: false, rawOverride: " nonsense ")
            .Should().Be(LogLevel.Info, "некорректное переопределение не должно отключать фильтрацию журнала");
        KTools_App.App.MinLevelEnvironmentVariable.Should().Be("KTOOLS_LOG_MIN_LEVEL");
    }
}
