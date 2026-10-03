using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
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

namespace KTools_App.Tests.Diagnostics;

[TestClass]
[DoNotParallelize]
public sealed class CrashLifecycleTests
{
    [TestInitialize]
    public void SetUp()
    {
        ActiveProcessTracker.ResetForTests();
    }

    [TestCleanup]
    public void TearDown()
    {
        ActiveProcessTracker.ResetForTests();
    }

    private static bool StopWatcherStage(List<string> order)
    {
        order.Add("watcher");
        return true;
    }

    private static Process? StartProcess()
    {
        return Process.Start(new ProcessStartInfo("ping.exe", "-n 30 127.0.0.1")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        });
    }

    private static void SafeKill(Process? process)
    {
        try
        {
            if (process is not null && !process.HasExited)
            {
                process.Kill();
                process.WaitForExit(2000);
            }
        }
        catch (Exception)
        {
        }

        try
        {
            process?.Dispose();
        }
        catch (Exception)
        {
        }
    }

    [TestMethod]
    public void CrashCoordinator_WritesOneBoundedRedactedEmergencyRecord()
    {
        using var scope = new TempDirectoryScope();
        EmergencyLogSink sink = CreateSink(scope);
        using var coordinator = new CrashCoordinator(sink);
        Exception exception = new InvalidOperationException(
            "password=SENTINEL-PASSWORD C:\\Users\\private\\input.mkv",
            new ArgumentException("token=SENTINEL-TOKEN"));

        CrashWriteResult result = coordinator.RecordWinUiUnhandled(exception);

        result.EmergencyWritten.Should().BeTrue();
        result.Record.CrashId.Should().HaveLength(CrashCoordinator.MaxCrashIdLength);
        string[] files = Directory.GetFiles(
            Path.Combine(scope.RootPath, "emergency"),
            EmergencyLogSink.FileSearchPattern);
        files.Should().ContainSingle();
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(files[0]));
        string payload = File.ReadAllText(files[0]);
        payload.Should().NotContain("SENTINEL-PASSWORD");
        payload.Should().NotContain("SENTINEL-TOKEN");
        payload.Should().NotContain("C:\\Users\\private");
        document.RootElement.GetProperty("channel").GetString().Should().Be("crash_emergency");
        document.RootElement.GetProperty("crashId").GetString().Should().Be(result.Record.CrashId);
        new FileInfo(files[0]).Length.Should().BeLessThanOrEqualTo(sink.MaxFileBytes);
    }

    [TestMethod]
    public void CrashCoordinator_DuplicateCrashId_DoesNotWriteSecondEmergencyRecord()
    {
        using var scope = new TempDirectoryScope();
        EmergencyLogSink sink = CreateSink(scope);
        using var coordinator = new CrashCoordinator(sink);
        CrashRecord record = coordinator.CreateRecord(
            "App.Test",
            new InvalidOperationException("first"),
            "first",
            LogLevel.Error,
            CrashEventKind.WinUiUnhandled,
            emergencyRequired: true,
            crashId: "same-crash-id");

        CrashWriteResult first = coordinator.Record(record);
        CrashWriteResult second = coordinator.Record(record);

        first.EmergencyWritten.Should().BeTrue();
        second.Duplicate.Should().BeTrue();
        Directory.GetFiles(Path.Combine(scope.RootPath, "emergency"), EmergencyLogSink.FileSearchPattern)
            .Should().ContainSingle();
        sink.WrittenCount.Should().Be(1);
    }

    [TestMethod]
    public void CrashCoordinator_NegativeOutcome_AllowsRetryUntilConfirmed()
    {
        using var scope = new TempDirectoryScope();
        Mock<ILogService> log = new();
        int flushCalls = 0;
        log.Setup(service => service.Flush(It.IsAny<TimeSpan>()))
            .Returns(() => ++flushCalls > 1);
        using var coordinator = new CrashCoordinator(CreateSink(scope), log.Object);
        AggregateException exception = new(new InvalidOperationException("retryable"));

        bool firstObserved = coordinator.TryRecordUnobservedTaskException(exception, out CrashWriteResult first);
        bool secondObserved = coordinator.TryRecordUnobservedTaskException(exception, out CrashWriteResult second);
        bool thirdObserved = coordinator.TryRecordUnobservedTaskException(exception, out CrashWriteResult third);

        firstObserved.Should().BeFalse("первая запись завершилась неудачей flush");
        first.StructuredWriteSucceeded.Should().BeFalse();
        first.Duplicate.Should().BeFalse();
        secondObserved.Should().BeTrue("неуспешный результат не блокирует повторную запись");
        second.StructuredWriteSucceeded.Should().BeTrue();
        second.Retried.Should().BeTrue();
        second.Duplicate.Should().BeFalse();
        thirdObserved.Should().BeTrue();
        third.Duplicate.Should().BeTrue("подтверждённая запись подавляет повтор");
        coordinator.RetryCount.Should().Be(1);
        coordinator.DuplicateCount.Should().Be(1);
    }

    [TestMethod]
    public void AppDomainTerminating_AfterControlledShutdown_WritesOwnEmergencyAndStructuredRecord()
    {
        using var scope = new TempDirectoryScope();
        RecordingLogService log = new();
        using var coordinator = new CrashCoordinator(CreateSink(scope), log);
        bool initiated = coordinator.TryHandleWinUiUnhandled(
            new InvalidOperationException("first"),
            "App.WinUi",
            () => true,
            out CrashWriteResult? firstResult);
        initiated.Should().BeTrue();

        CrashWriteResult terminating = coordinator.RecordAppDomainTerminating(
            new AccessViolationException("terminating"),
            isTerminating: true,
            crashId: coordinator.LastControlledShutdownCrashId);

        terminating.Record.CrashId.Should().NotBe(firstResult!.Record.CrashId);
        terminating.Record.RelatedCrashId.Should().Be(firstResult.Record.CrashId);
        terminating.EmergencyWritten.Should().BeTrue();
        terminating.Duplicate.Should().BeFalse();
        Directory.GetFiles(Path.Combine(scope.RootPath, "emergency"), EmergencyLogSink.FileSearchPattern)
            .Should().HaveCount(2, "terminating-событие всегда пишет собственную аварийную запись");
        RecordedLogEvent domainEvent = log.EventsById(CrashCoordinator.AppDomainTerminatingEventId)
            .Should().ContainSingle().Which;
        domainEvent.GetProperty<string>("CrashId").Should().Be(terminating.Record.CrashId);
        domainEvent.GetProperty<string>("RelatedCrashId").Should().Be(firstResult.Record.CrashId);
        domainEvent.GetProperty<string>("ErrorCode").Should().Be("TERMINATING");
    }

    [TestMethod]
    public void AppDomainTerminating_SameExceptionAsUnhandled_WritesSeparateRecord()
    {
        using var scope = new TempDirectoryScope();
        RecordingLogService log = new();
        using var coordinator = new CrashCoordinator(CreateSink(scope), log);
        Exception shared = new InvalidOperationException("shared");

        CrashWriteResult winUi = coordinator.RecordWinUiUnhandled(shared);
        CrashWriteResult terminating = coordinator.RecordAppDomainTerminating(
            shared,
            isTerminating: true,
            crashId: winUi.Record.CrashId);

        terminating.Duplicate.Should().BeFalse();
        terminating.Record.CrashId.Should().NotBe(winUi.Record.CrashId);
        EmergencyFileCount(scope).Should().Be(2);
    }

    [TestMethod]
    public void DisposedCoordinator_AppDomainHandler_FallsBackToEmergencySink()
    {
        using var scope = new TempDirectoryScope();
        RecordingLogService log = new();
        var coordinator = new CrashCoordinator(CreateSink(scope), log);
        coordinator.Dispose();
        coordinator.Dispose();
        long sharedBefore = EmergencyLogSink.Shared.WrittenCount;

        CrashWriteResult result = coordinator.RecordAppDomainTerminating(
            new InvalidOperationException("after-dispose"),
            isTerminating: true);

        coordinator.IsDisposed.Should().BeTrue();
        result.EmergencyWritten.Should().BeTrue();
        result.StructuredWriteSucceeded.Should().BeFalse();
        result.ErrorCode.Should().Be(CrashCoordinator.ErrorDisposedFallbackWritten);
        EmergencyLogSink.Shared.WrittenCount.Should().BeGreaterThan(sharedBefore);
        coordinator.DisposedFallbackCount.Should().Be(1);
        log.Events.Should().BeEmpty("disposed-coordinator не пишет через освобождённый сервис журнала");
    }

    [TestMethod]
    public void DisposedCoordinator_ShutdownOutcome_UsesIndependentEmergencyFallback()
    {
        using var scope = new TempDirectoryScope();
        var coordinator = new CrashCoordinator(CreateSink(scope), new RecordingLogService());
        coordinator.Dispose();
        long sharedBefore = EmergencyLogSink.Shared.WrittenCount;

        coordinator.RecordShutdownOutcome(new CrashShutdownResult(
            shutdownRequested: true,
            operationsStopped: true,
            settingsPersisted: true,
            processesVerified: true,
            watcherStopped: true,
            flushSucceeded: true,
            servicesDisposed: true,
            exitRequested: false,
            "exit-request-failed"));

        EmergencyLogSink.Shared.WrittenCount.Should().BeGreaterThan(sharedBefore);
        coordinator.DisposedFallbackCount.Should().Be(1);
    }

    [TestMethod]
    public void WinUiPolicy_DoesNotMarkHandledWhenShutdownWasNotStarted()
    {
        using var scope = new TempDirectoryScope();
        using var coordinator = new CrashCoordinator(CreateSink(scope));

        bool handled = coordinator.TryHandleWinUiUnhandled(
            new InvalidOperationException("first"),
            "App.WinUi",
            () => false,
            out CrashWriteResult? result);

        handled.Should().BeFalse("обработчик не может подавить исключение без запущенного завершения");
        result!.ControlledShutdownInitiated.Should().BeFalse();
        result.ErrorCode.Should().Be("controlled-shutdown-not-initiated");
        result.EmergencyWritten.Should().BeTrue("аварийная запись пишется независимо от запуска завершения");
        coordinator.IsRedeliveryDisabled.Should().BeTrue(
            "повторная доставка отключается до аварийной записи");
    }

    [TestMethod]
    public void WinUiPolicy_MarksHandledOnlyForFirstStartedShutdown()
    {
        using var scope = new TempDirectoryScope();
        using var coordinator = new CrashCoordinator(CreateSink(scope));
        int shutdownStarts = 0;

        bool first = coordinator.TryHandleWinUiUnhandled(
            new InvalidOperationException("first"),
            "App.WinUi",
            () =>
            {
                shutdownStarts++;
                return true;
            },
            out CrashWriteResult? firstResult);
        bool second = coordinator.TryHandleWinUiUnhandled(
            new InvalidOperationException("second"),
            "App.WinUi",
            () =>
            {
                shutdownStarts++;
                return true;
            },
            out CrashWriteResult? secondResult);

        first.Should().BeTrue();
        firstResult!.ControlledShutdownInitiated.Should().BeTrue();
        second.Should().BeFalse("повторная доставка не запускает второе завершение");
        secondResult!.Record.CrashId.Should().NotBe(firstResult.Record.CrashId);
        secondResult.EmergencyWritten.Should().BeTrue("аварийная запись не подавляется повторной доставкой");
        shutdownStarts.Should().Be(1);
        coordinator.LastControlledShutdownCrashId.Should().Be(firstResult.Record.CrashId);
    }

    [TestMethod]
    public void TryBeginControlledShutdown_OpensGateOnlyAfterSuccessfulStart()
    {
        using var scope = new TempDirectoryScope();
        using var coordinator = new CrashCoordinator(CreateSink(scope));
        int startAttempts = 0;

        bool failed = coordinator.TryBeginControlledShutdown(
            "crash",
            () =>
            {
                startAttempts++;
                throw new InvalidOperationException("start-failure");
            },
            out CrashShutdownStartResult failedResult);
        bool gateClosedAfterFailure = coordinator.IsControlledShutdownStarted;

        bool started = coordinator.TryBeginControlledShutdown(
            "crash",
            () =>
            {
                startAttempts++;
                return true;
            },
            out CrashShutdownStartResult startedResult);
        bool repeated = coordinator.TryBeginControlledShutdown(
            "crash",
            () =>
            {
                startAttempts++;
                return true;
            },
            out CrashShutdownStartResult repeatedResult);

        failed.Should().BeFalse();
        failedResult.ErrorCode.Should().Be("shutdown-start-failed");
        gateClosedAfterFailure.Should().BeFalse("неуспешный запуск не выставляет флаг завершения");
        started.Should().BeTrue();
        startedResult.Initiated.Should().BeTrue();
        coordinator.IsControlledShutdownStarted.Should().BeTrue();
        repeated.Should().BeTrue();
        repeatedResult.AlreadyInProgress.Should().BeTrue();
        repeatedResult.Initiated.Should().BeFalse();
        startAttempts.Should().Be(2, "повторный запрос не запускает второе завершение");
    }

    [TestMethod]
    public async Task ControlledShutdown_KeepsStageOrderAndReportsExitAsRequiredForSuccess()
    {
        using var scope = new TempDirectoryScope();
        RecordingLogService log = new();
        using var coordinator = new CrashCoordinator(CreateSink(scope), log);
        List<string> order = new();
        ProcessTerminationSummary summary = ProcessTerminationSummary.NoTrackedProcesses;
        CrashShutdownResult? terminal = null;

        CrashShutdownResult result = await coordinator.RunControlledShutdownStagesAsync(
            "window.closed",
            stopOperations: _ =>
            {
                order.Add("operations");
                return Task.FromResult(true);
            },
            stopWatcher: _ =>
            {
                order.Add("watcher");
                return Task.FromResult(true);
            },
            terminateProcesses: _ =>
            {
                order.Add("processes");
                return Task.FromResult(summary);
            },
            flush: _ =>
            {
                order.Add("flush");
                return Task.FromResult(true);
            },
            disposeServices: () =>
            {
                order.Add("dispose");
                return Task.FromResult(true);
            },
            requestExit: () =>
            {
                order.Add("exit");
                return Task.FromResult(true);
            },
            cancellationToken: CancellationToken.None,
            persistSettings: _ =>
            {
                order.Add("settings");
                return Task.FromResult(SettingsPersistenceResult.Success);
            },
            writeTerminalEvent: (provisional, _) =>
            {
                terminal = provisional;
                order.Add("terminal");
                return Task.CompletedTask;
            });

        order.Should().Equal(
            "operations",
            "settings",
            "processes",
            "watcher",
            "flush",
            "terminal",
            "dispose",
            "exit");
        result.Succeeded.Should().BeTrue();
        result.ExitRequested.Should().BeTrue();
        result.ExecutedStages.Should().Equal(
            ShutdownStages.OperationIntake,
            ShutdownStages.SettingsPersistence,
            ShutdownStages.ProcessTermination,
            ShutdownStages.Watcher,
            ShutdownStages.LogFlush,
            ShutdownStages.TerminalEvent,
            ShutdownStages.DisposeServices,
            ShutdownStages.ExitRequest);
        terminal.Should().NotBeNull();
        terminal!.Succeeded.Should().BeFalse("до запроса выхода успех не заявляется");
        terminal.ErrorCode.Should().Be(CrashShutdownResult.ErrorExitRequestPending);
        terminal.ExitRequested.Should().BeFalse();
    }

    [TestMethod]
    public async Task ControlledShutdown_NormalWindowClose_WritesStructuredOutcomeBeforeDisposeAndCreatesNoEmergencyFile()
    {
        using var scope = new TempDirectoryScope();
        RecordingLogService log = new();
        using var coordinator = new CrashCoordinator(CreateSink(scope), log);
        List<string> order = new();
        bool outcomeWrittenBeforeDispose = false;

        CrashShutdownResult result = await coordinator.RunControlledShutdownStagesAsync(
            "window.closed",
            stopOperations: _ => Task.FromResult(true),
            stopWatcher: _ => Task.FromResult(StopWatcherStage(order)),
            terminateProcesses: _ => Task.FromResult(ProcessTerminationSummary.NoTrackedProcesses),
            flush: _ => Task.FromResult(true),
            disposeServices: () =>
            {
                outcomeWrittenBeforeDispose = log
                    .EventsById(CrashCoordinator.ControlledShutdownOutcomeEventId)
                    .Count == 1;
                order.Add("dispose");
                return Task.FromResult(true);
            },
            requestExit: () => Task.FromResult(true),
            cancellationToken: CancellationToken.None,
            persistSettings: _ => Task.FromResult(SettingsPersistenceResult.Success),
            writeTerminalEvent: (_, _) =>
            {
                order.Add("terminal");
                return Task.CompletedTask;
            });

        result.Succeeded.Should().BeTrue();
        result.StagesSucceeded.Should().BeTrue();
        outcomeWrittenBeforeDispose.Should().BeTrue("результат завершения записывается до освобождения служб");
        EmergencyFileCount(scope).Should().Be(0, "обычный выход не создаёт аварийный файл");
        coordinator.ShutdownOutcomeDroppedCount.Should().Be(0);

        RecordedLogEvent outcome = log.EventsById(CrashCoordinator.ControlledShutdownOutcomeEventId)
            .Should().ContainSingle().Which;
        outcome.Level.Should().Be(LogLevel.Info);
        outcome.Status.Should().Be(LogStatus.Succeeded, "итоговое событие завершает этапы и не заявляет о продолжающейся работе");
        outcome.GetProperty<bool>("Succeeded").Should().BeTrue();
        outcome.GetProperty<bool>("Failed").Should().BeFalse();
        outcome.GetProperty<bool>("ExitRequested").Should().BeFalse("запрос выхода выполняется после записи результата");
        outcome.GetProperty<bool>("ExitRequestPending").Should().BeTrue();
        outcome.Properties.Should().NotContainKey(
            "ErrorCode",
            "ожидание запроса выхода не является ошибкой, поэтому непустой ErrorCode при Succeeded недопустим");
        outcome.GetProperty<string>("ExecutedStages").Should().Contain(ShutdownStages.TerminalEvent);
    }

    [TestMethod]
    public void ControlledShutdownOutcome_InfoLevel_NeverFallsBackToEmergencySink()
    {
        using var scope = new TempDirectoryScope();
        var coordinator = new CrashCoordinator(CreateSink(scope), new RecordingLogService());
        coordinator.Dispose();
        long sharedBefore = EmergencyLogSink.Shared.WrittenCount;

        coordinator.RecordShutdownOutcome(new CrashShutdownResult(
            shutdownRequested: true,
            operationsStopped: true,
            settingsPersisted: true,
            processesVerified: true,
            watcherStopped: true,
            flushSucceeded: true,
            servicesDisposed: false,
            exitRequested: false,
            CrashShutdownResult.ErrorExitRequestPending));

        EmergencyLogSink.Shared.WrittenCount.Should().Be(sharedBefore, "информационный результат не является аварией");
        EmergencyFileCount(scope).Should().Be(0);
        coordinator.ShutdownOutcomeDroppedCount.Should().Be(1);
    }

    [TestMethod]
    public async Task ControlledShutdown_NullIntakeAndKillStages_ReportStageUnavailable()
    {
        using var scope = new TempDirectoryScope();
        RecordingLogService log = new();
        using var coordinator = new CrashCoordinator(CreateSink(scope), log);
        List<string> order = new();

        CrashShutdownResult result = await coordinator.RunControlledShutdownStagesAsync(
            "crash.unhandled_exception",
            stopOperations: null,
            stopWatcher: _ => Task.FromResult(true),
            terminateProcesses: null,
            flush: _ => Task.FromResult(true),
            disposeServices: () =>
            {
                order.Add("dispose");
                return Task.FromResult(true);
            },
            requestExit: () =>
            {
                order.Add("exit");
                return Task.FromResult(true);
            },
            cancellationToken: CancellationToken.None,
            persistSettings: _ => Task.FromResult(SettingsPersistenceResult.Success),
            writeTerminalEvent: null);

        result.OperationsStopped.Should().BeFalse("отсутствие зарегистрированной стадии приёма операций — это сбой");
        result.ProcessesVerified.Should().BeFalse("отсутствие стадии завершения процессов не считается успехом");
        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be(CrashShutdownResult.ErrorStageUnavailable);
        result.ProcessSummary.Should().NotBeNull();
        result.ProcessSummary!.ErrorCode.Should().Be(CrashShutdownResult.ErrorStageUnavailable);
        result.ProcessSummary.AllVerified.Should().BeFalse();
        order.Should().Equal(
            new[] { "dispose", "exit" },
            "оставшиеся стадии выполняются при недоступных стадиях приёма");
        result.ExecutedStages.Should().NotContain(ShutdownStages.OperationIntake);
        result.ExecutedStages.Should().NotContain(ShutdownStages.ProcessTermination);
    }

    [TestMethod]
    public async Task ControlledShutdown_WithoutSettingsStage_DoesNotClaimSettingsPersisted()
    {
        using var scope = new TempDirectoryScope();
        using var coordinator = new CrashCoordinator(CreateSink(scope), new RecordingLogService());

        CrashShutdownResult result = await coordinator.RunControlledShutdownStagesAsync(
            "crash.unhandled_exception",
            stopOperations: _ => Task.FromResult(true),
            stopWatcher: _ => Task.FromResult(true),
            terminateProcesses: _ => Task.FromResult(ProcessTerminationSummary.NoTrackedProcesses),
            flush: _ => Task.FromResult(true),
            disposeServices: () => Task.FromResult(true),
            requestExit: () => Task.FromResult(true),
            cancellationToken: CancellationToken.None,
            persistSettings: null,
            writeTerminalEvent: null);

        result.SettingsPersisted.Should().BeFalse("сбой до создания окна не может сообщать о сохранённых настройках");
        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be(CrashShutdownResult.ErrorStageUnavailable);
        result.ErrorCode.Should().NotBe(CrashShutdownResult.ErrorSettingsPersistenceFailed);
    }

    [TestMethod]
    public async Task ControlledShutdown_LateProcessAfterKillStage_IsRejectedAndNotCountedAsVerified()
    {
        using var scope = new TempDirectoryScope();
        using var coordinator = new CrashCoordinator(CreateSink(scope), new RecordingLogService());
        Process? tracked = StartProcess();
        Process? late = StartProcess();
        tracked.Should().NotBeNull();
        late.Should().NotBeNull();
        bool lateRegistered = true;

        try
        {
            ActiveProcessTracker.Register(tracked!).Should().BeTrue();
            ProcessTerminationSummary? captured = null;

            CrashShutdownResult result = await coordinator.RunControlledShutdownStagesAsync(
                "window.closed",
                stopOperations: _ => Task.FromResult(ActiveProcessTracker.TryBeginShutdown()),
                stopWatcher: _ => Task.FromResult(true),
                terminateProcesses: _ =>
                {
                    captured = ActiveProcessTracker.KillAll(TimeSpan.FromSeconds(2));
                    return Task.FromResult(captured);
                },
                flush: _ =>
                {
                    lateRegistered = ActiveProcessTracker.Register(late!);
                    return Task.FromResult(true);
                },
                disposeServices: () => Task.FromResult(true),
                requestExit: () => Task.FromResult(true),
                cancellationToken: CancellationToken.None,
                persistSettings: _ => Task.FromResult(SettingsPersistenceResult.Success),
                writeTerminalEvent: null);

            captured.Should().NotBeNull();
            captured!.Results.Should().HaveCount(1, "поздний процесс не попадает в снимок завершения");
            captured.IsConfirmedNoOp.Should().BeFalse();
            captured.Total.Should().Be(1);
            captured.Verified.Should().Be(1);
            captured.Skipped.Should().Be(0);
            lateRegistered.Should().BeFalse("после снимка завершения регистрация отклоняется");
            result.ProcessesVerified.Should().BeTrue();
            result.Succeeded.Should().BeTrue();
            tracked!.HasExited.Should().BeTrue();
        }
        finally
        {
            ActiveProcessTracker.ResetForTests();
            SafeKill(tracked);
            SafeKill(late);
        }
    }

    [TestMethod]
    public async Task ControlledShutdown_CancelledToken_SkipsRemainingStagesAndReportsCancellation()
    {
        using var scope = new TempDirectoryScope();
        using var coordinator = new CrashCoordinator(CreateSink(scope), new RecordingLogService());
        using CancellationTokenSource cts = new();
        cts.Cancel();

        CrashShutdownResult result = await coordinator.RunControlledShutdownStagesAsync(
            "window.closed",
            stopOperations: _ => Task.FromResult(true),
            stopWatcher: _ => Task.FromResult(true),
            terminateProcesses: _ => Task.FromResult(ProcessTerminationSummary.NoTrackedProcesses),
            flush: _ => Task.FromResult(true),
            disposeServices: () => Task.FromResult(true),
            requestExit: () => Task.FromResult(true),
            cancellationToken: cts.Token,
            persistSettings: _ => Task.FromResult(SettingsPersistenceResult.Success),
            writeTerminalEvent: null);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be(CrashShutdownResult.ErrorCancelled);
        result.ExecutedStages.Should().BeEmpty("отменённые стадии не выполняются");
        result.ExitRequested.Should().BeFalse();
    }

    [TestMethod]
    public async Task ControlledShutdown_FailedStageAndUnverifiedProcesses_NeverReportSuccess()
    {
        using var scope = new TempDirectoryScope();
        using var coordinator = new CrashCoordinator(CreateSink(scope), new RecordingLogService());
        ProcessTerminationResult unverified = new(
            "proc-1",
            4242,
            attempted: true,
            succeeded: false,
            failed: true,
            alreadyExited: false,
            exitCode: null,
            errors: new[] { "kill-failed" });
        ProcessTerminationSummary summary = new(
            attempted: 1,
            succeeded: 0,
            failed: 1,
            alreadyExited: 0,
            new[] { unverified },
            new[] { "kill-failed" });

        CrashShutdownResult result = await coordinator.RunControlledShutdownStagesAsync(
            "crash",
            stopOperations: _ => Task.FromResult(true),
            stopWatcher: _ => Task.FromResult(true),
            terminateProcesses: _ => Task.FromResult(summary),
            flush: _ => Task.FromResult(false),
            disposeServices: () => Task.FromResult(true),
            requestExit: () => Task.FromResult(true),
            cancellationToken: CancellationToken.None,
            persistSettings: _ => Task.FromResult(SettingsPersistenceResult.Failure("access-denied")),
            writeTerminalEvent: null);

        result.Succeeded.Should().BeFalse();
        result.ProcessesVerified.Should().BeFalse();
        result.FlushSucceeded.Should().BeFalse();
        result.SettingsPersisted.Should().BeFalse();
        result.ExitRequested.Should().BeTrue();
        result.ErrorCode.Should().Be(CrashShutdownResult.ErrorSettingsPersistenceFailed);
        result.ProcessSummary.Should().NotBeNull();
        Dictionary<string, object?> properties = result.ProcessSummary!.ToLogProperties();
        properties["Pid"].Should().Be("4242");
        properties["ProcessId"].Should().Be("proc-1");
        properties["Errors"].Should().Be("kill-failed");
        properties["Failed"].Should().Be(1);
        properties["Attempted"].Should().Be(1);
        properties["Verified"].Should().Be(0);
    }

    [TestMethod]
    public async Task ControlledShutdown_HungStage_IsBoundedAndDoesNotBlockFollowingStages()
    {
        using var scope = new TempDirectoryScope();
        using var coordinator = new CrashCoordinator(CreateSink(scope), new RecordingLogService());
        bool exitRequested = false;

        CrashShutdownResult result = await coordinator.RunControlledShutdownStagesAsync(
            "crash",
            stopOperations: _ => Task.FromResult(true),
            stopWatcher: _ => new TaskCompletionSource<bool>().Task,
            terminateProcesses: _ => Task.FromResult(ProcessTerminationSummary.NoTrackedProcesses),
            flush: _ => Task.FromResult(true),
            disposeServices: () => Task.FromResult(true),
            requestExit: () =>
            {
                exitRequested = true;
                return Task.FromResult(true);
            },
            cancellationToken: CancellationToken.None,
            persistSettings: _ => Task.FromResult(SettingsPersistenceResult.Success),
            writeTerminalEvent: null);

        result.WatcherStopped.Should().BeFalse("зависшая стадия ограничена по времени");
        exitRequested.Should().BeTrue("следующие стадии не отменяются зависшей стадией");
        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be(CrashShutdownResult.ErrorWatcherStopFailed);
    }

    [TestMethod]
    public void UnobservedTaskException_IsObservedOnlyAfterSuccessfulBoundedWrite()
    {
        using var scope = new TempDirectoryScope();
        Mock<ILogService> log = new();
        log.Setup(service => service.Flush(It.IsAny<TimeSpan>())).Returns(true);
        using var coordinator = new CrashCoordinator(CreateSink(scope), log.Object);
        AggregateException exception = new(
            new InvalidOperationException("SENTINEL-AGGREGATE"),
            new ArgumentException("token=SENTINEL-TOKEN"));

        bool observed = coordinator.TryRecordUnobservedTaskException(exception, out CrashWriteResult result);

        observed.Should().BeTrue();
        result.StructuredWriteSucceeded.Should().BeTrue();
        log.Verify(service => service.Write(
            CrashCoordinator.UnobservedTaskEventId,
            LogLevel.Error,
            LogStatus.Failed,
            It.IsAny<string>(),
            It.IsAny<Exception>(),
            "App",
            It.IsAny<LogContext>(),
            It.IsAny<IReadOnlyDictionary<string, object?>>()), Times.Once);
        Directory.GetFiles(Path.Combine(scope.RootPath, "emergency"), EmergencyLogSink.FileSearchPattern)
            .Should().BeEmpty("observed background exceptions do not create crash reports");
    }

    [TestMethod]
    public void UnobservedTaskException_IsNotObservedWhenFlushFails()
    {
        using var scope = new TempDirectoryScope();
        Mock<ILogService> log = new();
        log.Setup(service => service.Flush(It.IsAny<TimeSpan>())).Returns(false);
        using var coordinator = new CrashCoordinator(CreateSink(scope), log.Object);

        bool observed = coordinator.TryRecordUnobservedTaskException(
            new AggregateException(new InvalidOperationException("failure")),
            out CrashWriteResult result);

        observed.Should().BeFalse();
        result.StructuredWriteSucceeded.Should().BeFalse();
    }

    [TestMethod]
    public void MainWindowShutdownPolicy_DoesNotReportSuccessForSaveOrFlushFailure()
    {
        ProcessTerminationSummary verified = new(1, 1, 0, 0);
        ProcessTerminationSummary failed = new(1, 0, 1, 0);

        MainWindow.CanReportShutdownSuccess(
            SettingsPersistenceResult.Success,
            verified,
            flushSucceeded: true,
            watcherStopped: true).Should().BeTrue();
        MainWindow.CanReportShutdownSuccess(
            SettingsPersistenceResult.Failure(),
            verified,
            flushSucceeded: true,
            watcherStopped: true).Should().BeFalse();
        MainWindow.CanReportShutdownSuccess(
            SettingsPersistenceResult.Success,
            verified,
            flushSucceeded: false,
            watcherStopped: true).Should().BeFalse();
        MainWindow.CanReportShutdownSuccess(
            SettingsPersistenceResult.Success,
            failed,
            flushSucceeded: true,
            watcherStopped: true).Should().BeFalse();
    }

    [TestMethod]
    public void ShutdownOutcomeEvent_ClaimsSuccessOnlyAfterExitRequested()
    {
        RecordingLogService log = new();
        using var coordinator = new CrashCoordinator(EmergencyLogSink.Shared, log);

        coordinator.RecordShutdownOutcome(new CrashShutdownResult(
            shutdownRequested: true,
            operationsStopped: true,
            settingsPersisted: true,
            processesVerified: true,
            watcherStopped: true,
            flushSucceeded: true,
            servicesDisposed: true,
            exitRequested: false,
            "exit-request-failed"));
        coordinator.RecordShutdownOutcome(new CrashShutdownResult(
            shutdownRequested: true,
            operationsStopped: true,
            settingsPersisted: true,
            processesVerified: true,
            watcherStopped: true,
            flushSucceeded: true,
            servicesDisposed: true,
            exitRequested: true));

        IReadOnlyList<RecordedLogEvent> events = log.EventsById(CrashCoordinator.ControlledShutdownOutcomeEventId);
        events.Should().HaveCount(2);
        events[0].Level.Should().Be(LogLevel.Error);
        events[0].Status.Should().Be(LogStatus.Failed);
        events[0].GetProperty<bool>("Succeeded").Should().BeFalse();
        events[0].GetProperty<string>("ErrorCode").Should().Be("exit-request-failed");
        events[1].Level.Should().Be(LogLevel.Info);
        events[1].Status.Should().Be(LogStatus.Succeeded);
        events[1].GetProperty<bool>("Succeeded").Should().BeTrue();
        events[1].GetProperty<string>("Status").Should().Contain("exit=True");
    }

    [TestMethod]
    public void AppSource_HasNoLegacyCrashReportWritesAndKeepsStartupOrder()
    {
        string source = File.ReadAllText(FindAppFile("App.xaml.cs"));
        source.Should().NotBeNullOrWhiteSpace();
        source.Should().NotContain("crash_report.txt");
        source.Should().NotContain("FormatCrashReport");
        source.Should().NotContain("WriteCrashReport");

        int pipelineIndex = source.IndexOf("AppStartupPipeline.Run", StringComparison.Ordinal);
        int loggerIndex = source.IndexOf("GetRequiredService<ILogService>", StringComparison.Ordinal);
        int coordinatorIndex = source.IndexOf("GetRequiredService<CrashCoordinator>", StringComparison.Ordinal);
        int settingsIndex = source.IndexOf("GetRequiredService<ISettingsManager>", StringComparison.Ordinal);
        pipelineIndex.Should().BeGreaterThanOrEqualTo(0);
        loggerIndex.Should().BeGreaterThan(pipelineIndex);
        coordinatorIndex.Should().BeGreaterThan(loggerIndex);
        settingsIndex.Should().BeGreaterThan(coordinatorIndex);
        source.IndexOf("RegisterCrashHandlers(Services", StringComparison.Ordinal)
            .Should().BeGreaterThan(-1)
            .And.BeLessThan(settingsIndex);
    }

    [TestMethod]
    public void AppStartupPipeline_ResolvesLoggerThenHandlersThenSettings()
    {
        using var scope = new TempDirectoryScope();
        RecordingLogService log = new();
        List<string> stages = new();
        string settingsDirectory = scope.GetFullPath("settings");
        Directory.CreateDirectory(settingsDirectory);
        Mock<IPathManager> pathManager = new();
        pathManager.Setup(manager => manager.GetSettingsDirectory()).Returns(settingsDirectory);
        SettingsManager? resolved = null;

        IReadOnlyList<string> completed = AppStartupPipeline.Run(
            log,
            () => stages.Add("handlers"),
            () =>
            {
                stages.Add("settings-resolve");
                resolved = new SettingsManager(log, pathManager.Object);
                log.Write(
                    "app.started",
                    LogLevel.Info,
                    "Application startup started",
                    "App",
                    context: null);
                return resolved;
            });

        stages.Should().Equal("handlers", "settings-resolve");
        completed.Should().Equal(
            AppStartupPipeline.LoggerStage,
            AppStartupPipeline.CrashHandlersStage,
            AppStartupPipeline.SettingsStage);
        resolved.Should().NotBeNull();
        IReadOnlyList<string> callOrder = log.CallOrder;
        callOrder.Should().NotBeEmpty();
        int firstInitialize = callOrder.ToList().FindIndex(entry => entry.StartsWith("init:", StringComparison.Ordinal));
        int firstEvent = callOrder.ToList().FindIndex(entry => entry.StartsWith("event:", StringComparison.Ordinal));
        firstInitialize.Should().BeGreaterThanOrEqualTo(0, "менеджер настроек выбирает фактический каталог журнала");
        firstEvent.Should().BeGreaterThan(firstInitialize, "до выбора каталога не пишется ни одного бизнес-события");
    }

    [TestMethod]
    public void AppStartupPipeline_WithoutLogger_NeverRegistersHandlersOrResolvesSettings()
    {
        List<string> stages = new();

        Action act = () => AppStartupPipeline.Run(
            null!,
            () => stages.Add("handlers"),
            () =>
            {
                stages.Add("settings-resolve");
                return new Mock<ISettingsManager>().Object;
            });

        act.Should().Throw<ArgumentNullException>();
        stages.Should().BeEmpty("без журналирования обработчики сбоя и настройки не поднимаются");
    }

    [TestMethod]
    public void ProgramSource_ChecksHandoffAndKeepsArgumentsOutOfDiagnostics()
    {
        string programSource = File.ReadAllText(FindAppFile("Program.cs"));
        string diagnosticsSource = File.ReadAllText(
            FindAppFile(Path.Combine("Services", "Contracts", "ProgramDiagnostics.cs")));
        programSource.Should().NotBeNullOrWhiteSpace();
        diagnosticsSource.Should().NotBeNullOrWhiteSpace();
        programSource.Should().NotContain("WriteArgsToFile");
        programSource.Should().NotContain("WriteFailure(args");
        programSource.Should().NotContain("WriteFailure(Environment");
        programSource.Should().Contain("ProgramDiagnostics.WriteFailure(PendingArgsWriteStage");
        programSource.Should().Contain("HandoffFailedExitCode");
        diagnosticsSource.Should().NotContain("GetCommandLineArgs");
        diagnosticsSource.Should().NotContain("new LogService(");
        diagnosticsSource.Should().Contain("EmergencyLogSink.Shared");
    }

    [TestMethod]
    public void ProgramDiagnostics_RecordsRedactedFailureWithSessionCorrelation()
    {
        using var scope = new TempDirectoryScope();
        EmergencyLogSink sink = CreateSink(scope);
        long failuresBefore = ProgramDiagnostics.FailureCount;

        bool written = ProgramDiagnostics.WriteFailure(
            "pending_args_write",
            new InvalidOperationException("argv C:\\Users\\private\\file.mkv --token=SENTINEL-TOKEN"),
            sink);

        written.Should().BeTrue();
        ProgramDiagnostics.FailureCount.Should().BeGreaterThan(failuresBefore);
        string[] files = Directory.GetFiles(
            Path.Combine(scope.RootPath, "emergency"),
            EmergencyLogSink.FileSearchPattern);
        files.Should().ContainSingle();
        string payload = File.ReadAllText(files[0]);
        payload.Should().NotContain("SENTINEL-TOKEN");
        payload.Should().NotContain("C:\\Users\\private");
        payload.Should().Contain(ProgramDiagnostics.FailureEventId);
        payload.Should().NotContain("pending_args_write",
            "в аварийную запись попадает только безопасный идентификатор стадии");
        using JsonDocument document = JsonDocument.Parse(payload);
        document.RootElement.GetProperty("session").GetString().Should().Be(ProgramDiagnostics.SessionId.ToString("D"));
        document.RootElement.GetProperty("source").GetString().Should().Be(ProgramDiagnostics.FailureSource);
    }

    [TestMethod]
    public void AppDomainTerminating_AfterControlledShutdown_KeepsRelatedIdAndProcessMetadataInRealJsonl()
    {
        using var scope = new TempDirectoryScope();
        LogServiceOptions options = new()
        {
            CustomLogDirectory = Path.Combine(scope.RootPath, "logs"),
            EmergencyDirectory = Path.Combine(scope.RootPath, "emergency"),
            FlushIntervalMilliseconds = 60000,
            FileFormat = LogFileFormat.Jsonl
        };
        Directory.CreateDirectory(options.EmergencyDirectory);
        using LogService log = new(options);
        using var coordinator = new CrashCoordinator(CreateSink(scope), log);
        bool initiated = coordinator.TryHandleWinUiUnhandled(
            new InvalidOperationException("first"),
            "App.WinUi",
            () => true,
            out CrashWriteResult? firstResult);
        initiated.Should().BeTrue();
        ProcessTerminationResult alreadyExited = new(
            "proc-42",
            4242,
            attempted: false,
            succeeded: false,
            failed: false,
            alreadyExited: true,
            exitCode: 7,
            errors: null);
        ProcessTerminationSummary summary = new(
            attempted: 0,
            succeeded: 0,
            failed: 0,
            alreadyExited: 1,
            new[] { alreadyExited },
            new[] { "exit-verification-timeout" });

        coordinator.RecordAppDomainTerminating(
            new AccessViolationException("terminating"),
            isTerminating: true,
            coordinator.LastControlledShutdownCrashId,
            summary.ToLogProperties());

        log.Flush(TimeSpan.FromSeconds(10)).Should().BeTrue();
        JsonElement terminating = ReadJsonlEvent(log.CurrentLogFile!, CrashCoordinator.AppDomainTerminatingEventId);
        JsonElement properties = terminating.GetProperty("properties");

        properties.GetProperty("RelatedCrashId").GetString().Should().Be(firstResult!.Record.CrashId);
        properties.GetProperty("ErrorCode").GetString().Should().Be("TERMINATING");
        properties.GetProperty("ProcessId").GetString().Should().Be("proc-42");
        properties.GetProperty("Pid").GetString().Should().Be("4242");
        properties.GetProperty("ExitCode").GetString().Should().Be("4242=7");
        properties.GetProperty("AlreadyExited").GetInt32().Should().Be(1);
        properties.GetProperty("Skipped").GetInt32().Should().Be(1);
        properties.GetProperty("Total").GetInt32().Should().Be(1);
        properties.GetProperty("Verified").GetInt32().Should().Be(1);
        properties.GetProperty("Errors").GetString().Should().Be("exit-verification-timeout");
        properties.TryGetProperty("markedProperties", out _).Should().BeFalse(
            "все безопасные поля процесса проходят allowlist редактора журнала");
    }

    [TestMethod]
    public async Task ExitRequestExecutor_ReturnsSuccessOnlyWhenExitActionExecuted()
    {
        bool executed = false;
        List<string> failures = new();

        bool success = await ExitRequestExecutor.RequestAsync(
            action =>
            {
                action();
                return true;
            },
            () => executed = true,
            TimeSpan.FromSeconds(1),
            failures.Add);

        success.Should().BeTrue();
        executed.Should().BeTrue();
        failures.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ExitRequestExecutor_EnqueueRejected_ReportsErrorCodeAndNoSuccess()
    {
        bool executed = false;
        List<string> failures = new();

        bool success = await ExitRequestExecutor.RequestAsync(
            _ => false,
            () => executed = true,
            TimeSpan.FromSeconds(1),
            failures.Add);

        success.Should().BeFalse("отклонённая постановка в очередь не является успешным выходом");
        executed.Should().BeFalse();
        failures.Should().Equal(ExitRequestExecutor.ErrorEnqueueFailed);
    }

    [TestMethod]
    public async Task ExitRequestExecutor_ExitThrows_ReportsErrorCodeAndNoSuccess()
    {
        List<string> failures = new();

        bool success = await ExitRequestExecutor.RequestAsync(
            null,
            () => throw new InvalidOperationException("exit-failed"),
            TimeSpan.FromSeconds(1),
            failures.Add);

        success.Should().BeFalse();
        failures.Should().Equal(ExitRequestExecutor.ErrorExecuteFailed);
    }

    [TestMethod]
    public async Task ExitRequestExecutor_ExitNeverRuns_IsBoundedByTimeout()
    {
        List<string> failures = new();

        bool success = await ExitRequestExecutor.RequestAsync(
            _ => true,
            () => { },
            TimeSpan.FromMilliseconds(50),
            failures.Add);

        success.Should().BeFalse("запрос выхода без выполнения действия не считается успешным");
        failures.Should().Equal(ExitRequestExecutor.ErrorTimeout);
    }

    [TestMethod]
    public void MainWindowSource_ChecksControlledShutdownResult()
    {
        string source = File.ReadAllText(FindAppFile("MainWindow.xaml.cs"));

        source.Should().Contain("bool completed = App.TryBeginControlledShutdownAndWait(");
        source.Should().Contain("app.shutdown.not_completed");
    }

    [TestMethod]
    public void ProgramDiagnostics_SequentialFailures_RecordEveryFailureWithoutDuplicateSuppression()
    {
        using var scope = new TempDirectoryScope();
        EmergencyLogSink sink = CreateSink(scope);
        long failuresBefore = ProgramDiagnostics.FailureCount;

        bool first = ProgramDiagnostics.WriteFailure(
            Program.PendingArgsWriteStage,
            new IOException("channel locked"),
            "pending-args-write-failed",
            sink);
        bool second = ProgramDiagnostics.WriteFailure(
            Program.PendingArgsWriteStage,
            new IOException("channel locked again"),
            "pending-args-write-exception",
            sink);
        bool third = ProgramDiagnostics.WriteFailure(
            Program.ActivationDispatchStage,
            null,
            "activation-dispatch-failed",
            sink);

        first.Should().BeTrue();
        second.Should().BeTrue();
        third.Should().BeTrue();
        ProgramDiagnostics.FailureCount.Should().Be(failuresBefore + 3);
        string[] files = Directory.GetFiles(
            Path.Combine(scope.RootPath, "emergency"),
            EmergencyLogSink.FileSearchPattern);
        files.Should().HaveCount(3, "каждая независимая ошибка получает собственную аварийную запись");
        sink.DroppedCount.Should().Be(0, "бюджет аварийных записей не теряет реальные ошибки");
        List<string> crashIds = new();
        List<string> payloads = new();
        foreach (string file in files)
        {
            string payload = File.ReadAllText(file);
            new FileInfo(file).Length.Should().BeLessThanOrEqualTo(sink.MaxFileBytes);
            payload.Should().Contain(ProgramDiagnostics.FailureEventId);
            payloads.Add(payload);
            using JsonDocument document = JsonDocument.Parse(payload);
            crashIds.Add(document.RootElement.GetProperty("crashId").GetString()!);
        }

        crashIds.Should().OnlyHaveUniqueItems("идентификатор аварии уникален для каждой ошибки");
        crashIds.Should().AllSatisfy(id => Assert.IsTrue(
            id!.StartsWith(ProgramDiagnostics.CrashIdPrefix, StringComparison.Ordinal)));
        payloads.Should().Contain(payload => payload.Contains("pendingargswritefailed", StringComparison.Ordinal));
        payloads.Should().Contain(payload => payload.Contains("pendingargswriteexception", StringComparison.Ordinal));
        payloads.Should().Contain(payload => payload.Contains("activationdispatchfailed", StringComparison.Ordinal));
        payloads.Should().OnlyContain(payload => payload.Contains(ProgramDiagnostics.SessionMarker, StringComparison.Ordinal));
    }

    [TestMethod]
    public void ForwardToPrimaryInstance_FailedHandoff_ReturnsNonZeroWithoutRedirect()
    {
        long failuresBefore = ProgramDiagnostics.FailureCount;
        bool redirected = false;

        int writeFailed = Program.ForwardToPrimaryInstance(
            new[] { "KTools.App", "--script", "VideoEncoding" },
            _ => "pending-args-write-failed",
            () => redirected = true);
        int writeThrew = Program.ForwardToPrimaryInstance(
            new[] { "KTools.App" },
            _ => throw new IOException("channel unavailable"),
            () => redirected = true);
        int redirectedFailed = Program.ForwardToPrimaryInstance(
            new[] { "KTools.App" },
            _ => null,
            () => throw new InvalidOperationException("redirect failed"));
        int success = Program.ForwardToPrimaryInstance(
            new[] { "KTools.App" },
            _ => null,
            () => redirected = true);

        writeFailed.Should().NotBe(0, "неудачная передача аргументов не может завершаться кодом 0");
        writeFailed.Should().Be(Program.HandoffFailedExitCode);
        writeThrew.Should().Be(Program.HandoffFailedExitCode);
        redirectedFailed.Should().Be(Program.HandoffFailedExitCode);
        success.Should().Be(Program.SuccessExitCode);
        redirected.Should().BeTrue("перенаправление выполняется только после успешной записи аргументов");
        ProgramDiagnostics.FailureCount.Should().BeGreaterThanOrEqualTo(failuresBefore + 3);
    }

    [TestMethod]
    public void PendingArgsChannel_WritesBoundedDataFileAndRemovesExpiredOnes()
    {
        using var scope = new TempDirectoryScope();
        string directory = scope.GetFullPath("pending");
        Directory.CreateDirectory(directory);
        File.WriteAllText(scope.GetFullPath("blocked-channel"), "not a directory");

        bool written = PendingArgsChannel.TryWrite(
            directory,
            new[] { "KTools.App", "--script", "VideoEncoding", "C:\\media\\a.mkv" },
            out string? errorCode);
        string expired = Path.Combine(directory, "expired.txt");
        File.WriteAllLines(expired, new[] { "stale" });
        File.SetLastWriteTimeUtc(expired, DateTime.UtcNow - TimeSpan.FromHours(1));
        int pruned = PendingArgsChannel.Prune(directory, TimeSpan.FromMinutes(10), 256);
        IReadOnlyList<string> pending = PendingArgsChannel.EnumeratePendingFiles(directory, 256);

        written.Should().BeTrue();
        errorCode.Should().BeNull();
        File.Exists(expired).Should().BeFalse("просроченные файлы канала удаляются безопасно");
        pruned.Should().Be(1);
        pending.Should().ContainSingle();
        string[] lines = File.ReadAllLines(pending[0]);
        lines.Should().Contain("--script");
        lines.Should().Contain("VideoEncoding");

        bool failed = PendingArgsChannel.TryWrite(
            Path.Combine(scope.GetFullPath("blocked-channel"), "nested"),
            new[] { "KTools.App" },
            out string? failureCode);
        failed.Should().BeFalse();
        failureCode.Should().NotBeNullOrEmpty();
    }

    private static int EmergencyFileCount(TempDirectoryScope scope)
    {
        return Directory.GetFiles(
            Path.Combine(scope.RootPath, "emergency"),
            EmergencyLogSink.FileSearchPattern).Length;
    }

    private static JsonElement ReadJsonlEvent(string path, string eventId)
    {
        string[] lines;
        using (FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (StreamReader reader = new(stream, Encoding.UTF8))
        {
            lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }

        foreach (string line in lines)
        {
            using JsonDocument document = JsonDocument.Parse(line);
            if (string.Equals(
                document.RootElement.GetProperty("eventId").GetString(),
                eventId,
                StringComparison.Ordinal))
            {
                return document.RootElement.Clone();
            }
        }

        throw new InvalidOperationException("Событие " + eventId + " отсутствует в JSONL журнала");
    }

    private static EmergencyLogSink CreateSink(TempDirectoryScope scope)
    {
        LogServiceOptions options = new()
        {
            EmergencyDirectory = Path.Combine(scope.RootPath, "emergency"),
            MaxEmergencyFileBytes = 4096
        };
        Directory.CreateDirectory(options.EmergencyDirectory);
        return new EmergencyLogSink(options.EmergencyDirectory, options);
    }

    private static string FindAppFile(string fileName)
    {
        string? directory = AppContext.BaseDirectory;
        for (int index = 0; index < 8 && directory is not null; index++)
        {
            string candidate = Path.Combine(directory, "KTools.App", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new FileNotFoundException(fileName);
    }
}
