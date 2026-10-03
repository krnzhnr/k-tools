// -*- coding: utf-8 -*-
using System;
using System.Globalization;
using System.Threading;

using KTools_App.Core;
using KTools_App.Diagnostics;

namespace KTools_App.Services.Contracts;

public static class ProgramDiagnostics
{
    public const string FailureEventId = "app.single_instance.failure";
    public const string FailureSource = "Program";
    public const string SessionMarker = "program";
    public const string CrashIdPrefix = "program-failure-";
    public const int MaxCrashIdLength = CrashCoordinator.MaxCrashIdLength;
    public const string UnknownErrorCode = "unknown-error";
    public const int MaxErrorCodeLength = 64;

    private static readonly Lazy<Guid> SessionCorrelation = new(() => Guid.NewGuid());

    private static long _failureCount;
    private static long _emergencyFailureCount;
    private static long _emergencySuccessCount;

    public static Guid SessionId => SessionCorrelation.Value;

    public static long FailureCount => Interlocked.Read(ref _failureCount);

    public static long EmergencyFailureCount => Interlocked.Read(ref _emergencyFailureCount);

    public static long EmergencySuccessCount => Interlocked.Read(ref _emergencySuccessCount);

    public static void WriteFailure(string stage, Exception? exception)
    {
        WriteFailure(stage, exception, null, EmergencyLogSink.Shared);
    }

    public static void WriteFailure(string stage, Exception? exception, string? errorCode)
    {
        WriteFailure(stage, exception, errorCode, EmergencyLogSink.Shared);
    }

    public static bool WriteFailure(string stage, Exception? exception, EmergencyLogSink sink)
    {
        return WriteFailure(stage, exception, null, sink);
    }

    public static bool WriteFailure(string stage, Exception? exception, string? errorCode, EmergencyLogSink sink)
    {
        Interlocked.Increment(ref _failureCount);
        string safeStage = LogRedactor.CompactSafeToken(stage);
        string safeCode = CompactErrorCode(errorCode);
        string reason = string.Create(
            CultureInfo.InvariantCulture,
            $"{FailureEventId}: stage={safeStage}; code={safeCode}; outcome=handoff-failed; marker={SessionMarker}");
        try
        {
            ArgumentNullException.ThrowIfNull(sink);
            bool written = sink.Write(
                reason,
                exception,
                FailureSource,
                CreateCrashId(),
                SessionId,
                LogLevel.Error);
            if (written)
            {
                Interlocked.Increment(ref _emergencySuccessCount);
                return true;
            }

            Interlocked.Increment(ref _emergencyFailureCount);
            return false;
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _emergencyFailureCount);
            return false;
        }
    }

    public static string CreateCrashId()
    {
        string candidate = CrashIdPrefix + Guid.NewGuid().ToString("N");
        return candidate.Length <= MaxCrashIdLength ? candidate : candidate[..MaxCrashIdLength];
    }

    private static string CompactErrorCode(string? errorCode)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
        {
            return UnknownErrorCode;
        }

        string compact = LogRedactor.CompactSafeToken(errorCode);
        return compact.Length <= MaxErrorCodeLength ? compact : compact[..MaxErrorCodeLength];
    }
}
