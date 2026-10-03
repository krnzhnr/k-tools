// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

namespace KTools_App.Core;

public static class ActiveProcessTracker
{
    public const int MaxTrackedProcesses = 512;
    public const int MinPerProcessTimeoutMilliseconds = 1;
    public const int MaxPerProcessTimeoutMilliseconds = 30000;
    public const string ErrorAlreadyExited = "already-exited";
    public const string ErrorExitTimeout = "exit-verification-timeout";
    public const string ErrorExitVerification = "exit-state-verification-failed";
    public const string ErrorKillFailed = "kill-failed";
    public const string ErrorHandleUnavailable = "process-handle-unavailable";
    public const string ErrorPidReuse = "pid-reuse-detected";
    public const string ErrorCapacityEvicted = "capacity-evicted";

    private static readonly object Gate = new();
    private static readonly Dictionary<int, TrackedProcess> Tracked = new();

    private static int _shutdownRequested;
    private static int _nextRegistrationId;
    private static long _registrationFailures;
    private static long _pidReuseDetections;
    private static long _disposedHandles;
    private static long _capacityEvictions;

    public static bool HasActiveProcesses
    {
        get
        {
            TrackedProcess[] snapshot;
            lock (Gate)
            {
                snapshot = Tracked.Values.ToArray();
            }

            foreach (TrackedProcess entry in snapshot)
            {
                if (IsRunning(entry))
                {
                    return true;
                }
            }

            return false;
        }
    }

    public static bool IsShutdownRequested => Volatile.Read(ref _shutdownRequested) != 0;

    public static int RegisteredProcessCount
    {
        get
        {
            lock (Gate)
            {
                return Tracked.Count;
            }
        }
    }

    public static long RegistrationFailureCount => Interlocked.Read(ref _registrationFailures);

    public static long PidReuseDetectionCount => Interlocked.Read(ref _pidReuseDetections);

    public static long DisposedHandleCount => Interlocked.Read(ref _disposedHandles);

    public static long CapacityEvictionCount => Interlocked.Read(ref _capacityEvictions);

    public static bool IsAcceptingRegistrations => !IsShutdownRequested;

    public static bool TryBeginShutdown()
    {
        lock (Gate)
        {
            Volatile.Write(ref _shutdownRequested, 1);
        }

        return true;
    }

    public static void ResetForTests()
    {
        lock (Gate)
        {
            Volatile.Write(ref _shutdownRequested, 0);
            foreach (TrackedProcess entry in Tracked.Values)
            {
                DisposeHandle(entry);
            }

            Tracked.Clear();
        }
    }

    public static bool Register(Process process)
    {
        if (process is null)
        {
            return false;
        }

        lock (Gate)
        {
            if (IsShutdownRequested)
            {
                return false;
            }

            foreach (TrackedProcess entry in Tracked.Values)
            {
                if (ReferenceEquals(entry.Source, process))
                {
                    return true;
                }
            }

            int osPid;
            try
            {
                osPid = process.Id;
                if (osPid <= 0 || process.HasExited)
                {
                    return false;
                }
            }
            catch (Exception)
            {
                Interlocked.Increment(ref _registrationFailures);
                return false;
            }

            Process? owned = TryOpenOwnedHandle(osPid, out bool handleFailed);
            if (handleFailed)
            {
                Interlocked.Increment(ref _registrationFailures);
            }

            DateTime? startTimeUtc = ReadStartTimeUtc(process, owned);
            RemoveConflictingEntry(osPid, process);
            EnforceCapacity();
            int registrationId = NextRegistrationId();
            Tracked[registrationId] = new TrackedProcess(registrationId, process, owned, osPid, startTimeUtc);
            return true;
        }
    }

    public static void Unregister(Process process)
    {
        if (process is null)
        {
            return;
        }

        lock (Gate)
        {
            foreach (TrackedProcess entry in Tracked.Values.ToArray())
            {
                if (ReferenceEquals(entry.Source, process))
                {
                    Tracked.Remove(entry.RegistrationId);
                    DisposeHandle(entry);
                    return;
                }
            }
        }
    }

    public static ProcessTerminationSummary KillAll()
    {
        return KillAll(TimeSpan.FromSeconds(3));
    }

    public static ProcessTerminationSummary KillAll(int timeoutMilliseconds)
    {
        return KillAll(TimeSpan.FromMilliseconds(Math.Max(1, timeoutMilliseconds)));
    }

    public static ProcessTerminationSummary KillAll(TimeSpan perProcessTimeout)
    {
        TimeSpan effective = NormalizeTimeout(perProcessTimeout);
        TrackedProcess[] snapshot;
        lock (Gate)
        {
            Volatile.Write(ref _shutdownRequested, 1);
            snapshot = Tracked.Values.OrderBy(static entry => entry.RegistrationId).ToArray();
        }

        if (snapshot.Length == 0)
        {
            return ProcessTerminationSummary.NoTrackedProcesses;
        }

        List<ProcessTerminationResult> results = new(snapshot.Length);
        List<string> errors = new();
        foreach (TrackedProcess entry in snapshot)
        {
            ProcessTerminationResult result = Terminate(entry, effective, errors);
            results.Add(result);
            if (result.Verified)
            {
                RemoveTracked(entry);
            }
        }

        int attempted = results.Count(static result => result.Attempted);
        int succeeded = results.Count(static result => result.Succeeded);
        int failed = results.Count(static result => result.Failed);
        int alreadyExited = results.Count(static result => result.AlreadyExited);
        if (failed > 0 && errors.Count == 0)
        {
            errors.Add(ErrorExitVerification);
        }

        return new ProcessTerminationSummary(attempted, succeeded, failed, alreadyExited, results, errors);
    }

    /// <summary>
    /// Принудительно завершить один процесс и доказать факт его выхода ожиданием.
    /// Используется слоем выполнения внешних утилит при отмене операции:
    /// сам факт вызова Kill не считается успешным завершением.
    /// </summary>
    /// <param name="process">Процесс, который требуется завершить.</param>
    /// <param name="perProcessTimeout">Максимальное время ожидания фактического выхода.</param>
    /// <returns>Типизированный результат завершения с признаком доказанного выхода.</returns>
    public static ProcessTerminationResult TerminateProcess(Process process, TimeSpan perProcessTimeout)
    {
        TimeSpan effective = NormalizeTimeout(perProcessTimeout);
        List<string> errors = new(2);

        if (process is null)
        {
            errors.Add(ErrorHandleUnavailable);
            return CreateResult("proc-unknown", 0, false, false, true, false, null, errors);
        }

        TrackedProcess? entry = TryCreateTransientEntry(process, errors);
        if (entry is null)
        {
            return CreateResult("proc-unknown", 0, true, false, true, false, null, errors);
        }

        try
        {
            return Attempt(entry, effective, errors);
        }
        finally
        {
            entry.DisposeOwnedHandle();
        }
    }

    private static TrackedProcess? TryCreateTransientEntry(Process process, List<string> errors)
    {
        int osPid;
        try
        {
            osPid = process.Id;
        }
        catch (Exception)
        {
            errors.Add(ErrorHandleUnavailable);
            return null;
        }

        if (osPid <= 0)
        {
            errors.Add(ErrorHandleUnavailable);
            return null;
        }

        Process? owned = TryOpenOwnedHandle(osPid, out bool handleFailed);
        if (handleFailed || owned is null)
        {
            errors.Add(ErrorHandleUnavailable);
        }

        DateTime? startTimeUtc = ReadStartTimeUtc(process, owned);
        return new TrackedProcess(0, process, owned, osPid, startTimeUtc);
    }

    private static ProcessTerminationResult Terminate(
        TrackedProcess entry,
        TimeSpan perProcessTimeout,
        List<string> errors)
    {
        List<string> localErrors = new(2);
        ProcessTerminationResult result = Attempt(entry, perProcessTimeout, localErrors);
        if (result.Failed && !IsPidReuse(result.Error))
        {
            foreach (string error in localErrors)
            {
                AddError(errors, error);
            }
        }

        return result;
    }

    private static ProcessTerminationResult Attempt(
        TrackedProcess entry,
        TimeSpan perProcessTimeout,
        List<string> localErrors)
    {
        string processId = entry.ProcessId;
        int osPid = entry.OsPid;
        Process? killHandle = entry.KillHandle;
        if (killHandle is null)
        {
            localErrors.Add(ErrorHandleUnavailable);
            return CreateResult(processId, osPid, false, false, true, false, null, localErrors);
        }

        if (IsPidReuseDetected(entry, killHandle))
        {
            localErrors.Add(ErrorPidReuse);
            Interlocked.Increment(ref _pidReuseDetections);
            return CreateResult(processId, osPid, false, false, true, false, null, localErrors);
        }

        if (IsExited(killHandle))
        {
            localErrors.Add(ErrorAlreadyExited);
            return CreateResult(
                processId,
                osPid,
                false,
                false,
                false,
                true,
                ReadExitCode(entry),
                null);
        }

        try
        {
            killHandle.Kill(entireProcessTree: true);
        }
        catch (Exception exception)
        {
            localErrors.Add(FormatError(exception));
            localErrors.Add(ErrorKillFailed);
            return CreateResult(
                processId,
                osPid,
                true,
                false,
                true,
                false,
                ReadExitCode(entry),
                localErrors);
        }

        DateTime deadline = DateTime.UtcNow + perProcessTimeout;
        if (!WaitForExit(killHandle, deadline))
        {
            localErrors.Add(ErrorExitTimeout);
            return CreateResult(
                processId,
                osPid,
                true,
                false,
                true,
                false,
                ReadExitCode(entry),
                localErrors);
        }

        if (!IsExited(killHandle))
        {
            localErrors.Add(ErrorExitVerification);
            return CreateResult(
                processId,
                osPid,
                true,
                false,
                true,
                false,
                ReadExitCode(entry),
                localErrors);
        }

        return CreateResult(
            processId,
            osPid,
            true,
            true,
            false,
            false,
            ReadExitCode(entry),
            null);
    }

    private static ProcessTerminationResult CreateResult(
        string processId,
        int osPid,
        bool attempted,
        bool succeeded,
        bool failed,
        bool alreadyExited,
        int? exitCode,
        IReadOnlyList<string>? errors)
    {
        return new ProcessTerminationResult(
            processId,
            osPid,
            attempted,
            succeeded,
            failed,
            alreadyExited,
            exitCode,
            errors);
    }

    private static bool WaitForExit(Process handle, DateTime deadline)
    {
        double remaining = (deadline - DateTime.UtcNow).TotalMilliseconds;
        int milliseconds = (int)Math.Ceiling(Math.Max(1d, remaining));
        try
        {
            if (handle.WaitForExit(milliseconds))
            {
                return true;
            }
        }
        catch (Exception)
        {
            return false;
        }

        return IsExited(handle);
    }

    private static bool IsRunning(TrackedProcess entry)
    {
        Process? handle = entry.KillHandle;
        if (handle is null)
        {
            return false;
        }

        try
        {
            return !handle.HasExited;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static bool IsExited(Process handle)
    {
        try
        {
            return handle.HasExited;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static int? ReadExitCode(TrackedProcess entry)
    {
        int? fromSource = TryReadExitCode(entry.Source);
        return fromSource ?? (entry.Owned is null ? null : TryReadExitCode(entry.Owned));
    }

    private static int? TryReadExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : (int?)null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static DateTime? ReadStartTimeUtc(Process? primary, Process? owned)
    {
        if (primary is not null)
        {
            DateTime? value = TryReadStartTime(primary);
            if (value.HasValue)
            {
                return value;
            }
        }

        return owned is null ? null : TryReadStartTime(owned);
    }

    private static DateTime? TryReadStartTime(Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsPidReuseDetected(TrackedProcess entry, Process handle)
    {
        if (!entry.StartTimeUtc.HasValue)
        {
            return false;
        }

        DateTime? current = TryReadStartTime(handle);
        if (!current.HasValue)
        {
            return false;
        }

        return current.Value > entry.StartTimeUtc.Value;
    }

    private static bool IsPidReuse(string? error)
    {
        return string.Equals(error, ErrorPidReuse, StringComparison.Ordinal);
    }

    private static void EnforceCapacity()
    {
        while (Tracked.Count >= MaxTrackedProcesses)
        {
            int oldest = int.MaxValue;
            foreach (int key in Tracked.Keys)
            {
                if (key < oldest)
                {
                    oldest = key;
                }
            }

            if (oldest == int.MaxValue || !Tracked.Remove(oldest, out TrackedProcess? evicted))
            {
                return;
            }

            DisposeHandle(evicted);
            Interlocked.Increment(ref _capacityEvictions);
        }
    }

    private static void RemoveConflictingEntry(int osPid, Process source)
    {
        TrackedProcess? conflict = null;
        foreach (TrackedProcess entry in Tracked.Values)
        {
            if (entry.OsPid == osPid && !ReferenceEquals(entry.Source, source))
            {
                conflict = entry;
                break;
            }
        }

        if (conflict is null)
        {
            return;
        }

        Tracked.Remove(conflict.RegistrationId);
        DisposeHandle(conflict);
        Interlocked.Increment(ref _pidReuseDetections);
    }

    private static void RemoveTracked(TrackedProcess entry)
    {
        lock (Gate)
        {
            if (!Tracked.Remove(entry.RegistrationId))
            {
                return;
            }
        }

        DisposeHandle(entry);
    }

    private static void DisposeHandle(TrackedProcess entry)
    {
        Process? owned = entry.Owned;
        if (owned is null)
        {
            return;
        }

        try
        {
            owned.Dispose();
            Interlocked.Increment(ref _disposedHandles);
        }
        catch (Exception)
        {
        }
    }

    private static Process? TryOpenOwnedHandle(int osPid, out bool failed)
    {
        failed = false;
        if (osPid <= 0)
        {
            failed = true;
            return null;
        }

        try
        {
            return Process.GetProcessById(osPid);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (Exception)
        {
            failed = true;
            return null;
        }
    }

    private static void AddError(List<string> errors, string error)
    {
        if (!errors.Contains(error, StringComparer.Ordinal))
        {
            errors.Add(error);
        }
    }

    private static int NextRegistrationId()
    {
        int next = Interlocked.Increment(ref _nextRegistrationId);
        return next < 0 ? 1 : next;
    }

    private static TimeSpan NormalizeTimeout(TimeSpan timeout)
    {
        double milliseconds = timeout.TotalMilliseconds;
        if (double.IsNaN(milliseconds) || milliseconds < MinPerProcessTimeoutMilliseconds)
        {
            milliseconds = MinPerProcessTimeoutMilliseconds;
        }

        if (milliseconds > MaxPerProcessTimeoutMilliseconds)
        {
            milliseconds = MaxPerProcessTimeoutMilliseconds;
        }

        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private static string FormatError(Exception exception)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{exception.GetType().Name}:0x{exception.HResult:X8}");
    }

    private sealed class TrackedProcess
    {
        public TrackedProcess(
            int registrationId,
            Process source,
            Process? owned,
            int osPid,
            DateTime? startTimeUtc)
        {
            RegistrationId = registrationId;
            Source = source;
            Owned = owned;
            OsPid = osPid;
            StartTimeUtc = startTimeUtc;
            KillHandle = owned ?? source;
        }

        public int RegistrationId { get; }

        public Process Source { get; }

        public Process? Owned { get; }

        public Process? KillHandle { get; }

        public int OsPid { get; }

        public DateTime? StartTimeUtc { get; }

        public string ProcessId => RegistrationId <= 0
            ? "proc-live-" + OsPid.ToString(CultureInfo.InvariantCulture)
            : "proc-" + RegistrationId.ToString(CultureInfo.InvariantCulture);

        public void DisposeOwnedHandle()
        {
            Process? owned = Owned;
            if (owned is null)
            {
                return;
            }

            try
            {
                owned.Dispose();
            }
            catch (Exception)
            {
            }
        }
    }
}
