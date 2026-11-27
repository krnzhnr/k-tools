// -*- coding: utf-8 -*-
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Infrastructure;
using KTools_App.Models;
using KTools_App.Services.Contracts;
using KTools_App.Tests.TestHelpers;

namespace KTools_App.Tests.ProcessLayer;

/// <summary>
/// Проверки общего таймаута внешних процессов (issue #110):
/// висящий процесс обязан завершаться принудительно с кодом timeout-exceeded,
/// а не ждать вечно.
/// </summary>
[TestClass]
public class ProcessTimeoutTests
{
    private static string ComSpec => Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";

    private sealed class TimeoutTestRunner : AbstractProcessRunner
    {
        public TimeoutTestRunner(ILogService logService, IPathManager pathManager)
            : base(logService, pathManager)
        {
        }

        public Task<ProcessResult> RunCmdAsync(
            string arguments,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            return RunProcessAsync(
                "cmd",
                arguments,
                explicitBinaryPath: ComSpec,
                context: ProcessExecutionContext.NewOperation("cmd"),
                timeout: timeout,
                cancellationToken: cancellationToken);
        }
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task Run_HangingProcessExceedingTimeout_TerminatesWithTimeoutExceeded()
    {
        // Arrange — процесс висит около 30 секунд, лимит — 2 секунды
        var log = new RecordingLogService();
        var runner = new DirectProcessRunner(log);

        // Act
        ProcessResult result = await runner.RunAsync(
            ComSpec,
            "cmd",
            "/c ping -n 30 127.0.0.1 > nul",
            ProcessExecutionContext.NewOperation("cmd"),
            timeout: TimeSpan.FromSeconds(2));

        // Assert — ожидание ограничено таймаутом, процесс доказанно завершён
        result.IsCancelled.Should().BeTrue("превышение лимита времени останавливает висящий процесс");
        result.ErrorCode.Should().Be(ProcessResult.ErrorTimeoutExceeded);
        result.TerminationVerified.Should().BeTrue("факт выхода доказывается ожиданием, а не только вызовом Kill");
        result.DurationMs.Should().BeLessThan(30000, "результат возвращается вскоре после лимита, а не вместе с висящим процессом");

        RecordedLogEvent timeoutEvent = log.EventsById(ProcessEventIds.TimeoutExceeded).Should().ContainSingle(
            "превышение таймаута пишется отдельным событием, а не теряется среди отмен").Which;
        timeoutEvent.Status.Should().Be(LogStatus.Cancelled);
        timeoutEvent.Level.Should().Be(LogLevel.Warning, "срыв лимита времени — аномалия, а не штатная отмена");
        timeoutEvent.Properties.Should().ContainKey("CleanupState");
        timeoutEvent.GetProperty<string>("ErrorCode").Should().Be(ProcessResult.ErrorTimeoutExceeded);
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task RunProcess_HangingProcessExceedingTimeout_TerminatesWithTimeoutExceeded()
    {
        // Arrange
        var log = new RecordingLogService();
        var runner = new TimeoutTestRunner(log, MockBuilders.CreatePathManagerMock().Object);

        // Act
        ProcessResult result = await runner.RunCmdAsync(
            "/c ping -n 30 127.0.0.1 > nul",
            timeout: TimeSpan.FromSeconds(2));

        // Assert
        result.IsCancelled.Should().BeTrue();
        result.ErrorCode.Should().Be(ProcessResult.ErrorTimeoutExceeded);
        result.TerminationVerified.Should().BeTrue();
        result.DurationMs.Should().BeLessThan(30000);
        log.EventsById(ProcessEventIds.TimeoutExceeded).Should().ContainSingle();
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task Run_ProcessCompletingBeforeTimeout_SucceedsNormally()
    {
        // Arrange — быстрый процесс с щедрым лимитом: гонка не должна ломать штатный путь
        var log = new RecordingLogService();
        var runner = new DirectProcessRunner(log);

        // Act
        ProcessResult result = await runner.RunAsync(
            ComSpec,
            "cmd",
            "/c echo ok",
            ProcessExecutionContext.NewOperation("cmd"),
            timeout: TimeSpan.FromSeconds(60));

        // Assert
        result.IsCleanSuccess.Should().BeTrue("уложившийся в лимит процесс завершается обычным успехом");
        result.ErrorCode.Should().BeNull();
        log.EventsById(ProcessEventIds.TimeoutExceeded).Should().BeEmpty("без срыва лимита событие таймаута не пишется");
    }
}
