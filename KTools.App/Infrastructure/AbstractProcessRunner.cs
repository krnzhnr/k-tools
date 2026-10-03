// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Models;
using KTools_App.Services.Contracts;

namespace KTools_App.Infrastructure;

/// <summary>
/// Абстрактный базовый класс для безопасного асинхронного запуска дочерних консольных утилит.
/// </summary>
public abstract class AbstractProcessRunner
{
    /// <summary>
    /// Сервис логирования.
    /// </summary>
    protected ILogService Log { get; }
    protected IPathManager PathManager { get; }

    /// <summary>
    /// Инициализирует новый экземпляр AbstractProcessRunner.
    /// </summary>
    /// <param name="logService">Сервис логирования.</param>
    /// <param name="pathManager">Менеджер путей.</param>
    protected AbstractProcessRunner(ILogService logService, IPathManager pathManager)
    {
        Log = logService ?? throw new ArgumentNullException(nameof(logService));
        PathManager = pathManager ?? throw new ArgumentNullException(nameof(pathManager));
    }

    /// <summary>
    /// </summary>
    /// <param name="binaryName">Имя бинарного файла без расширения (например, "ffmpeg").</param>
    /// <param name="onOutputLine">Делегат для построчного перехвата стандартного вывода (stdout).</param>
    /// <param name="onErrorLine">Делегат для построчного перехвата вывода ошибок (stderr).</param>
    /// <param name="cancellationToken">Токен отмены для принудительного прерывания задачи.</param>
    protected async Task<ProcessResult> RunProcessAsync(
        string binaryName,
        string arguments,
        Action<string>? onOutputLine = null,
        Action<string>? onErrorLine = null,
        CancellationToken cancellationToken = default,
        string? workingDir = null,
        ProcessExecutionContext? context = null,
        string? expectedArtifact = null,
        int maxSuccessExitCode = 0,
        Func<bool>? verifyParseResult = null,
        string? explicitBinaryPath = null,
        bool emitOutputSamples = false)
    {
        string tool = ProcessExecutionContext.NormalizeToolName(binaryName);
        ProcessExecutionContext baseContext = context ?? ProcessExecutionContext.Create(tool);
        ProcessExecutionContext executionContext = string.IsNullOrWhiteSpace(expectedArtifact)
            ? baseContext
            : baseContext.WithExpectedArtifact(expectedArtifact);
        string processId = ProcessExecutionContext.CreateProcessId();
        string binaryPath = string.IsNullOrWhiteSpace(explicitBinaryPath)
            ? PathManager.GetBinaryPath(binaryName)
            : explicitBinaryPath!;

        if (!File.Exists(binaryPath))
        {
            ProcessResult missing = ProcessResult.NotStarted(
                executionContext,
                ProcessResult.ErrorBinaryMissing,
                $"Отсутствует исполняемый файл внешней утилиты '{tool}'",
                processId: processId);
            WriteProcessEvent(
                ProcessEventIds.StartFailed,
                LogLevel.Error,
                LogStatus.Failed,
                missing.Message,
                executionContext,
                missing,
                includeOutputTail: false,
                extraProperties: BuildLaunchProperties(null, 0, ProcessExecutionContext.LabelDirectory(workingDir)));
            return missing;
        }

        string commandLine = $"\"{binaryPath}\" {arguments}";
        int argumentCount = ProcessExecutionContext.CountArguments(arguments);
        string workingDirLabel = ProcessExecutionContext.LabelDirectory(workingDir);

        var startInfo = new ProcessStartInfo
        {
            FileName = binaryPath,
            Arguments = arguments,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = ProcessOutputPolicy.ResolveEncoding(),
            StandardErrorEncoding = ProcessOutputPolicy.ResolveEncoding(),
            WorkingDirectory = workingDir ?? Path.GetDirectoryName(binaryPath) ?? AppContext.BaseDirectory
        };

        using var process = new Process { StartInfo = startInfo };
        ProcessOutputBuffer stdoutBuffer = new() { Stream = "stdout" };
        ProcessOutputBuffer stderrBuffer = new() { Stream = "stderr" };
        ProcessOutputRateLimiter limiter = new();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            ProcessResult startFailure = ProcessResult.Failed(
                executionContext,
                ProcessResult.ErrorStartFailed,
                $"Не удалось запустить внешний процесс '{tool}'",
                processId,
                pid: null,
                exitCode: null,
                durationMs: stopwatch.Elapsed.TotalMilliseconds,
                exception: ex);
            WriteProcessEvent(
                ProcessEventIds.StartFailed,
                LogLevel.Error,
                LogStatus.Failed,
                startFailure.Message,
                executionContext,
                startFailure,
                exception: ex,
                includeOutputTail: false,
                extraProperties: BuildLaunchProperties(commandLine, argumentCount, workingDirLabel));
            return startFailure;
        }

        ActiveProcessTracker.Register(process);
        int osPid = SafePid(process);
        LogContext logContext = executionContext.ToLogContext().WithProcess(processId);

        WriteProcessEvent(
            osPid > 0 ? ProcessEventIds.Started : ProcessEventIds.Launched,
            LogLevel.Info,
            LogStatus.Running,
            $"Запущен внешний процесс '{tool}'",
            executionContext,
            result: null,
            processId: processId,
            pid: osPid,
            extraProperties: BuildLaunchProperties(commandLine, argumentCount, workingDirLabel));

        try
        {
            Task stdoutTask = PumpAsync(process.StandardOutput, "stdout", stdoutBuffer, onOutputLine, limiter, emitOutputSamples, executionContext, processId, logContext);
            Task stderrTask = PumpAsync(process.StandardError, "stderr", stderrBuffer, onErrorLine, limiter, emitOutputSamples, executionContext, processId, logContext);

            ProcessResult? cancelled = null;
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = Terminate(
                    process,
                    executionContext,
                    processId,
                    osPid,
                    stopwatch,
                    stderrBuffer,
                    expectedArtifact);
            }

            stopwatch.Stop();
            if (cancelled is not null)
            {
                WriteProcessEvent(
                    cancelled.TerminationVerified ? ProcessEventIds.Cancelled : ProcessEventIds.TerminationUnverified,
                    cancelled.TerminationVerified ? LogLevel.Info : LogLevel.Warning,
                    cancelled.Status,
                    cancelled.Message,
                    executionContext,
                    cancelled,
                    includeOutputTail: true,
                    extraProperties: BuildExitProperties(commandLine, argumentCount, workingDirLabel, limiter, exitCodeKnown: true));
                return cancelled;
            }

            ProcessResult outcome = EvaluateOutcome(
                process,
                tool,
                executionContext,
                processId,
                osPid,
                stopwatch,
                stdoutBuffer,
                stderrBuffer,
                expectedArtifact,
                maxSuccessExitCode,
                verifyParseResult);
            WriteProcessEvent(
                osPid > 0 && outcome.ExitCode.HasValue ? ProcessEventIds.Exit : ProcessEventIds.Completed,
                ResolveExitLevel(outcome),
                outcome.Status,
                outcome.Message,
                executionContext,
                outcome,
                exception: null,
                includeOutputTail: true,
                extraProperties: BuildExitProperties(
                    commandLine,
                    argumentCount,
                    workingDirLabel,
                    limiter,
                    osPid > 0 && outcome.ExitCode.HasValue));
            return outcome;
        }
        finally
        {
            ActiveProcessTracker.Unregister(process);
        }
    }

    private async Task PumpAsync(
        StreamReader reader,
        string stream,
        ProcessOutputBuffer buffer,
        Action<string>? lineSink,
        ProcessOutputRateLimiter limiter,
        bool emitSamples,
        ProcessExecutionContext context,
        string processId,
        LogContext logContext)
    {
        var accumulator = new System.Text.StringBuilder();
        char[] chunk = new char[4096];

        try
        {
            while (true)
            {
                int read = await reader.ReadAsync(chunk, 0, chunk.Length).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                for (int index = 0; index < read; index++)
                {
                    char raw = chunk[index];
                    if (raw is '\r' or '\n')
                    {
                        if (accumulator.Length > 0)
                        {
                            Emit(accumulator.ToString());
                        }

                        continue;
                    }

                    if (accumulator.Length < ProcessOutputPolicy.MaxRawLineLength)
                    {
                        accumulator.Append(raw);
                    }
                }
            }

            if (accumulator.Length > 0)
            {
                Emit(accumulator.ToString());
            }
        }
        catch (Exception)
        {
        }

        void Emit(string rawLine)
        {
            accumulator.Clear();

            // Строка парсерам передаётся в исходном виде: mkvmerge --identify и
            // другие потребители собирают из строк JSON либо разбирают значения,
            // и любое нормализование (склейка пробелов, обрезка по длине) ломает
            // разбор. Нормализованный вид используется только для буфера и лога.
            try
            {
                lineSink?.Invoke(rawLine);
            }
            catch (Exception)
            {
            }

            string line = ProcessOutputPolicy.NormalizeLine(rawLine);
            if (line.Length == 0)
            {
                return;
            }

            buffer.Append(line);

            if (emitSamples && limiter.TryAcquire())
            {
                try
                {
                    Log.Write(
                        ProcessEventIds.OutputSampled,
                        LogLevel.Debug,
                        line,
                        stream,
                        status: LogStatus.Running,
                        context: logContext,
                        properties: new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["ProcessId"] = processId,
                            ["Tool"] = context.Tool,
                            ["Stage"] = stream
                        });
                }
                catch (Exception)
                {
                }
            }
        }
    }

    private static ProcessResult Terminate(
        Process process,
        ProcessExecutionContext context,
        string processId,
        int osPid,
        Stopwatch stopwatch,
        ProcessOutputBuffer stderrBuffer,
        string? expectedArtifact)
    {
        ProcessTerminationResult termination = ActiveProcessTracker.TerminateProcess(process, TimeSpan.FromSeconds(5));
        stopwatch.Stop();

        bool verified = termination.Verified;
        int? exitCode = termination.ExitCode ?? SafeExitCode(process);
        bool? outputExists = string.IsNullOrWhiteSpace(expectedArtifact) ? null : VerifyArtifact(expectedArtifact);
        IReadOnlyList<string> stderrLines = stderrBuffer.Snapshot();

        return ProcessResult.Cancelled(
            context,
            verified ? ProcessResult.MessageCancelled : ProcessResult.MessageUnverifiedTermination,
            processId,
            termination.Pid > 0 ? termination.Pid : osPid,
            exitCode,
            stopwatch.Elapsed.TotalMilliseconds,
            outputExists,
            stderrBuffer.BuildTail(),
            stderrBuffer.Truncated,
            ProcessOutputPolicy.CountWarnings(stderrLines),
            ProcessOutputPolicy.CountErrors(stderrLines),
            terminationVerified: verified,
            errorCode: verified ? ProcessResult.ErrorCancelled : ProcessResult.ErrorTerminationUnverified,
            cleanupState: verified
                ? (outputExists == false ? CleanupState.Partial : CleanupState.Completed)
                : CleanupState.Partial,
            outputMetrics: stderrBuffer.Metrics());
    }

    private static bool VerifyArtifact(string? expectedPath)
    {
        if (string.IsNullOrWhiteSpace(expectedPath))
        {
            return false;
        }

        try
        {
            return File.Exists(expectedPath) && new FileInfo(expectedPath).Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static ProcessResult EvaluateOutcome(
        Process process,
        string tool,
        ProcessExecutionContext context,
        string processId,
        int osPid,
        Stopwatch stopwatch,
        ProcessOutputBuffer stdoutBuffer,
        ProcessOutputBuffer stderrBuffer,
        string? expectedArtifact,
        int maxSuccessExitCode,
        Func<bool>? verifyParseResult)
    {
        int? exitCode = SafeExitCode(process);
        double durationMs = stopwatch.Elapsed.TotalMilliseconds;
        IReadOnlyList<string> stderrLines = stderrBuffer.Snapshot();
        string tail = stderrBuffer.BuildTail();
        string combinedTail = tail.Length > 0 ? tail : stdoutBuffer.BuildTail();
        bool truncated = stdoutBuffer.Truncated || stderrBuffer.Truncated;
        int warnings = ProcessOutputPolicy.CountWarnings(stderrLines);
        int errorLines = ProcessOutputPolicy.CountErrors(stderrLines);
        bool? outputExists = string.IsNullOrWhiteSpace(expectedArtifact) ? null : VerifyArtifact(expectedArtifact);
        ProcessOutputMetrics metrics = ProcessOutputMetrics.Combine(stdoutBuffer.Metrics(), stderrBuffer.Metrics());

        if (!exitCode.HasValue || exitCode.Value > Math.Max(0, maxSuccessExitCode))
        {
            return ProcessResult.Failed(
                context,
                ProcessResult.ErrorNonZeroExit,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Внешний процесс '{tool}' завершился с кодом возврата {(exitCode.HasValue ? exitCode.Value.ToString(CultureInfo.InvariantCulture) : "неизвестным")}"),
                processId,
                osPid,
                exitCode,
                durationMs,
                outputExists,
                combinedTail,
                truncated,
                warnings,
                errorLines) with { OutputMetrics = metrics };
        }

        if (outputExists == false)
        {
            return ProcessResult.Failed(
                context,
                ProcessResult.ErrorArtifactMissing,
                ProcessResult.MessageArtifactMissing,
                processId,
                osPid,
                exitCode,
                durationMs,
                false,
                combinedTail,
                truncated,
                warnings,
                errorLines) with { OutputMetrics = metrics };
        }

        if (verifyParseResult is not null && !verifyParseResult())
        {
            return ProcessResult.Failed(
                context,
                ProcessResult.ErrorParseInvalid,
                $"Внешний процесс '{tool}' завершился без кода ошибки, но результат разбора непригоден",
                processId,
                osPid,
                exitCode,
                durationMs,
                outputExists,
                combinedTail,
                truncated,
                warnings,
                errorLines) with { OutputMetrics = metrics };
        }

        if (exitCode.Value > 0 || warnings > 0)
        {
            return ProcessResult.PartiallySucceeded(
                context,
                $"Внешний процесс '{tool}' завершён с предупреждениями",
                processId,
                osPid,
                exitCode,
                durationMs,
                outputExists,
                combinedTail,
                truncated,
                warnings,
                errorLines) with { OutputMetrics = metrics };
        }

        return ProcessResult.Succeeded(
            context,
            processId,
            osPid,
            exitCode,
            durationMs,
            outputExists,
            combinedTail,
            truncated,
            warnings,
            errorLines) with { OutputMetrics = metrics };
    }

    private static LogLevel ResolveExitLevel(ProcessResult result) => result.Status switch
    {
        LogStatus.Succeeded => LogLevel.Info,
        LogStatus.PartiallySucceeded => LogLevel.Warning,
        LogStatus.Cancelled => LogLevel.Info,
        _ => LogLevel.Error
    };

    private static Dictionary<string, object?> BuildLaunchProperties(
        string? commandLine,
        int argumentCount,
        string workingDirLabel)
    {
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ArgumentCount"] = argumentCount,
            ["WorkingDirLabel"] = workingDirLabel
        };

        if (!string.IsNullOrEmpty(commandLine))
        {
            properties["CommandLine"] = commandLine;
        }

        return properties;
    }

    /// <summary>
    /// Свойства терминального события. При недоступном коде возврата событие уходит
    /// под идентификатором <c>process.completed</c> (код возврата не выдумывается), но
    /// получает явный маркер недоступности, чтобы потеря кода не выглядела как нулевой код.
    /// </summary>
    private static Dictionary<string, object?> BuildExitProperties(
        string? commandLine,
        int argumentCount,
        string workingDirLabel,
        ProcessOutputRateLimiter limiter,
        bool exitCodeKnown)
    {
        Dictionary<string, object?> properties = BuildLaunchProperties(commandLine, argumentCount, workingDirLabel);
        if (!exitCodeKnown)
        {
            properties["ExitCode"] = "unavailable";
        }

        long suppressed = limiter.SuppressedCount;
        if (suppressed > 0)
        {
            properties["SuppressedCount"] = suppressed;
        }

        return properties;
    }

    private void WriteProcessEvent(
        string eventId,
        LogLevel level,
        LogStatus status,
        string message,
        ProcessExecutionContext context,
        ProcessResult? result,
        string? processId = null,
        int? pid = null,
        Exception? exception = null,
        bool includeOutputTail = true,
        IReadOnlyDictionary<string, object?>? extraProperties = null)
    {
        try
        {
            Dictionary<string, object?> properties = result is null
                ? context.ToLogProperties()
                : result.ToLogProperties(includeOutputTail);

            if (result is null)
            {
                if (!string.IsNullOrEmpty(processId))
                {
                    properties["ProcessId"] = processId;
                }

                if (pid is > 0)
                {
                    properties["Pid"] = pid.Value;
                }
            }

            if (extraProperties is not null)
            {
                foreach (KeyValuePair<string, object?> pair in extraProperties)
                {
                    properties[pair.Key] = pair.Value;
                }
            }

            ExceptionInfo? exceptionInfo = null;
            if (exception is not null)
            {
                try
                {
                    exceptionInfo = ExceptionInfo.FromException(exception);
                }
                catch (Exception)
                {
                    exceptionInfo = null;
                }
            }

            Log.Write(new LogEvent
            {
                EventId = eventId,
                Level = level,
                Status = status,
                Source = context.Tool,
                Message = message,
                OperationId = context.OperationId,
                ItemId = context.ItemId,
                ProcessId = result?.ProcessId ?? processId,
                Pid = result?.Pid ?? (pid is > 0 ? pid : null),
                Attempt = context.Attempt,
                Tool = context.Tool,
                ToolVersion = context.ToolVersion,
                ExitCode = result?.ExitCode,
                DurationMs = result is { DurationMs: > 0 } ? result.DurationMs : null,
                ErrorCode = result?.ErrorCode,
                Properties = properties,
                Exception = exceptionInfo
            });

        }
        catch (Exception)
        {
        }
    }

    private static int SafePid(Process process)
    {
        try
        {
            return process.Id;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static int? SafeExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
