// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;

using KTools_App.Diagnostics;

namespace KTools_App.Core;

public sealed record ProcessTerminationResult
{
    public ProcessTerminationResult(
        string processId,
        int osPid,
        bool attempted,
        bool succeeded,
        bool failed,
        bool alreadyExited,
        int? exitCode = null,
        IReadOnlyList<string>? errors = null)
    {
        ProcessId = string.IsNullOrWhiteSpace(processId) ? "proc-unknown" : processId;
        Pid = osPid;
        Attempted = attempted;
        Succeeded = succeeded;
        Failed = failed;
        AlreadyExited = alreadyExited;
        ExitCode = exitCode;
        Errors = errors is null || errors.Count == 0
            ? Array.Empty<string>()
            : new ReadOnlyCollection<string>(errors.ToArray());
    }

    public string ProcessId { get; }

    public int Pid { get; }

    public int OsPid => Pid;

    public bool Attempted { get; }

    public bool Succeeded { get; }

    public bool Failed { get; }

    public bool AlreadyExited { get; }

    public int? ExitCode { get; }

    public IReadOnlyList<string> Errors { get; }

    public string? Error => Errors.Count > 0 ? Errors[0] : null;

    public bool Verified => !Failed && (Succeeded || AlreadyExited);

    public string ToToken()
    {
        string exit = ExitCode.HasValue
            ? ExitCode.Value.ToString(CultureInfo.InvariantCulture)
            : "unknown";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{ProcessId}:pid={Pid};exit={exit};verified={Verified}");
    }
}

public sealed record ProcessTerminationSummary
{
    public const int MaxLoggedRows = 64;
    public const int MaxLoggedErrorLength = 512;
    public const int MaxLoggedErrorEntries = 8;
    public const int MaxLoggedErrorItemLength = 128;
    public const string ErrorIntakeUnknown = "termination-intake-unknown";
    public const string ErrorNoTrackedProcesses = "no-tracked-processes";

    public ProcessTerminationSummary(
        int attempted,
        int succeeded,
        int failed,
        int alreadyExited,
        IReadOnlyList<ProcessTerminationResult>? results = null,
        IReadOnlyList<string>? errors = null,
        bool? isConfirmedNoOp = null)
    {
        Attempted = Math.Max(0, attempted);
        Succeeded = Math.Max(0, succeeded);
        Failed = Math.Max(0, failed);
        AlreadyExited = Math.Max(0, alreadyExited);
        Results = results is null || results.Count == 0
            ? Array.Empty<ProcessTerminationResult>()
            : new ReadOnlyCollection<ProcessTerminationResult>(results.ToArray());
        Errors = errors is null || errors.Count == 0
            ? Array.Empty<string>()
            : new ReadOnlyCollection<string>(errors.ToArray());
        IsConfirmedNoOp = isConfirmedNoOp ?? (Attempted == 0
            && Succeeded == 0
            && Failed == 0
            && AlreadyExited == 0
            && Results.Count == 0
            && Errors.Count == 0);
    }

    public int Attempted { get; }

    public int Succeeded { get; }

    public int Failed { get; }

    public int AlreadyExited { get; }

    public IReadOnlyList<ProcessTerminationResult> Results { get; }

    public IReadOnlyList<string> Errors { get; }

    public bool IsConfirmedNoOp { get; }

    public int Total => Results.Count > 0 ? Results.Count : Attempted + AlreadyExited;

    public int Skipped => AlreadyExited;

    public int Verified
    {
        get
        {
            if (Results.Count > 0)
            {
                int verified = 0;
                foreach (ProcessTerminationResult result in Results)
                {
                    if (result.Verified)
                    {
                        verified++;
                    }
                }

                return verified;
            }

            return Math.Min(Succeeded + AlreadyExited, Total);
        }
    }

    public bool AllVerified => IsConfirmedNoOp
        || (Results.Count > 0
            ? Failed == 0 && Verified == Results.Count
            : Total > 0 && Failed == 0 && Verified >= Total);

    public bool HasPartialFailure => Failed > 0 && (Succeeded > 0 || AlreadyExited > 0);

    public bool HasFailure => Failed > 0 || Errors.Count > 0;

    public string? ErrorCode
    {
        get
        {
            if (Errors.Count > 0)
            {
                return BoundError(Errors[0]);
            }

            if (Total == 0 && !IsConfirmedNoOp)
            {
                return ErrorIntakeUnknown;
            }

            return IsConfirmedNoOp ? ErrorNoTrackedProcesses : null;
        }
    }

    public static ProcessTerminationSummary Empty { get; } = new(0, 0, 0, 0, isConfirmedNoOp: false);

    public static ProcessTerminationSummary NoTrackedProcesses { get; } =
        new(0, 0, 0, 0, isConfirmedNoOp: true);

    public static ProcessTerminationSummary Unavailable(string errorCode)
    {
        string code = string.IsNullOrWhiteSpace(errorCode) ? ErrorIntakeUnknown : errorCode;
        return Faulted(code);
    }

    public static ProcessTerminationSummary Faulted(string errorCode)
    {
        string code = string.IsNullOrWhiteSpace(errorCode) ? "termination-failed" : errorCode;
        ProcessTerminationResult result = new(
            "proc-shutdown",
            0,
            attempted: true,
            succeeded: false,
            failed: true,
            alreadyExited: false,
            exitCode: null,
            errors: new[] { code });
        return new ProcessTerminationSummary(1, 0, 1, 0, new[] { result }, new[] { code });
    }

    public IReadOnlyList<int> Pids => Results.Select(static result => result.Pid).ToArray();

    public IReadOnlyList<int?> ExitCodes => Results.Select(static result => result.ExitCode).ToArray();

    public IReadOnlyList<string> ProcessIds => Results.Select(static result => result.ProcessId).ToArray();

    public string ToToken()
    {
        int rows = Math.Min(Results.Count, MaxLoggedRows);
        if (rows == 0)
        {
            return "none";
        }

        StringBuilder builder = new(rows * 48);
        for (int index = 0; index < rows; index++)
        {
            if (index > 0)
            {
                builder.Append(';');
            }

            builder.Append(Results[index].ToToken());
        }

        if (Results.Count > rows)
        {
            builder.Append(";truncated=").Append((Results.Count - rows).ToString(CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    public string ToErrorToken()
    {
        if (Errors.Count == 0)
        {
            return string.Empty;
        }

        StringBuilder builder = new();
        int count = Math.Min(Errors.Count, MaxLoggedErrorEntries);
        for (int index = 0; index < count; index++)
        {
            if (builder.Length > 0)
            {
                builder.Append(';');
            }

            builder.Append(BoundError(Errors[index]));
            if (builder.Length >= MaxLoggedErrorLength)
            {
                return LogRedactor.Truncate(builder.ToString(), MaxLoggedErrorLength);
            }
        }

        if (Errors.Count > count)
        {
            builder.Append(";truncated=")
                .Append((Errors.Count - count).ToString(CultureInfo.InvariantCulture));
        }

        return LogRedactor.Truncate(builder.ToString(), MaxLoggedErrorLength);
    }

    public Dictionary<string, object?> ToLogProperties()
    {
        int rows = Math.Min(Results.Count, MaxLoggedRows);
        Dictionary<string, object?> properties = new(StringComparer.Ordinal)
        {
            ["Stage"] = "process_termination",
            ["Attempted"] = Attempted,
            ["Succeeded"] = Succeeded,
            ["Failed"] = Failed,
            ["AlreadyExited"] = AlreadyExited,
            ["Skipped"] = Skipped,
            ["Total"] = Total,
            ["Verified"] = Verified,
            ["Pid"] = JoinTokens(Results, rows, static result => result.Pid.ToString(CultureInfo.InvariantCulture)),
            ["ProcessId"] = JoinTokens(Results, rows, static result => result.ProcessId),
            ["ExitCode"] = JoinTokens(Results, rows, static result => result.Pid.ToString(CultureInfo.InvariantCulture)
                + "="
                + (result.ExitCode.HasValue ? result.ExitCode.Value.ToString(CultureInfo.InvariantCulture) : "unknown")),
            ["Errors"] = ToErrorToken(),
            ["OutputTail"] = ToToken()
        };

        // Пустой ErrorCode при Failed выглядел бы как «причина неизвестна, но код пуст»,
        // а потребитель искал отказы по errorCode != null получал бы ложное срабатывание.
        if (!string.IsNullOrWhiteSpace(ErrorCode))
        {
            properties["ErrorCode"] = ErrorCode;
        }

        return properties;
    }

    private static string BoundError(string? error)
    {
        string value = LogRedactor.Default.RedactDetailToken(error);
        return value.Length <= MaxLoggedErrorItemLength
            ? value
            : LogRedactor.TruncateChars(value, MaxLoggedErrorItemLength) + LogRedactor.TruncationMarker;
    }

    private static string JoinTokens(
        IReadOnlyList<ProcessTerminationResult> results,
        int rows,
        Func<ProcessTerminationResult, string> selector)
    {
        if (rows <= 0)
        {
            return string.Empty;
        }

        string[] values = new string[rows];
        for (int index = 0; index < rows; index++)
        {
            values[index] = selector(results[index]);
        }

        return string.Join(",", values);
    }
}
