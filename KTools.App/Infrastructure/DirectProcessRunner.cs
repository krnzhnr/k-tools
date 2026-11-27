// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Models;
using KTools_App.Services.Contracts;

namespace KTools_App.Infrastructure;

public sealed class DirectProcessRunner
{
    private const int KillWaitMilliseconds = 5000;
    private const int PollIntervalMilliseconds = 100;

    private readonly ILogService _log;

    public DirectProcessRunner(ILogService logService)
    {
        _log = logService ?? throw new ArgumentNullException(nameof(logService));
    }

    public async Task<ProcessResult> RunAsync(
        string binaryPath,
        string tool,
        string arguments,
        ProcessExecutionContext context,
        Action<string>? onOutputLine = null,
        Action<string>? onErrorLine = null,
        Func<bool>? isCancellationRequested = null,
        CancellationToken cancellationToken = default,
        string? workingDir = null,
        string? expectedArtifact = null,
        int maxSuccessExitCode = 0,
        Func<bool>? verifyPostcondition = null,
        TimeSpan? timeout = null)
    {
        ProcessExecutionContext executionContext = string.IsNullOrWhiteSpace(expectedArtifact)
            ? context
            : context.WithExpectedArtifact(expectedArtifact);
        string processId = ProcessExecutionContext.CreateProcessId();

        if (string.IsNullOrWhiteSpace(binaryPath) || !File.Exists(binaryPath))
        {
            ProcessResult missing = ProcessResult.NotStarted(
                executionContext,
                ProcessResult.ErrorBinaryMissing,
                $"Отсутствует исполняемый файл внешней утилиты '{tool}'",
                processId: processId);
            WriteEvent(ProcessEventIds.StartFailed, LogLevel.Error, LogStatus.Failed, missing.Message, executionContext, missing, includeOutputTail: false);
            return missing;
        }

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

        int argumentCount = ProcessExecutionContext.CountArguments(arguments);
        string workingDirLabel = ProcessExecutionContext.LabelDirectory(workingDir);
        string commandLine = $"\"{binaryPath}\" {arguments}";

        return await RunCoreAsync(
            startInfo,
            tool,
            executionContext,
            processId,
            commandLine,
            argumentCount,
            workingDirLabel,
            onOutputLine,
            onErrorLine,
            isCancellationRequested,
            cancellationToken,
            expectedArtifact,
            maxSuccessExitCode,
            verifyPostcondition,
            timeout).ConfigureAwait(false);
    }

    /// <summary>
    /// Запускает внешний процесс со списком аргументов через <c>ProcessStartInfo.ArgumentList</c>.
    /// Каждый элемент списка передаётся процессу ровно одним аргументом без разбора кавычек,
    /// поэтому враждебные значения (ссылки, коды субтитров) не могут добавить произвольные флаги.
    /// </summary>
    public async Task<ProcessResult> RunAsync(
        string binaryPath,
        string tool,
        IReadOnlyList<string> argumentList,
        ProcessExecutionContext context,
        Action<string>? onOutputLine = null,
        Action<string>? onErrorLine = null,
        Func<bool>? isCancellationRequested = null,
        CancellationToken cancellationToken = default,
        string? workingDir = null,
        string? expectedArtifact = null,
        int maxSuccessExitCode = 0,
        Func<bool>? verifyPostcondition = null,
        TimeSpan? timeout = null)
    {
        // Проверка аргументов выполняется в начале: пустой список недопустим.
        ArgumentNullException.ThrowIfNull(argumentList);

        ProcessExecutionContext executionContext = string.IsNullOrWhiteSpace(expectedArtifact)
            ? context
            : context.WithExpectedArtifact(expectedArtifact);
        string processId = ProcessExecutionContext.CreateProcessId();

        if (string.IsNullOrWhiteSpace(binaryPath) || !File.Exists(binaryPath))
        {
            ProcessResult missing = ProcessResult.NotStarted(
                executionContext,
                ProcessResult.ErrorBinaryMissing,
                $"Отсутствует исполняемый файл внешней утилиты '{tool}'",
                processId: processId);
            WriteEvent(ProcessEventIds.StartFailed, LogLevel.Error, LogStatus.Failed, missing.Message, executionContext, missing, includeOutputTail: false);
            return missing;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = binaryPath,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = ProcessOutputPolicy.ResolveEncoding(),
            StandardErrorEncoding = ProcessOutputPolicy.ResolveEncoding(),
            WorkingDirectory = workingDir ?? Path.GetDirectoryName(binaryPath) ?? AppContext.BaseDirectory
        };

        foreach (string argument in argumentList)
        {
            startInfo.ArgumentList.Add(argument);
        }

        string commandLine = $"\"{binaryPath}\" {BuildCommandLine(argumentList)}";
        string workingDirLabel = ProcessExecutionContext.LabelDirectory(workingDir);

        return await RunCoreAsync(
            startInfo,
            tool,
            executionContext,
            processId,
            commandLine,
            argumentList.Count,
            workingDirLabel,
            onOutputLine,
            onErrorLine,
            isCancellationRequested,
            cancellationToken,
            expectedArtifact,
            maxSuccessExitCode,
            verifyPostcondition,
            timeout).ConfigureAwait(false);
    }

    /// <summary>
    /// Экранирует одиночный аргумент по правилам <c>CommandLineToArgvW</c>:
    /// значение в кавычках с удвоением обратных слешей перед кавычками.
    /// </summary>
    public static string EscapeArgument(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        bool needQuotes = false;
        foreach (char character in value)
        {
            if (char.IsWhiteSpace(character) || character == '"')
            {
                needQuotes = true;
                break;
            }
        }

        if (!needQuotes)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        int backslashes = 0;
        foreach (char character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', (backslashes * 2) + 1);
                builder.Append('"');
                backslashes = 0;
                continue;
            }

            if (backslashes > 0)
            {
                builder.Append('\\', backslashes);
                backslashes = 0;
            }

            builder.Append(character);
        }

        if (backslashes > 0)
        {
            builder.Append('\\', backslashes * 2);
        }

        builder.Append('"');
        return builder.ToString();
    }

    /// <summary>
    /// Собирает отображаемую командную строку из списка аргументов для журнала.
    /// </summary>
    public static string BuildCommandLine(IEnumerable<string>? arguments)
    {
        if (arguments is null)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (string argument in arguments)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(EscapeArgument(argument));
        }

        return builder.ToString();
    }

    private async Task<ProcessResult> RunCoreAsync(
        ProcessStartInfo startInfo,
        string tool,
        ProcessExecutionContext executionContext,
        string processId,
        string commandLine,
        int argumentCount,
        string workingDirLabel,
        Action<string>? onOutputLine,
        Action<string>? onErrorLine,
        Func<bool>? isCancellationRequested,
        CancellationToken cancellationToken,
        string? expectedArtifact,
        int maxSuccessExitCode,
        Func<bool>? verifyPostcondition,
        TimeSpan? timeout = null)
    {
        // Невалидный лимит отклоняется сразу: ждать ноль секунд или вечность по ошибке нельзя.
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Лимит времени выполнения должен быть положительным");
        }

        using var process = new Process { StartInfo = startInfo };
        ProcessOutputBuffer stdoutBuffer = new() { Stream = "stdout" };
        ProcessOutputBuffer stderrBuffer = new() { Stream = "stderr" };
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
            WriteEvent(ProcessEventIds.StartFailed, LogLevel.Error, LogStatus.Failed, startFailure.Message, executionContext, startFailure, ex);
            return startFailure;
        }

        // Привязка к заданию с KILL_ON_JOB_CLOSE идёт сразу после запуска:
        // ОС завершит дочерний процесс при гибели приложения даже без штатного завершения.
        // Отказ безопасен: кооперативное завершение через реестр продолжает работать.
        ActiveProcessTracker.TryAttachToJob(process);
        ActiveProcessTracker.Register(process);
        int osPid = SafePid(process);

        WriteEvent(
            osPid > 0 ? ProcessEventIds.Started : ProcessEventIds.Launched,
            LogLevel.Info,
            LogStatus.Running,
            $"Запущен внешний процесс '{tool}'",
            executionContext,
            result: null,
            processId: processId,
            pid: osPid,
            properties: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["CommandLine"] = commandLine,
                ["ArgumentCount"] = argumentCount,
                ["WorkingDirLabel"] = workingDirLabel
            });

        bool cancelled = false;
        using var poller = new CancellationPoller(isCancellationRequested, () =>
        {
            if (!SafeHasExited(process))
            {
                cancelled = true;
            }
        });
        poller.Start();

        try
        {
            Task stdoutTask = PumpAsync(process.StandardOutput, stdoutBuffer, onOutputLine, isCancellationRequested, process, () => cancelled = true);
            Task stderrTask = PumpAsync(process.StandardError, stderrBuffer, onErrorLine, isCancellationRequested, process, () => cancelled = true);

            bool timedOut = false;
            try
            {
                if (timeout is null)
                {
                    await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    timedOut = await WaitForExitWithTimeoutAsync(process, timeout.Value, cancellationToken).ConfigureAwait(false);
                    if (timedOut)
                    {
                        cancelled = true;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }

            if (cancelled)
            {
                bool alreadyExited = SafeHasExited(process);
                ProcessTerminationResult? termination = alreadyExited
                    ? null
                    : ActiveProcessTracker.TerminateProcess(process, TimeSpan.FromMilliseconds(KillWaitMilliseconds));
                int? terminatedExit = termination?.ExitCode ?? SafeExitCode(process);
                int terminatedPid = termination is not null && termination.Pid > 0 ? termination.Pid : osPid;
                bool verified = alreadyExited || (termination is not null && termination.Verified);
                await Task.WhenAll(Swallow(stdoutTask), Swallow(stderrTask)).ConfigureAwait(false);
                stopwatch.Stop();

                // Срыв лимита времени — отдельная причина остановки: процесс уже
                // доказанно завершён через TerminateProcess, отличается только диагноз.
                string cancelMessage = timedOut
                    ? ProcessResult.MessageTimeoutExceeded
                    : verified ? ProcessResult.MessageCancelled : ProcessResult.MessageUnverifiedTermination;
                string cancelErrorCode = timedOut
                    ? ProcessResult.ErrorTimeoutExceeded
                    : verified ? ProcessResult.ErrorCancelled : ProcessResult.ErrorTerminationUnverified;

                ProcessResult cancelResult = ProcessResult.Cancelled(
                    executionContext,
                    cancelMessage,
                    processId,
                    terminatedPid,
                    terminatedExit,
                    stopwatch.Elapsed.TotalMilliseconds,
                    string.IsNullOrWhiteSpace(expectedArtifact) ? null : FileExistsNonEmpty(expectedArtifact),
                    stderrBuffer.BuildTail(),
                    stderrBuffer.Truncated || stdoutBuffer.Truncated,
                    ProcessOutputPolicy.CountWarnings(stderrBuffer.Snapshot()),
                    ProcessOutputPolicy.CountErrors(stderrBuffer.Snapshot()),
                    terminationVerified: verified,
                    errorCode: cancelErrorCode,
                    cleanupState: verified
                        ? (string.IsNullOrWhiteSpace(expectedArtifact) || FileExistsNonEmpty(expectedArtifact) == true
                            ? CleanupState.Completed
                            : CleanupState.Partial)
                        : CleanupState.Partial,
                    outputMetrics: ProcessOutputMetrics.Combine(stderrBuffer.Metrics(), stdoutBuffer.Metrics()));

                WriteEvent(
                    timedOut
                        ? ProcessEventIds.TimeoutExceeded
                        : verified ? ProcessEventIds.Cancelled : ProcessEventIds.TerminationUnverified,
                    timedOut || !verified ? LogLevel.Warning : LogLevel.Info,
                    LogStatus.Cancelled,
                    cancelResult.Message,
                    executionContext,
                    cancelResult,
                    properties: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["CommandLine"] = commandLine,
                        ["ArgumentCount"] = argumentCount,
                        ["WorkingDirLabel"] = workingDirLabel
                    });
                return cancelResult;
            }

            await Task.WhenAll(Swallow(stdoutTask), Swallow(stderrTask)).ConfigureAwait(false);
        }
        finally
        {
            ActiveProcessTracker.Unregister(process);
        }

        stopwatch.Stop();
        int? exitCode = SafeExitCode(process);
        double durationMs = stopwatch.Elapsed.TotalMilliseconds;
        string tail = stderrBuffer.BuildTail();
        string combinedTail = tail.Length > 0 ? tail : stdoutBuffer.BuildTail();
        bool truncated = stdoutBuffer.Truncated || stderrBuffer.Truncated;
        int warnings = ProcessOutputPolicy.CountWarnings(stderrBuffer.Snapshot());
        int errorLines = ProcessOutputPolicy.CountErrors(stderrBuffer.Snapshot());
        bool? outputExists = string.IsNullOrWhiteSpace(expectedArtifact) ? null : FileExistsNonEmpty(expectedArtifact);
        ProcessOutputMetrics metrics = ProcessOutputMetrics.Combine(stderrBuffer.Metrics(), stdoutBuffer.Metrics());

        ProcessResult result;
        if (!exitCode.HasValue || exitCode.Value > Math.Max(0, maxSuccessExitCode))
        {
            result = ProcessResult.Failed(
                executionContext,
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
                errorLines);
        }
        else if (outputExists == false)
        {
            result = ProcessResult.Failed(
                executionContext,
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
                errorLines);
        }
        else if (verifyPostcondition is not null && !verifyPostcondition())
        {
            result = ProcessResult.Failed(
                executionContext,
                ProcessResult.ErrorParseInvalid,
                $"Внешний процесс '{tool}' завершился без кода ошибки, но результат операции не подтверждён",
                processId,
                osPid,
                exitCode,
                durationMs,
                outputExists,
                combinedTail,
                truncated,
                warnings,
                errorLines);
        }
        else if (exitCode.Value > 0 || warnings > 0)
        {
            result = ProcessResult.PartiallySucceeded(
                executionContext,
                $"Внешний процесс '{tool}' завершён с предупреждениями",
                processId,
                osPid,
                exitCode,
                durationMs,
                outputExists,
                combinedTail,
                truncated,
                warnings,
                errorLines);
        }
        else
        {
            result = ProcessResult.Succeeded(
                executionContext,
                processId,
                osPid,
                exitCode,
                durationMs,
                outputExists,
                combinedTail,
                truncated,
                warnings,
                errorLines);
        }

        result = result with { OutputMetrics = metrics };
        bool exitCodeKnown = osPid > 0 && exitCode.HasValue;

        var exitProperties = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["CommandLine"] = commandLine,
            ["ArgumentCount"] = argumentCount,
            ["WorkingDirLabel"] = workingDirLabel
        };

        if (!exitCodeKnown)
        {
            exitProperties["ExitCode"] = "unavailable";
        }

        WriteEvent(
            exitCodeKnown ? ProcessEventIds.Exit : ProcessEventIds.Completed,
            result.Status switch
            {
                LogStatus.Succeeded => LogLevel.Info,
                LogStatus.PartiallySucceeded => LogLevel.Warning,
                _ => LogLevel.Error
            },
            result.Status,
            result.Message,
            executionContext,
            result,
            properties: exitProperties);
        return result;
    }

    private static async Task Swallow(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Ожидает выхода процесса с ограничением по времени.
    /// Возвращает true, если сработал лимит; отмена через токен пробрасывается исключением.
    /// </summary>
    private static async Task<bool> WaitForExitWithTimeoutAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return true;
        }
    }

    private static async Task PumpAsync(
        StreamReader reader,
        ProcessOutputBuffer buffer,
        Action<string>? lineSink,
        Func<bool>? isCancellationRequested,
        Process process,
        Action markCancelled)
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
                            Dispatch(accumulator.ToString());
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
                Dispatch(accumulator.ToString());
            }
        }
        catch (Exception)
        {
        }

        void Dispatch(string rawLine)
        {
            accumulator.Clear();

            // Парсерам передаётся исходная строка: нормализация склеивает пробелы
            // и обрезает по длине, из-за чего разбор JSON и значений ломается.
            try
            {
                lineSink?.Invoke(rawLine);
            }
            catch (Exception)
            {
            }

            if (isCancellationRequested is not null && isCancellationRequested())
            {
                markCancelled();
            }

            string line = ProcessOutputPolicy.NormalizeLine(rawLine);
            if (line.Length == 0)
            {
                return;
            }

            buffer.Append(line);
        }
    }

    private static bool FileExistsNonEmpty(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void WriteEvent(
        string eventId,
        LogLevel level,
        LogStatus status,
        string message,
        ProcessExecutionContext context,
        ProcessResult? result,
        Exception? exception = null,
        bool includeOutputTail = true,
        string? processId = null,
        int? pid = null,
        IReadOnlyDictionary<string, object?>? properties = null)
    {
        try
        {
            Dictionary<string, object?> merged = result is null
                ? context.ToLogProperties()
                : result.ToLogProperties(includeOutputTail);

            if (result is null)
            {
                if (!string.IsNullOrEmpty(processId))
                {
                    merged["ProcessId"] = processId;
                }

                if (pid is > 0)
                {
                    merged["Pid"] = pid.Value;
                }
            }

            if (properties is not null)
            {
                foreach (KeyValuePair<string, object?> pair in properties)
                {
                    merged[pair.Key] = pair.Value;
                }
            }

            ExceptionInfo? info = null;
            if (exception is not null)
            {
                try
                {
                    info = ExceptionInfo.FromException(exception);
                }
                catch (Exception)
                {
                    info = null;
                }
            }

            _log.Write(new LogEvent
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
                ExitCode = result?.ExitCode,
                DurationMs = result is { DurationMs: > 0 } ? result.DurationMs : null,
                ErrorCode = result?.ErrorCode,
                Properties = merged,
                Exception = info
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

    private static bool SafeHasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private sealed class CancellationPoller : IDisposable
    {
        private readonly Func<bool>? _isCancellationRequested;
        private readonly Action _onCancelled;
        private readonly CancellationTokenSource _stop = new();
        private Task? _loop;

        public CancellationPoller(Func<bool>? isCancellationRequested, Action onCancelled)
        {
            _isCancellationRequested = isCancellationRequested;
            _onCancelled = onCancelled;
        }

        public void Start()
        {
            if (_isCancellationRequested is null)
            {
                return;
            }

            _loop = Task.Run(async () =>
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        bool requested;
                        try
                        {
                            requested = _isCancellationRequested();
                        }
                        catch (Exception)
                        {
                            return;
                        }

                        if (requested)
                        {
                            try
                            {
                                _onCancelled();
                            }
                            catch (Exception)
                            {
                            }

                            return;
                        }

                        await Task.Delay(PollIntervalMilliseconds, _stop.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception)
                {
                }
            });
        }

        public void Dispose()
        {
            try
            {
                _stop.Cancel();
                _stop.Dispose();
            }
            catch (Exception)
            {
            }

            _ = _loop;
        }
    }
}
