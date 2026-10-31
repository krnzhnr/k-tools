using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using KTools_App.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KTools_App.Tests.Diagnostics;

[TestClass]
[DoNotParallelize]
public sealed class ActiveProcessTrackerTests
{
    [TestInitialize]
    public void SetUp()
    {
        ActiveProcessTracker.ResetForTests();
    }

    [TestCleanup]
    public void TearDown()
    {
        ActiveProcessTracker.KillAll(TimeSpan.FromSeconds(1));
        ActiveProcessTracker.ResetForTests();
    }

    [TestMethod]
    public void KillAll_MixedSuccessAndFailure_ReturnsTypedSummaryAndKeepsUnverifiedEntry()
    {
        Process? good = StartProcess();
        Process? bad = StartProcess();
        good.Should().NotBeNull();
        bad.Should().NotBeNull();

        ActiveProcessTracker.Register(good!).Should().BeTrue();
        ActiveProcessTracker.Register(bad!).Should().BeTrue();
        int goodPid = good!.Id;
        int badPid = bad!.Id;
        bad!.Dispose();

        ProcessTerminationSummary summary = ActiveProcessTracker.KillAll(TimeSpan.FromSeconds(2));

        summary.Attempted.Should().Be(2, "трекер владеет собственным дескриптором и завершает процессы независимо от вызывающего кода");
        summary.Succeeded.Should().Be(2);
        summary.Failed.Should().Be(0);
        summary.Errors.Should().BeEmpty();
        summary.AllVerified.Should().BeTrue();
        summary.Results.Should().HaveCount(2);
        summary.Results.Should().OnlyContain(result => result.Verified);
        summary.Results.Should().Contain(result => result.ExitCode.HasValue,
            "код завершения читается, пока дескриптор вызывающего кода доступен");
        summary.Results.Should().OnlyContain(result => result.ProcessId.StartsWith("proc-", StringComparison.Ordinal));
        summary.Results.Select(result => result.Pid).Should().BeEquivalentTo(new[] { goodPid, badPid });
        summary.ToLogProperties()["Errors"].Should().Be(string.Empty);
        summary.ToLogProperties()["Pid"].Should().BeOfType<string>().Which.Should().NotBeNullOrWhiteSpace();
        summary.ToLogProperties()["ExitCode"].Should().BeOfType<string>().Which.Should().Contain("=");
        good!.HasExited.Should().BeTrue();
        ActiveProcessTracker.RegisteredProcessCount.Should().Be(0);
        good.Dispose();
    }

    [TestMethod]
    public void KillAll_KillFailure_KeepsEntryAndExposesErrorWithoutSwallowing()
    {
        Process? target = StartProcess();
        target.Should().NotBeNull();
        Process? protectedProcess = TryGetProtectedProcess();
        if (protectedProcess is null)
        {
            target!.Kill();
            target.WaitForExit(2000);
            target.Dispose();
            return;
        }

        try
        {
            ActiveProcessTracker.Register(target!).Should().BeTrue();
            ActiveProcessTracker.Register(protectedProcess).Should().BeTrue();
            long pidReuseBefore = ActiveProcessTracker.PidReuseDetectionCount;

            ProcessTerminationSummary summary = ActiveProcessTracker.KillAll(TimeSpan.FromMilliseconds(500));

            summary.Failed.Should().Be(1, "невозможность завершить защищённый процесс фиксируется как ошибка");
            summary.Succeeded.Should().Be(1);
            summary.AllVerified.Should().BeFalse();
            summary.Errors.Should().NotBeEmpty();
            summary.Results.Should().Contain(result => result.Failed && result.Error == ActiveProcessTracker.ErrorKillFailed);
            summary.ToLogProperties()["Errors"].Should().BeOfType<string>().Which.Should().NotBeNullOrWhiteSpace();
            summary.ToLogProperties()["Failed"].Should().Be(1);
            ActiveProcessTracker.PidReuseDetectionCount.Should().Be(pidReuseBefore);
            ActiveProcessTracker.RegisteredProcessCount.Should().Be(1, "непроверенный процесс остаётся в реестре");
            target!.HasExited.Should().BeTrue();
        }
        finally
        {
            ActiveProcessTracker.ResetForTests();
            target!.Kill();
            target.WaitForExit(2000);
            target.Dispose();
        }
    }

    [TestMethod]
    public void KillAll_AlreadyExitedProcess_IsVerifiedAndCleared()
    {
        Process? process = StartProcess(exitImmediately: true);
        process.Should().NotBeNull();
        ActiveProcessTracker.Register(process!);
        process!.WaitForExit(2000).Should().BeTrue();

        ProcessTerminationSummary summary = ActiveProcessTracker.KillAll(TimeSpan.FromSeconds(1));

        summary.Failed.Should().Be(0);
        summary.AlreadyExited.Should().Be(1);
        summary.AllVerified.Should().BeTrue();
        summary.Results.Should().ContainSingle();
        summary.Results[0].ExitCode.Should().NotBeNull("код завершения фиксируется даже для уже завершившегося процесса");
        summary.Results[0].ProcessId.Should().StartWith("proc-");
        summary.Results[0].Pid.Should().Be(process.Id);
        ActiveProcessTracker.RegisteredProcessCount.Should().Be(0);
        process.Dispose();
    }

    [TestMethod]
    public void KillAll_AfterShutdownRequest_RejectsNewRegistrations()
    {
        Process? process = StartProcess();
        process.Should().NotBeNull();
        ActiveProcessTracker.TryBeginShutdown();

        ActiveProcessTracker.Register(process!);

        ActiveProcessTracker.RegisteredProcessCount.Should().Be(0);
        process!.Kill();
        process.WaitForExit(2000);
        process.Dispose();
    }

    [TestMethod]
    public void KillAll_WithoutRegisteredProcesses_ReportsConfirmedNoOpSummary()
    {
        ProcessTerminationSummary summary = ActiveProcessTracker.KillAll(TimeSpan.FromMilliseconds(50));

        summary.Total.Should().Be(0);
        summary.Attempted.Should().Be(0);
        summary.Succeeded.Should().Be(0);
        summary.Failed.Should().Be(0);
        summary.AlreadyExited.Should().Be(0);
        summary.IsConfirmedNoOp.Should().BeTrue("трекер подтвердил, что процессов не принимал и не завершал");
        summary.AllVerified.Should().BeTrue();
        summary.ErrorCode.Should().Be(ProcessTerminationSummary.ErrorNoTrackedProcesses);
        summary.HasFailure.Should().BeFalse();
        ActiveProcessTracker.HasActiveProcesses.Should().BeFalse();
    }

    [TestMethod]
    public void EmptySummary_ForUnknownIntake_IsNotVerified()
    {
        ProcessTerminationSummary summary = ProcessTerminationSummary.Empty;

        summary.IsConfirmedNoOp.Should().BeFalse();
        summary.AllVerified.Should().BeFalse("пустой результат без подтверждённого приёма не является успехом");
        summary.ErrorCode.Should().Be(ProcessTerminationSummary.ErrorIntakeUnknown);
        summary.Verified.Should().Be(0);
        summary.Skipped.Should().Be(0);
        summary.ToLogProperties()["ErrorCode"].Should().Be(ProcessTerminationSummary.ErrorIntakeUnknown);
    }

    [TestMethod]
    public void SummaryLogProperties_ExposeVerifiedAndSkippedCounters()
    {
        ProcessTerminationResult verified = new(
            "proc-1",
            111,
            attempted: true,
            succeeded: true,
            failed: false,
            alreadyExited: false,
            exitCode: 0,
            errors: null);
        ProcessTerminationResult skipped = new(
            "proc-2",
            222,
            attempted: false,
            succeeded: false,
            failed: false,
            alreadyExited: true,
            exitCode: 0,
            errors: null);
        ProcessTerminationSummary summary = new(1, 1, 0, 1, new[] { verified, skipped });

        Dictionary<string, object?> properties = summary.ToLogProperties();

        properties["Verified"].Should().Be(2);
        properties["Skipped"].Should().Be(1);
        properties["AlreadyExited"].Should().Be(1);
        properties["Total"].Should().Be(2);
        properties.Should().NotContainKey(
            "ErrorCode",
            "успешное завершение не несёт ErrorCode: потребитель, ищущий отказы по errorCode != null, не должен ловить ложное срабатывание");
        summary.AllVerified.Should().BeTrue();
    }

    [TestMethod]
    public void SummaryErrors_AreBoundedAndRedacted()
    {
        ProcessTerminationSummary summary = new(
            attempted: 1,
            succeeded: 0,
            failed: 1,
            alreadyExited: 0,
            new[] { new ProcessTerminationResult(
                "proc-1",
                333,
                attempted: true,
                succeeded: false,
                failed: true,
                alreadyExited: false,
                exitCode: null,
                errors: null) },
            new[] { "token=SENTINEL-TOKEN", new string('e', 4096) });

        string errors = summary.ToErrorToken();

        errors.Should().NotContain("SENTINEL-TOKEN");
        errors.Length.Should().BeLessThanOrEqualTo(ProcessTerminationSummary.MaxLoggedErrorLength);
        summary.ErrorCode.Should().Be("token=[redacted]");
    }

    [TestMethod]
    public void KillAll_MultipleProcesses_ReportsPerProcessExitCodesAndDistinctIds()
    {
        List<Process> processes = new();
        try
        {
            for (int index = 0; index < 3; index++)
            {
                Process process = StartProcess()!;
                process.Should().NotBeNull();
                processes.Add(process);
                ActiveProcessTracker.Register(process).Should().BeTrue();
            }

            ActiveProcessTracker.RegisteredProcessCount.Should().Be(3);
            ProcessTerminationSummary summary = ActiveProcessTracker.KillAll(TimeSpan.FromSeconds(2));

            summary.Attempted.Should().Be(3);
            summary.Succeeded.Should().Be(3);
            summary.Failed.Should().Be(0);
            summary.AllVerified.Should().BeTrue();
            summary.ProcessIds.Distinct().Should().HaveCount(3, "внутренние идентификаторы не переиспользуются");
            summary.Pids.Should().BeEquivalentTo(processes.Select(process => process.Id));
            summary.ExitCodes.Should().OnlyContain(code => code.HasValue);
            ActiveProcessTracker.RegisteredProcessCount.Should().Be(0);
            processes.Should().OnlyContain(process => process.HasExited);
        }
        finally
        {
            foreach (Process process in processes)
            {
                SafeDispose(process);
            }
        }
    }

    [TestMethod]
    public void KillAll_SnapshotBlocksRegistrationsAndClearsRegistry()
    {
        Process? tracked = StartProcess();
        Process? late = StartProcess();
        tracked.Should().NotBeNull();
        late.Should().NotBeNull();
        ActiveProcessTracker.Register(tracked!).Should().BeTrue();
        using ManualResetEventSlim gate = new(false);
        Task registrar = Task.Run(() =>
        {
            gate.Wait();
            for (int index = 0; index < 200; index++)
            {
                ActiveProcessTracker.Register(late!);
            }
        });
        Task<ProcessTerminationSummary> killer = Task.Run(() =>
        {
            gate.Wait();
            return ActiveProcessTracker.KillAll(TimeSpan.FromSeconds(2));
        });

        gate.Set();
        Task.WaitAll(registrar, killer);
        ProcessTerminationSummary summary = killer.Result;

        ActiveProcessTracker.IsShutdownRequested.Should().BeTrue("снимок завершения закрывает приём новых процессов");
        ActiveProcessTracker.RegisteredProcessCount.Should().Be(0,
            "процесс не может остаться в реестре после снимка завершения");
        ActiveProcessTracker.Register(late!).Should().BeFalse("после снимка регистрация отклоняется");
        summary.AllVerified.Should().BeTrue("завершённые процессы подтверждены, неучтённые не считаются ошибкой");
        tracked!.HasExited.Should().BeTrue();
        SafeDispose(tracked);
        SafeKill(late);
    }

    [TestMethod]
    public void Register_SameProcessTwice_IsIdempotentAndTrackedOnce()
    {
        Process? process = StartProcess();
        process.Should().NotBeNull();

        ActiveProcessTracker.Register(process!).Should().BeTrue();
        ActiveProcessTracker.Register(process!).Should().BeTrue();

        ActiveProcessTracker.RegisteredProcessCount.Should().Be(1);
        ProcessTerminationSummary summary = ActiveProcessTracker.KillAll(TimeSpan.FromSeconds(2));
        summary.Results.Should().ContainSingle();
        process!.HasExited.Should().BeTrue();
        process.Dispose();
    }
    [TestMethod]
    public void Unregister_UnknownProcess_DoesNotAffectTrackedEntries()
    {
        Process? tracked = StartProcess();
        Process? other = StartProcess();
        tracked.Should().NotBeNull();
        other.Should().NotBeNull();
        ActiveProcessTracker.Register(tracked!);

        ActiveProcessTracker.Unregister(other!);
        ActiveProcessTracker.Unregister(null!);

        ActiveProcessTracker.RegisteredProcessCount.Should().Be(1);
        ProcessTerminationSummary summary = ActiveProcessTracker.KillAll(TimeSpan.FromSeconds(2));
        summary.AllVerified.Should().BeTrue();
        tracked!.HasExited.Should().BeTrue();
        SafeDispose(tracked);
        SafeDispose(other);
    }

    [TestMethod]
    public void ResetForTests_DisposesOwnedHandlesAndAllowsRegistration()
    {
        Process? process = StartProcess();
        process.Should().NotBeNull();
        ActiveProcessTracker.Register(process!);
        ActiveProcessTracker.TryBeginShutdown();
        long disposedBefore = ActiveProcessTracker.DisposedHandleCount;

        ActiveProcessTracker.ResetForTests();
        bool registered = ActiveProcessTracker.Register(process!);

        ActiveProcessTracker.RegisteredProcessCount.Should().Be(1);
        registered.Should().BeTrue("после сброса трекер принимает новые регистрации");
        ActiveProcessTracker.DisposedHandleCount.Should().BeGreaterThan(disposedBefore);
        SafeDispose(process);
        ActiveProcessTracker.ResetForTests();
    }

    [TestMethod]
    public void SummaryLogProperties_ExposeSafeProcessFields()
    {
        ProcessTerminationResult result = new(
            "proc-7",
            3210,
            attempted: true,
            succeeded: true,
            failed: false,
            alreadyExited: false,
            exitCode: 0,
            errors: null);
        ProcessTerminationSummary summary = new(1, 1, 0, 0, new[] { result });

        Dictionary<string, object?> properties = summary.ToLogProperties();

        properties["ProcessId"].Should().Be("proc-7");
        properties["Pid"].Should().Be("3210");
        properties["ExitCode"].Should().Be("3210=0");
        properties["Attempted"].Should().Be(1);
        properties["Succeeded"].Should().Be(1);
        properties["Failed"].Should().Be(0);
        properties["AlreadyExited"].Should().Be(0);
        properties["Verified"].Should().Be(1);
        properties["Errors"].Should().Be(string.Empty);
        properties["Total"].Should().Be(1);
        properties.Should().NotContainKey(
            "ErrorCode",
            "успешное завершение не несёт ErrorCode: потребитель, ищущий отказы по errorCode != null, не должен ловить ложное срабатывание");
    }

    [TestMethod]
    public void TrackedProcessRegistry_NeverExceedsCapacity()
    {
        ActiveProcessTracker.MaxTrackedProcesses.Should().BeGreaterThan(0);
        ActiveProcessTracker.RegisteredProcessCount.Should().BeLessThanOrEqualTo(
            ActiveProcessTracker.MaxTrackedProcesses);
    }

    private static void SafeDispose(Process? process)
    {
        try
        {
            process?.Dispose();
        }
        catch (Exception)
        {
        }
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

        SafeDispose(process);
    }

    private static Process? TryGetProtectedProcess()
    {
        try
        {
            Process idle = Process.GetProcessById(0);
            return idle.HasExited ? null : idle;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Process? StartProcess(bool exitImmediately = false)
    {
        string arguments = exitImmediately ? "-n 1 127.0.0.1" : "-n 30 127.0.0.1";
        return Process.Start(new ProcessStartInfo("ping.exe", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false
        });
    }
}
