// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Infrastructure;
using KTools_App.Tests.TestHelpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KTools_App.Tests.Diagnostics;

/// <summary>
/// Проверяет OS-гарантию завершения дочерних процессов: привязку к Job Object
/// с флагом KILL_ON_JOB_CLOSE (issue #88). Логика проверяется герметично через
/// подмену системных вызовов, реальная ОС — адаптивным тестом.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ProcessJobObjectTests
{
    private static string ComSpec => Environment.GetEnvironmentVariable("ComSpec")
        ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");

    [TestInitialize]
    public void SetUp()
    {
        ActiveProcessTracker.ResetForTests();
        ActiveProcessTracker.ResetJobObjectForTests();
    }

    [TestCleanup]
    public void TearDown()
    {
        ActiveProcessTracker.KillAll(TimeSpan.FromSeconds(1));
        ActiveProcessTracker.ResetForTests();
        ActiveProcessTracker.ResetJobObjectForTests();
    }

    [TestMethod]
    public void TryAttachToJob_NullProcess_ReturnsFalse()
    {
        ActiveProcessTracker.TryAttachToJob(null).Should().BeFalse();
    }

    [TestMethod]
    public void IsAttachedToJob_NullProcess_ReturnsFalse()
    {
        ActiveProcessTracker.IsAttachedToJob(null).Should().BeFalse();
    }

    [TestMethod]
    public void TryAttachToJob_ExitedProcess_ReturnsFalseAndCountsFailure()
    {
        Process? child = StartPing(exitImmediately: true);
        child.Should().NotBeNull();
        child!.WaitForExit(10000).Should().BeTrue("процесс обязан завершиться до привязки");
        long failuresBefore = ActiveProcessTracker.JobAttachFailureCount;

        ActiveProcessTracker.TryAttachToJob(child).Should().BeFalse(
            "завершившийся процесс привязывать не к чему");

        ActiveProcessTracker.JobAttachFailureCount.Should().Be(
            failuresBefore + 1,
            "неудачная привязка обязана фиксироваться в счётчике");
        SafeDispose(child);
    }

    [TestMethod]
    public void Register_RunningChildProcess_AttachesItToJobObject()
    {
        var fake = new FakeJobObjectApi();
        ActiveProcessTracker.JobObjectApi = fake;
        Process? child = StartPing();
        child.Should().NotBeNull();

        try
        {
            ActiveProcessTracker.Register(child!).Should().BeTrue();

            ActiveProcessTracker.IsJobObjectActive.Should().BeTrue(
                "регистрация обязана создать задание");

            fake.AssignCalls.Should().ContainSingle(
                "регистрация обязана привязывать процесс к заданию автоматически");

            fake.AssignCalls[0].Process.Should().Be(
                child!.Handle,
                "привязывается дескриптор зарегистрированного процесса");

            ActiveProcessTracker.IsAttachedToJob(child).Should().BeTrue(
                "зарегистрированный процесс обязан числиться в задании");
        }
        finally
        {
            SafeKill(child);
        }
    }

    [TestMethod]
    public void TryAttachToJob_SecondProcess_ReusesJobObject()
    {
        var fake = new FakeJobObjectApi();
        ActiveProcessTracker.JobObjectApi = fake;
        Process? first = StartPing();
        Process? second = StartPing();
        first.Should().NotBeNull();
        second.Should().NotBeNull();

        try
        {
            ActiveProcessTracker.TryAttachToJob(first!).Should().BeTrue();
            ActiveProcessTracker.TryAttachToJob(second!).Should().BeTrue();

            fake.CreateCalls.Should().Be(1, "задание создаётся один раз на процесс приложения");
            fake.ConfigureCalls.Should().ContainSingle("флаг KILL_ON_JOB_CLOSE настраивается один раз");
            fake.AssignCalls.Should().HaveCount(2, "каждый процесс привязывается отдельно");
        }
        finally
        {
            SafeKill(first);
            SafeKill(second);
        }
    }

    [TestMethod]
    public void TryAttachToJob_WhenConfigurationFails_ClosesJobAndCountsFailure()
    {
        var fake = new FakeJobObjectApi { FailConfigure = true };
        ActiveProcessTracker.JobObjectApi = fake;
        Process? child = StartPing();
        child.Should().NotBeNull();
        long failuresBefore = ActiveProcessTracker.JobAttachFailureCount;

        try
        {
            ActiveProcessTracker.TryAttachToJob(child!).Should().BeFalse(
                "без флага KILL_ON_JOB_CLOSE задание бесполезно");

            ActiveProcessTracker.IsJobObjectActive.Should().BeFalse(
                "ненастроенное задание не должно оставаться открытым");

            fake.ClosedJobs.Should().ContainSingle(
                "ненастроенный дескриптор задания обязан закрываться");

            fake.AssignCalls.Should().BeEmpty("без задания привязывать не к чему");

            ActiveProcessTracker.JobAttachFailureCount.Should().Be(
                failuresBefore + 1,
                "неудачная привязка обязана фиксироваться в счётчике");
        }
        finally
        {
            SafeKill(child);
        }
    }

    [TestMethod]
    public async Task RunAsync_ChildProcess_AttachesItToJobObject()
    {
        var fake = new FakeJobObjectApi();
        ActiveProcessTracker.JobObjectApi = fake;
        var log = new RecordingLogService();
        var runner = new DirectProcessRunner(log);

        ProcessResult result = await runner.RunAsync(
            ComSpec,
            "cmd",
            "/c echo ok",
            ProcessExecutionContext.Create("cmd")).ConfigureAwait(false);

        result.IsSuccess.Should().BeTrue();
        fake.CreateCalls.Should().Be(1, "раннер обязан создать задание при запуске дочернего процесса");
        fake.ConfigureCalls.Should().ContainSingle("флаг KILL_ON_JOB_CLOSE настраивается один раз");
        fake.AssignCalls.Should().HaveCount(
            2,
            "привязка идёт сразу после запуска и подтверждается при регистрации: отказ регистрации при завершении не оставит процесс без гарантии ОС");
        fake.AssignCalls.Should().OnlyContain(
            call => call.Job == fake.ConfigureCalls[0],
            "обе привязки относятся к заданию приложения");
        fake.AssignCalls[0].Process.Should().Be(
            fake.AssignCalls[1].Process,
            "привязывается один и тот же дочерний процесс");
    }

    [TestMethod]
    public void TryAttachToJob_RealApi_AttachesWhenOsAllows()
    {
        Process? child = StartPing();
        child.Should().NotBeNull();

        try
        {
            bool attached = ActiveProcessTracker.TryAttachToJob(child!);
            if (!ActiveProcessTracker.IsJobObjectActive)
            {
                Assert.Inconclusive(
                    "ОС заблокировала настройку Job Object в этой среде: добровольная привязка недоступна");
            }

            attached.Should().BeTrue(
                "запущенный дочерний процесс обязан привязываться к заданию");

            ActiveProcessTracker.IsAttachedToJob(child!).Should().BeTrue(
                "дочерний процесс обязан числиться в задании с KILL_ON_JOB_CLOSE");
        }
        finally
        {
            SafeKill(child);
        }
    }

    private static Process? StartPing(bool exitImmediately = false)
    {
        string arguments = exitImmediately ? "-n 1 127.0.0.1" : "-n 30 127.0.0.1";
        return Process.Start(new ProcessStartInfo("ping.exe", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false
        });
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
        finally
        {
            SafeDispose(process);
        }
    }

    /// <summary>
    /// Тестовая подмена системных вызовов Job Object: фиксирует обращения
    /// и ведёт учёт привязок без реальной ОС.
    /// </summary>
    private sealed class FakeJobObjectApi : ActiveProcessTracker.IJobObjectApi
    {
        private readonly object _gate = new();
        private int _nextHandle = 4096;
        private readonly HashSet<(IntPtr Job, IntPtr Process)> _attached = new();

        public List<(IntPtr Job, IntPtr Process)> AssignCalls { get; } = new();

        public List<IntPtr> ConfigureCalls { get; } = new();

        public List<IntPtr> ClosedJobs { get; } = new();

        public int CreateCalls { get; private set; }

        public bool FailConfigure { get; set; }

        public IntPtr CreateJobObject()
        {
            lock (_gate)
            {
                CreateCalls++;
                return new IntPtr(_nextHandle++);
            }
        }

        public bool ConfigureKillOnClose(IntPtr job)
        {
            lock (_gate)
            {
                ConfigureCalls.Add(job);
                return !FailConfigure;
            }
        }

        public bool AssignProcessToJobObject(IntPtr job, IntPtr processHandle)
        {
            lock (_gate)
            {
                AssignCalls.Add((job, processHandle));
                _attached.Add((job, processHandle));
                return true;
            }
        }

        public bool IsProcessInJob(IntPtr processHandle, IntPtr jobHandle)
        {
            lock (_gate)
            {
                return _attached.Contains((jobHandle, processHandle));
            }
        }

        public void CloseJobObject(IntPtr job)
        {
            lock (_gate)
            {
                ClosedJobs.Add(job);
            }
        }
    }
}
