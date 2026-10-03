// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Models;
using KTools_App.Diagnostics;
using KTools_App.Infrastructure;
using KTools_App.Services.Contracts;
using KTools_App.Tests.TestHelpers;

namespace KTools_App.Tests.ProcessLayer;

[TestClass]
public class ProcessLayerTests
{
    private const string SentinelSecret = "SENTINEL-SECRET-TOKEN";    private const string SentinelUrl = "https://sentinel.example.com/watch?v=SENTINEL-QUERY-TOKEN";
    private const string SentinelCookie = "SENTINEL-COOKIE-VALUE";

    private static string ComSpec => Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";

    private sealed class TestRunner : AbstractProcessRunner
    {
        public TestRunner(ILogService logService, IPathManager pathManager)
            : base(logService, pathManager)
        {
        }

        public Task<ProcessResult> RunCmdAsync(
            string arguments,
            ProcessExecutionContext? context = null,
            string? expectedArtifact = null,
            int maxSuccessExitCode = 0,
            Func<bool>? verifyParseResult = null,
            CancellationToken cancellationToken = default)
        {
            return RunProcessAsync(
                "cmd",
                arguments,
                context: context,
                expectedArtifact: expectedArtifact,
                maxSuccessExitCode: maxSuccessExitCode,
                verifyParseResult: verifyParseResult,
                explicitBinaryPath: ComSpec,
                cancellationToken: cancellationToken);
        }
    }

    private static TestRunner CreateRunner(RecordingLogService log, IPathManager? pathManager = null)
    {
        return new TestRunner(log, pathManager ?? MockBuilders.CreatePathManagerMock().Object);
    }


    [TestMethod]
    public void Create_WithoutOperationId_ProducesSafeGeneratedToken()
    {
        ProcessExecutionContext context = ProcessExecutionContext.Create("ffmpeg");

        context.OperationId.Should().StartWith(ProcessExecutionContext.OperationPrefix);
        context.OperationId.Should().NotContainAny("=", "?", "/", "\\");
        context.Tool.Should().Be("ffmpeg");
        context.Attempt.Should().Be(ProcessExecutionContext.DefaultAttempt);
    }

    [TestMethod]
    public void Create_WithUnsafeOperationId_HashesInsteadOfLeaking()
    {
        ProcessExecutionContext context = ProcessExecutionContext.Create("yt-dlp", SentinelUrl);

        context.OperationId.Should().NotContain("SENTINEL");
        context.OperationId.Should().NotContain("://");
        context.OperationId.Should().NotContain("?");
    }

    [TestMethod]
    public void WithExpectedArtifact_KeepsOnlyFileName()
    {
        using var scope = new TempDirectoryScope();
        string full = scope.GetFullPath(Path.Combine("nested", "result.mkv"));

        ProcessExecutionContext context = ProcessExecutionContext.Create("mkvmerge")
            .WithExpectedArtifact(full);

        context.ExpectedArtifact.Should().Be("result.mkv");
        context.ExpectedArtifact.Should().NotContain("nested");
        context.HasExpectedArtifact.Should().BeTrue();
    }

    [TestMethod]
    public void NormalizeToolName_StripsExtensionAndPath()
    {
        ProcessExecutionContext.NormalizeToolName(@"C:\bin\kt-ffmpeg.exe").Should().Be("kt-ffmpeg");
        ProcessExecutionContext.NormalizeToolName("dee").Should().Be("dee");
        ProcessExecutionContext.NormalizeToolName("   ").Should().Be(ProcessExecutionContext.ToolFallback);
    }

    [TestMethod]
    public void CountArguments_CountsQuotedAndBareTokens()
    {
        ProcessExecutionContext.CountArguments("-y \"C:\\a b\\in.mkv\" -c copy").Should().Be(4);
        ProcessExecutionContext.CountArguments("   ").Should().Be(0);
        ProcessExecutionContext.CountArguments(null).Should().Be(0);
    }

    [TestMethod]
    public void HashArguments_IsStableAndHidesContent()
    {
        string first = ProcessExecutionContext.HashArguments("--cookie " + SentinelCookie);
        string second = ProcessExecutionContext.HashArguments("--cookie " + SentinelCookie);
        string other = ProcessExecutionContext.HashArguments("--cookie other");

        first.Should().Be(second);
        first.Should().NotBe(other);
        first.Should().NotContain("SENTINEL");
        ProcessExecutionContext.HashArguments(string.Empty).Should().Be("empty");
    }

    [TestMethod]
    public void LabelDirectory_ProducesStableOpaqueLabel()
    {
        string label = ProcessExecutionContext.LabelDirectory(@"C:\Users\sentinel\Videos");

        label.Should().StartWith("wd-");
        label.Should().NotContain("sentinel");
        label.Should().Be(ProcessExecutionContext.LabelDirectory(@"C:\Users\sentinel\Videos"));
        ProcessExecutionContext.LabelDirectory(null).Should().Be("default");
    }


    [TestMethod]
    public void ResolveEncoding_IsUtf8WithoutBomAndNeverThrows()
    {
        System.Text.Encoding encoding = ProcessOutputPolicy.ResolveEncoding();

        encoding.WebName.Should().Be("utf-8");
        encoding.GetPreamble().Should().BeEmpty();
        Action act = () => encoding.GetString(new byte[] { 0x41, 0xFF, 0x42 });
        act.Should().NotThrow("некорректные байты не должны прерывать чтение вывода");
    }

    [TestMethod]
    public void OutputBuffer_KeepsTailAndMarksTruncation()
    {
        ProcessOutputBuffer buffer = new(maxLines: 4, maxBytes: 4096);
        for (int index = 0; index < 20; index++)
        {
            buffer.Append("строка " + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        buffer.RetainedLines.Should().Be(4);
        buffer.DroppedLines.Should().Be(16);
        buffer.Truncated.Should().BeTrue();
        buffer.Snapshot().Last().Should().Be("строка 19");
    }

    [TestMethod]
    public void OutputBuffer_ByteLimitTruncatesSingleLine()
    {
        ProcessOutputBuffer buffer = new(maxLines: 8, maxBytes: 256);
        buffer.Append(new string('X', 4000));

        buffer.Truncated.Should().BeTrue();
        buffer.DroppedBytes.Should().BeGreaterThan(0);
        buffer.BuildTail().Length.Should().BeLessThan(4000);
    }

    [TestMethod]
    public void OutputBuffer_TailIsRedacted()
    {
        ProcessOutputBuffer buffer = new();
        buffer.Append("download " + SentinelUrl);
        buffer.Append("cookie: " + SentinelCookie);

        string tail = buffer.BuildTail();

        tail.Should().NotContain("SENTINEL-QUERY-TOKEN");
        tail.Should().NotContain(SentinelCookie);
        tail.Should().Contain("sentinel.example.com");
    }

    [TestMethod]
    public void NormalizeLine_CollapsesControlCharactersAndBoundsLength()
    {
        ProcessOutputPolicy.NormalizeLine("первая\r\nвторая\tтретья").Should().Be("первая вторая третья");
        ProcessOutputPolicy.NormalizeLine(new string('y', 9000)).Length.Should().BeLessThan(ProcessOutputPolicy.MaxSingleLineLength + 64);
    }

    [TestMethod]
    public void CountWarnings_DetectsWarningLinesOnly()
    {
        List<string> lines = new()
        {
            "Warning: track 1 missing",
            "warning: truncated",
            "Error: cannot open",
            "обычная строка"
        };

        ProcessOutputPolicy.CountWarnings(lines).Should().Be(2);
        ProcessOutputPolicy.CountErrors(lines).Should().Be(1);
    }

    [TestMethod]
    public void RateLimiter_BoundsAcceptedEventsPerWindow()
    {
        ProcessOutputRateLimiter limiter = new(maxSamples: 3, windowMilliseconds: 60000);

        for (int index = 0; index < 20; index++)
        {
            limiter.TryAcquire();
        }

        limiter.SuppressedCount.Should().Be(17);
    }


    [TestMethod]
    public void Result_ExitCodeZeroWithoutArtifact_IsFailure()
    {
        ProcessExecutionContext context = ProcessExecutionContext.Create("ffmpeg");

        ProcessResult raw = ProcessResult.Succeeded(context, "proc-1", 100, 0, 12.5, outputExists: false);

        raw.OutputExists.Should().BeFalse("фабрика фиксирует наблюдаемый факт, решение принимает вызывающий слой");
        raw.OutputExists.Should().NotBe(true);
    }

    [TestMethod]
    public void Result_MapsStatusToExecutionStatus()
    {
        ProcessExecutionContext context = ProcessExecutionContext.Create("ffmpeg");

        ProcessResult.Cancelled(context, "отмена").ToExecutionStatus().Should().Be(ExecutionStatus.Cancelled);
        ProcessResult.Failed(context, "e", "m").ToExecutionStatus().Should().Be(ExecutionStatus.Failed);
        ProcessResult.Succeeded(context).ToExecutionStatus().Should().Be(ExecutionStatus.Succeeded);
    }

    [TestMethod]
    public void Result_NeverExposesNegativeExitCode()
    {
        ProcessExecutionContext context = ProcessExecutionContext.Create("ffmpeg");

        ProcessResult result = ProcessResult.Failed(context, "e", "m", exitCode: -3);

        result.ExitCode.Should().BeNull("отрицательный код возврата не является кодом процесса");
    }

    [TestMethod]
    public void Result_ToLogProperties_ContainsOnlyAllowlistedSafeFields()
    {
        ProcessExecutionContext context = ProcessExecutionContext.Create("ffmpeg", "op-1", "item-1");
        ProcessResult result = ProcessResult.Failed(
            context,
            "nonzero-exit",
            "сбой",
            "proc-1",
            4242,
            3,
            100.5,
            false,
            "хвост",
            true,
            2,
            1);

        Dictionary<string, object?> properties = result.ToLogProperties();

        properties.Should().ContainKey("OperationId");
        properties.Should().ContainKey("ProcessId");
        properties.Should().ContainKey("Pid");
        properties.Should().ContainKey("ExitCode");
        properties.Should().ContainKey("DurationMs");
        properties.Should().ContainKey("OutputExists");
        properties.Should().ContainKey("WarningCount");
        properties.Should().ContainKey("OutputTruncated");
        properties.Should().ContainKey("ErrorCode");
        foreach (string key in properties.Keys)
        {
            LogRedactor.Default.IsAllowedPropertyKey(key).Should().BeTrue(
                "свойство '{0}' должно быть в allowlist редактора", key);
        }
    }

    [TestMethod]
    public void Result_CompatibilityConstructor_PreservesLegacySemantics()
    {
        new ProcessResult(true, 0, "ок").IsSuccess.Should().BeTrue();
        ProcessResult failed = new(false, 1, "сбой");
        failed.IsSuccess.Should().BeFalse();
        failed.ExitCode.Should().Be(1);
    }

    [TestMethod]
    public void Result_ToSummary_HasNoPathsAndNoSecrets()
    {
        ProcessExecutionContext context = ProcessExecutionContext.Create("yt-dlp");
        ProcessResult result = ProcessResult.Failed(context, "e", "m", exitCode: 2, outputTail: SentinelUrl);

        string summary = result.ToSummary();

        summary.Should().Contain("yt-dlp");
        summary.Should().NotContain("SENTINEL");
        summary.Should().NotContain(@"C:\");
    }


    [TestMethod]
    public async Task Run_ExitCodeZero_ProducesStartAndExitEventsWithCorrelation()
    {
        using var logScope = new TempDirectoryScope();
        var log = new RecordingLogService();
        TestRunner runner = CreateRunner(log);

        ProcessResult result = await runner.RunCmdAsync("/c echo готовность");

        result.IsSuccess.Should().BeTrue();
        result.ExitCode.Should().Be(0);
        result.Pid.Should().BeGreaterThan(0);
        result.ProcessId.Should().NotBeNullOrWhiteSpace();
        result.ProcessId.Should().NotBe(result.Pid!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        RecordedLogEvent started = log.EventsById(ProcessEventIds.Started).Single();
        started.Status.Should().Be(LogStatus.Running);
        started.GetProperty<string>("Tool").Should().Be("cmd");
        started.GetProperty<int>("Pid").Should().Be(result.Pid);

        RecordedLogEvent exit = log.EventsById(ProcessEventIds.Exit).Single();
        exit.Status.Should().Be(LogStatus.Succeeded);
        exit.GetProperty<int>("ExitCode").Should().Be(0);
        exit.GetProperty<double>("DurationMs").Should().BeGreaterThan(0);
        exit.GetProperty<int>("ArgumentCount").Should().BeGreaterThan(0);
        exit.GetProperty<string>("WorkingDirLabel").Should().NotBeNullOrWhiteSpace();
    }

    [TestMethod]
    public async Task Run_MissingBinary_IsTypedFailureWithoutProcessId()
    {
        var log = new RecordingLogService();
        var runner = new DirectProcessRunner(log);

        ProcessResult result = await runner.RunAsync(
            Path.Combine(Path.GetTempPath(), "definitely-missing-" + Guid.NewGuid().ToString("N") + ".exe"),
            "missing-tool",
            "-x",
            ProcessExecutionContext.NewOperation("missing-tool"));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ProcessResult.ErrorBinaryMissing);
        result.Pid.Should().BeNull();
        result.ExitCode.Should().BeNull();
        log.EventsById(ProcessEventIds.StartFailed).Should().ContainSingle();
    }

    [TestMethod]
    public async Task Run_NonZeroExit_IsFailureWithExitCodeAndBoundedTail()
    {
        var log = new RecordingLogService();
        TestRunner runner = CreateRunner(log);

        ProcessResult result = await runner.RunCmdAsync("/c echo stderr-marker 1>&2 & exit 3");

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ProcessResult.ErrorNonZeroExit);
        result.ExitCode.Should().Be(3);
        result.Status.Should().Be(LogStatus.Failed);
        result.OutputTail.Should().Contain("stderr-marker");
        RecordedLogEvent exit = log.EventsById(ProcessEventIds.Exit).Single();
        exit.Level.Should().Be(LogLevel.Error);
    }

    [TestMethod]
    public async Task Run_ExitZeroButExpectedArtifactMissing_IsArtifactFailure()
    {
        using var scope = new TempDirectoryScope();
        var log = new RecordingLogService();
        TestRunner runner = CreateRunner(log);
        string expected = scope.GetFullPath("never-created.mkv");

        ProcessResult result = await runner.RunCmdAsync("/c echo ок", expectedArtifact: expected);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ProcessResult.ErrorArtifactMissing);
        result.OutputExists.Should().BeFalse();
    }

    [TestMethod]
    public async Task Run_ExitZeroWithArtifact_VerifiesPostcondition()
    {
        using var scope = new TempDirectoryScope();
        string expected = scope.CreateFile("produced.bin", "данные");
        var log = new RecordingLogService();
        TestRunner runner = CreateRunner(log);

        ProcessResult result = await runner.RunCmdAsync("/c echo ок", expectedArtifact: expected);

        result.IsSuccess.Should().BeTrue();
        result.OutputExists.Should().BeTrue();
        result.ExpectedArtifact.Should().Be("produced.bin");
    }

    [TestMethod]
    public async Task Run_MalformedStdout_FailsParsePostcondition()
    {
        var log = new RecordingLogService();
        TestRunner runner = CreateRunner(log);
        bool parsed = false;

        ProcessResult result = await runner.RunCmdAsync(
            "/c echo {не-json",
            verifyParseResult: () => parsed);

        result.IsSuccess.Should().BeFalse("неразобранный вывод не подтверждает успех");
        result.ErrorCode.Should().Be(ProcessResult.ErrorParseInvalid);
    }

    [TestMethod]
    public async Task Run_ParsePostconditionSatisfied_Succeeds()
    {
        var log = new RecordingLogService();
        TestRunner runner = CreateRunner(log);
        bool parsed = false;

        ProcessResult result = await runner.RunCmdAsync(
            "/c echo {\"ok\":true}",
            verifyParseResult: () => parsed = true);

        result.IsSuccess.Should().BeTrue();
        parsed.Should().BeTrue();
    }

    [TestMethod]
    public async Task Run_WarningExitCode_IsPartialSuccess()
    {
        var log = new RecordingLogService();
        TestRunner runner = CreateRunner(log);

        ProcessResult result = await runner.RunCmdAsync("/c echo сработало & exit 1", maxSuccessExitCode: 1);

        result.IsSuccess.Should().BeTrue("код возврата 1 у mkvmerge/eac3to означает успех с предупреждениями");
        result.IsPartiallySucceeded.Should().BeTrue();
        result.IsCleanSuccess.Should().BeFalse();
        result.Status.Should().Be(LogStatus.PartiallySucceeded);
        result.ExitCode.Should().Be(1);
    }

    [TestMethod]
    public async Task Run_WarningLineWithoutError_StaysSuccessfulWithWarningCount()
    {
        var log = new RecordingLogService();
        TestRunner runner = CreateRunner(log);

        ProcessResult result = await runner.RunCmdAsync("/c echo warning: deprecated option 1>&2 & exit 0");

        result.IsSuccess.Should().BeTrue("строка предупреждения не должна превращать успех в ошибку");
        result.IsPartiallySucceeded.Should().BeTrue();
        result.WarningCount.Should().BeGreaterThan(0);
        result.IsFailed.Should().BeFalse();
    }

    [TestMethod]
    public async Task Run_Cancellation_ProducesCancelledStatusWithVerifiedTermination()
    {
        var log = new RecordingLogService();
        TestRunner runner = CreateRunner(log);
        using var cts = new CancellationTokenSource();

        Task<ProcessResult> run = runner.RunCmdAsync("/c ping -n 30 127.0.0.1 > nul", cancellationToken: cts.Token);
        await Task.Delay(300);
        cts.Cancel();

        ProcessResult result = await run;

        result.IsCancelled.Should().BeTrue();
        result.Status.Should().Be(LogStatus.Cancelled);
        result.TerminationVerified.Should().BeTrue("факт выхода доказывается ожиданием, а не только вызовом Kill");
        result.ErrorCode.Should().Be(ProcessResult.ErrorCancelled);

        IReadOnlyList<RecordedLogEvent> events = log.EventsById(ProcessEventIds.Cancelled);
        events.Should().ContainSingle();
        events[0].Level.Should().Be(LogLevel.Info, "штатная отмена не является предупреждением");
        events[0].Status.Should().Be(LogStatus.Cancelled);
    }

    [TestMethod]
    public async Task Run_ForeignCancellationRequest_IsCancelledNotFailed()
    {
        var log = new RecordingLogService();
        var runner = new DirectProcessRunner(log);
        bool cancelRequested = false;

        ProcessResult result = await runner.RunAsync(
            ComSpec,
            "cmd",
            "/c ping -n 30 127.0.0.1 > nul",
            ProcessExecutionContext.NewOperation("cmd"),
            isCancellationRequested: () => cancelRequested);

        result.IsSuccess.Should().BeTrue("без внешнего запроса отмены процесс завершается штатно");
        cancelRequested.Should().BeFalse();
    }

    [TestMethod]
    public async Task Run_LogsCommandLineAndMetrics()
    {
        var log = new RecordingLogService();
        TestRunner runner = CreateRunner(log);

        await runner.RunCmdAsync("/c echo " + SentinelSecret + " " + SentinelUrl);

        IReadOnlyList<RecordedLogEvent> launchEvents = log.Events
            .Where(e => e.EventId == ProcessEventIds.Started || e.EventId == ProcessEventIds.Launched)
            .ToList();

        launchEvents.Should().NotBeEmpty();
        foreach (RecordedLogEvent evt in launchEvents)
        {
            evt.GetProperty<string>("CommandLine").Should().Contain("cmd");
            evt.GetProperty<string>("CommandLine").Should().Contain("/c echo");
            evt.GetProperty<int>("ArgumentCount").Should().BeGreaterThan(0);
        }
    }

    [TestMethod]
    public async Task Run_LargeStdout_IsBoundedAndTruncationIsObservable()
    {
        var log = new RecordingLogService();
        TestRunner runner = CreateRunner(log);

        ProcessResult result = await runner.RunCmdAsync(
            "/c for /L %i in (1,1,4000) do @echo marker-%i");

        result.OutputTail.Should().NotBeNull();
        result.OutputTail!.Length.Should().BeLessThanOrEqualTo(ProcessOutputPolicy.DefaultMaxTailBytes);
        result.OutputTruncated.Should().BeTrue("хвост ограничен и усечение наблюдаемо");
    }

    [TestMethod]
    public async Task Run_OutputIsNotLoggedLineByLine()
    {
        var log = new RecordingLogService();
        TestRunner runner = CreateRunner(log);

        await runner.RunCmdAsync("/c for /L %i in (1,1,200) do @echo output-line-%i");

        log.Events.Should().NotContain(e => e.EventId == ProcessEventIds.OutputSampled,
            "вывод процесса не превращается в построчный журнал");
    }

    [TestMethod]
    public async Task DirectRunner_ScriptCancel_ProducesCancelledResult()
    {
        var log = new RecordingLogService();
        var runner = new DirectProcessRunner(log);
        bool cancelRequested = false;

        Task<ProcessResult> run = runner.RunAsync(
            ComSpec,
            "cmd",
            "/c ping -n 30 127.0.0.1 > nul",
            ProcessExecutionContext.NewOperation("cmd"),
            isCancellationRequested: () => cancelRequested,
            workingDir: null);

        await Task.Delay(400);
        cancelRequested = true;

        ProcessResult result = await run;
        result.IsCancelled.Should().BeTrue("внешний признак отмены сценария останавливает процесс");
        result.TerminationVerified.Should().BeTrue();
        result.ErrorCode.Should().Be(ProcessResult.ErrorCancelled);

        // Событие об отмене обязано нести CleanupState: по нему видно, что после отмены
        // не осталось ни процесса, ни артефактов. Иначе Warning+Cancelled остаётся
        // неинтерпретируемым, а это самая частая тревожная запись в журнале.
        RecordedLogEvent cancellation = log.Events
            .Where(e => e.EventId is ProcessEventIds.Cancelled or ProcessEventIds.TerminationUnverified)
            .Should().ContainSingle("событие отмены пишется ровно один раз").Which;
        cancellation.Status.Should().Be(LogStatus.Cancelled);
        cancellation.Level.Should().Be(
            result.TerminationVerified ? KTools_App.Core.LogLevel.Info : KTools_App.Core.LogLevel.Warning);
        cancellation.Properties.Should().ContainKey("CleanupState");
        cancellation.GetProperty<string>("CleanupState").Should().Be(
            result.CleanupState.ToString(),
            "журнал и типизированный результат обязаны сообщать одно и то же состояние уборки");
        cancellation.Properties.Should().ContainKey("ErrorCode");
    }

    /// <summary>
    /// Регрессия: строка длиннее <see cref="ProcessOutputPolicy.MaxSingleLineLength"/>
    /// обрезалась до передачи потребителю, и разбор JSON (mkvmerge --identify)
    /// падал с «не удалось разобрать JSON». Потребитель обязан получать исходную
    /// строку, тогда как буфер и журнал — ограниченную версию.
    /// </summary>
    [TestMethod]
    public void NormalizeLine_BoundsLoggedLine_ButRawLineIsNotTruncated()
    {
        // Arrange — строка заведомо длиннее лимита журнала
        string longLine = "  \"name\": \"" + new string('x', ProcessOutputPolicy.MaxSingleLineLength * 2) + "\",";

        // Act — то, что получает буфер и журнал
        string normalized = ProcessOutputPolicy.NormalizeLine(longLine);

        // Assert
        normalized.Length.Should().BeLessThanOrEqualTo(
            ProcessOutputPolicy.MaxSingleLineLength,
            "в журнал и хвост уходит ограниченная версия строки");
        longLine.Length.Should().BeGreaterThan(
            ProcessOutputPolicy.MaxSingleLineLength,
            "исходная строка длиннее лимита, иначе тест ничего не проверяет");
        longLine.Should().Contain("  \"name\"", "исходная строка сохраняет отступы JSON без нормализации");
    }
}
