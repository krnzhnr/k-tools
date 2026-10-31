// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Services.Contracts;

namespace KTools_App.Infrastructure;

/// <summary>
/// Синглтон-обертка для запуска кодировщика Apple AAC (qaac64.exe) через конвейер с FFmpeg.
/// </summary>
public sealed class QaacRunner
{
    private const string SourceName = nameof(QaacRunner);
    private readonly ILogService _logService;
    private readonly IPathManager _pathManager;

    private static readonly object TempEnvLock = new();
    private static string? _cachedTempDir;

    /// <summary>
    /// Инициализирует новый экземпляр QaacRunner с внедрением зависимостей.
    /// </summary>
    /// <param name="logService">Сервис логирования.</param>
    /// <param name="pathManager">Менеджер путей.</param>
    public QaacRunner(ILogService logService, IPathManager pathManager)
    {
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _pathManager = pathManager ?? throw new ArgumentNullException(nameof(pathManager));
    }

    /// <summary>
    /// Запустить кодирование AAC через потоковый конвейер FFmpeg | QAAC64.
    /// </summary>
    public async Task<ProcessResult> RunAsync(
        string inputPath,
        string outputPath,
        string tvbr = "127",
        bool adts = false,
        List<string>? extraArgs = null,
        double totalDuration = 0.0,
        Action<ProgressInfo>? onProgress = null,
        CancellationToken cancellationToken = default,
        string mode = "True VBR (-V)",
        string qualityOrBitrate = "127",
        bool noDelay = false,
        bool limiter = false,
        ProcessExecutionContext? context = null)
    {
        string inputName = Path.GetFileName(inputPath);
        string outputName = Path.GetFileName(outputPath);
        ProcessExecutionContext executionContext = (context ?? ProcessExecutionContext.NewOperation("qaac64"))
            .WithExpectedArtifact(outputPath);
        string pipelineId = ProcessExecutionContext.CreateProcessId();

        string qaacPath = _pathManager.GetBinaryPath("qaac64");
        string ffmpegPath = _pathManager.GetBinaryPath("ffmpeg");

        if (!File.Exists(qaacPath))
        {
            return ProcessResult.NotStarted(
                executionContext,
                ProcessResult.ErrorBinaryMissing,
                "Отсутствует кодировщик qaac64");
        }

        if (!File.Exists(ffmpegPath))
        {
            return ProcessResult.NotStarted(
                executionContext,
                ProcessResult.ErrorBinaryMissing,
                "Отсутствует декодер ffmpeg");
        }

        // 1. Создаем изолированную временную папку для обхода ограничений AppContainer (WinUI 3 MSIX).
        string tempDir;
        string tempQaacPath;

        lock (TempEnvLock)
        {
            try
            {
                if (string.IsNullOrEmpty(_cachedTempDir))
                {
                    _cachedTempDir = Path.Combine(Path.GetTempPath(), "KTools_Qaac_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(_cachedTempDir);
                }

                tempDir = _cachedTempDir;

                // Копируем исполняемый файл во временную директорию
                if (!File.Exists(Path.Combine(tempDir, "qaac64.exe")))
                {
                    File.Copy(qaacPath, Path.Combine(tempDir, "qaac64.exe"), true);
                }

                // Копируем все DLL библиотеки Apple из подпапки QTfiles64 / QTFiles64
                string baseDir = Path.GetDirectoryName(qaacPath) ?? AppContext.BaseDirectory;
                string[] sourceSubfolders = { "QTfiles64", "QTFiles64", "QTFiles", "QTfiles" };
                string? sourceDir = null;

                foreach (var subfolder in sourceSubfolders)
                {
                    string path = Path.Combine(baseDir, subfolder);
                    if (Directory.Exists(path) && File.Exists(Path.Combine(path, "CoreAudioToolbox.dll")))
                    {
                        sourceDir = path;
                        break;
                    }
                }

                if (sourceDir == null)
                {
                    _logService.Write(
                        "qaac.runtime_incomplete",
                        LogLevel.Warning,
                        LogStatus.PartiallySucceeded,
                        "Не найдена папка QTfiles64 с библиотеками Apple Application Support. Возможен сбой запуска",
                        null,
                        "QaacRunner",
                        executionContext.ToLogContext(),
                        new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["Stage"] = "runtime_prepare"
                        });
                }
                else
                {
                    var dllFiles = Directory.GetFiles(sourceDir, "*.dll");
                    foreach (var dllFile in dllFiles)
                    {
                        string dllDestPath = Path.Combine(tempDir, Path.GetFileName(dllFile));
                        if (!File.Exists(dllDestPath))
                        {
                            File.Copy(dllFile, dllDestPath, true);
                        }
                    }
                }

                tempQaacPath = Path.Combine(tempDir, "qaac64.exe");
            }
            catch (Exception ex)
            {
                _logService.Write(
                    "qaac.runtime_prepare_failed",
                    LogLevel.Error,
                    LogStatus.Failed,
                    "Не удалось подготовить изолированное временное окружение для QAAC",
                    ex,
                    "QaacRunner",
                    executionContext.ToLogContext(),
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["Stage"] = "runtime_prepare",
                        ["ErrorCode"] = "runtime-prepare-failed"
                    });
                // Фоллбэк: пробуем запуск из оригинальной папки
                tempQaacPath = qaacPath;
                tempDir = Path.GetDirectoryName(qaacPath) ?? AppContext.BaseDirectory;
            }
        }

        // 2. Формируем аргументы для FFmpeg (декодирование в WAV и вывод в stdout)
        string ffmpegArgs = $"-v error -i \"{inputPath}\" -f wav -";

        // 3. Формируем аргументы для QAAC (чтение из stdin и запись в файл)
        var qaacArgsList = new List<string>();
        if (adts)
        {
            qaacArgsList.Add("--adts");
        }

        if (noDelay)
        {
            qaacArgsList.Add("--no-delay");
        }

        if (limiter)
        {
            qaacArgsList.Add("--limiter");
        }

        // Очищаем значение битрейта если выбран с суффиксом " (Авто/Максимальный)"
        string cleanVal = qualityOrBitrate;
        if (cleanVal.Contains(" "))
        {
            cleanVal = cleanVal.Split(' ')[0];
        }

        if (mode.StartsWith("Constrained VBR", StringComparison.OrdinalIgnoreCase))
        {
            qaacArgsList.Add("-v");
            qaacArgsList.Add(cleanVal);
        }
        else if (mode.StartsWith("ABR", StringComparison.OrdinalIgnoreCase))
        {
            qaacArgsList.Add("-a");
            qaacArgsList.Add(cleanVal);
        }
        else if (mode.StartsWith("CBR", StringComparison.OrdinalIgnoreCase))
        {
            qaacArgsList.Add("-c");
            qaacArgsList.Add(cleanVal);
        }
        else if (mode.StartsWith("HE AAC", StringComparison.OrdinalIgnoreCase))
        {
            qaacArgsList.Add("--he");
            qaacArgsList.Add("-v");
            qaacArgsList.Add(cleanVal);
        }
        else
        {
            // По умолчанию True VBR (-V)
            qaacArgsList.Add("-V");
            qaacArgsList.Add(cleanVal);
        }

        qaacArgsList.Add("-");
        qaacArgsList.Add("-o");
        qaacArgsList.Add($"\"{outputPath}\"");

        if (extraArgs != null)
        {
            qaacArgsList.AddRange(extraArgs);
        }

        string qaacArgs = string.Join(" ", qaacArgsList);

        // 4. Настройка процессов
        var ffmpegStartInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = ffmpegArgs,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = ProcessOutputPolicy.ResolveEncoding(),
            WorkingDirectory = Path.GetDirectoryName(ffmpegPath) ?? AppContext.BaseDirectory
        };

        var qaacStartInfo = new ProcessStartInfo
        {
            FileName = tempQaacPath,
            Arguments = qaacArgs,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = ProcessOutputPolicy.ResolveEncoding(),
            WorkingDirectory = tempDir
        };

        // Заполняем переменную PATH во временном процессе
        SetupQaacEnvironment(qaacStartInfo, tempDir);

        using var ffmpegProc = new Process { StartInfo = ffmpegStartInfo };
        using var qaacProc = new Process { StartInfo = qaacStartInfo };

        string ffmpegProcessId = ProcessExecutionContext.CreateProcessId();
        string qaacProcessId = ProcessExecutionContext.CreateProcessId();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            ffmpegProc.Start();
            qaacProc.Start();
            ActiveProcessTracker.Register(ffmpegProc);
            ActiveProcessTracker.Register(qaacProc);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            TryTerminate(ffmpegProc);
            TryTerminate(qaacProc);
            CleanupTempDir(tempDir, executionContext);
            _logService.Write(
                ProcessEventIds.StartFailed,
                LogLevel.Error,
                LogStatus.Failed,
                "Не удалось запустить процессы конвейера QAAC",
                ex,
                "QaacRunner",
                executionContext.ToLogContext(),
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["ProcessId"] = pipelineId,
                    ["ErrorCode"] = ProcessResult.ErrorStartFailed
                });
            return ProcessResult.Failed(
                executionContext,
                ProcessResult.ErrorStartFailed,
                "Не удалось запустить процессы конвейера QAAC",
                pipelineId,
                pid: null,
                durationMs: stopwatch.Elapsed.TotalMilliseconds,
                exception: ex);
        }

        WritePipelineEvent(
            ProcessEventIds.Started,
            LogLevel.Info,
            LogStatus.Running,
            $"Запущен декодер конвейера AAC для '{inputName}'",
            executionContext,
            ffmpegProcessId,
            ffmpegProc,
            "ffmpeg",
            PipelineLaunchProperties(ffmpegArgs, ffmpegPath));

        WritePipelineEvent(
            ProcessEventIds.Started,
            LogLevel.Info,
            LogStatus.Running,
            $"Запущен кодировщик AAC для '{inputName}' (режим: {mode}, no-delay: {noDelay}, limiter: {limiter})",
            executionContext,
            qaacProcessId,
            qaacProc,
            "qaac64",
            PipelineLaunchProperties(qaacArgs, tempQaacPath));

        ProcessOutputBuffer ffmpegErrors = new() { Stream = "ffmpeg-stderr" };
        ProcessOutputBuffer qaacErrors = new() { Stream = "qaac-stderr" };
        ProcessOutputBuffer qaacWarnings = new() { Stream = "qaac-warnings" };

        Task ffmpegErrorTask = ReadBoundedAsync(ffmpegProc.StandardError, ffmpegErrors, null);
        Task qaacErrorTask = ReadBoundedAsync(
            qaacProc.StandardError,
            qaacErrors,
            line =>
            {
                if (onProgress is not null)
                {
                    var progress = QaacOutputParser.ParseLine(line, totalDuration);
                    if (progress != null)
                    {
                        onProgress(progress);
                    }
                }
            });

        // 5. Конвейеризация данных из stdout FFmpeg в stdin QAAC
        var pipeTask = Task.Run(async () =>
        {
            try
            {
                byte[] buffer = new byte[65536];
                using var input = ffmpegProc.StandardOutput.BaseStream;
                using var output = qaacProc.StandardInput.BaseStream;

                int bytesRead;
                while ((bytesRead = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logService.Write(
                    "qaac.pipeline_transfer_failed",
                    LogLevel.Error,
                    LogStatus.Failed,
                    "Ошибка конвейерной передачи данных в конвейере AAC",
                    ex,
                    "QaacRunner",
                    executionContext.ToLogContext(),
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["ErrorCode"] = "QAAC_PIPELINE_TRANSFER_FAILED",
                        ["ProcessId"] = pipelineId,
                        ["Stage"] = "pipe"
                    });
            }
            finally
            {
                try
                {
                    qaacProc.StandardInput.BaseStream.Close();
                }
                catch (Exception)
                {
                }
            }
        });

        // Ожидание завершения процессов или отмены
        bool cancelled = false;
        try
        {
            var waitFfmpeg = ffmpegProc.WaitForExitAsync(cancellationToken);
            var waitQaac = qaacProc.WaitForExitAsync(cancellationToken);

            await Task.WhenAll(waitFfmpeg, waitQaac, pipeTask, ffmpegErrorTask, qaacErrorTask);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        stopwatch.Stop();
        ActiveProcessTracker.Unregister(ffmpegProc);
        ActiveProcessTracker.Unregister(qaacProc);

        int? ffmpegExit = SafeExitCode(ffmpegProc);
        int? qaacExit = SafeExitCode(qaacProc);
        double durationMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3);

        WritePipelineEvent(
            PipelineOutcomeEventId(ffmpegProc, ffmpegExit),
            cancelled ? LogLevel.Info : ResolvePipelineLevel(ffmpegExit, qaacExit),
            cancelled ? LogStatus.Cancelled : ResolvePipelineStatus(ffmpegExit, qaacExit),
            cancelled
                ? "Конвейер кодирования AAC прерван по запросу отмены"
                : $"Декодер конвейера AAC завершён: {DescribeExit(ffmpegExit)}",
            executionContext,
            ffmpegProcessId,
            ffmpegProc,
            "ffmpeg",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Stage"] = "pipeline_ffmpeg",
                ["DurationMs"] = durationMs
            },
            ffmpegExit,
            durationMs);

        WritePipelineEvent(
            PipelineOutcomeEventId(qaacProc, qaacExit),
            cancelled ? LogLevel.Info : ResolvePipelineLevel(ffmpegExit, qaacExit),
            cancelled ? LogStatus.Cancelled : ResolvePipelineStatus(ffmpegExit, qaacExit),
            cancelled
                ? "Конвейер кодирования AAC прерван по запросу отмены"
                : $"Кодировщик AAC завершён: {DescribeExit(qaacExit)}",
            executionContext,
            qaacProcessId,
            qaacProc,
            "qaac64",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Stage"] = "pipeline_qaac",
                ["DurationMs"] = durationMs,
                ["WarningCount"] = ProcessOutputPolicy.CountWarnings(qaacErrors.Snapshot()),
                ["OutputTail"] = qaacErrors.BuildTail(),
                ["OutputTruncated"] = qaacErrors.Truncated
            },
            qaacExit,
            durationMs);

        if (cancelled)
        {
            TryTerminate(ffmpegProc);
            TryTerminate(qaacProc);
            DeletePartialOutput(outputPath, outputName, executionContext, pipelineId);
            CleanupTempDir(tempDir, executionContext);
            return ProcessResult.Cancelled(
                executionContext,
                ProcessResult.MessageCancelled,
                pipelineId,
                SafePid(ffmpegProc),
                qaacExit ?? ffmpegExit,
                durationMs,
                false,
                qaacErrors.BuildTail(),
                qaacErrors.Truncated,
                ProcessOutputPolicy.CountWarnings(qaacErrors.Snapshot()),
                ProcessOutputPolicy.CountErrors(qaacErrors.Snapshot()),
                terminationVerified: true);
        }

        bool artifactExists = FileExistsNonEmpty(outputPath);
        bool success = ffmpegExit == 0 && qaacExit == 0 && artifactExists;
        int warnings = ProcessOutputPolicy.CountWarnings(qaacErrors.Snapshot());
        string tail = qaacErrors.BuildTail();

        if (!success)
        {
            string errorCode = ffmpegExit != 0
                ? "ffmpeg-failed"
                : qaacExit != 0
                    ? "qaac-failed"
                    : ProcessResult.ErrorArtifactMissing;

            if (artifactExists)
            {
                DeletePartialOutput(outputPath, outputName, executionContext, pipelineId);
            }

            CleanupTempDir(tempDir, executionContext);

            return ProcessResult.Failed(
                executionContext,
                errorCode,
                $"Сбой конвейера кодирования AAC: ffmpeg={DescribeExit(ffmpegExit)}, qaac64={DescribeExit(qaacExit)}, артефакт={(artifactExists ? "создан" : "отсутствует")}",
                pipelineId,
                SafePid(ffmpegProc),
                qaacExit ?? ffmpegExit,
                durationMs,
                artifactExists,
                tail,
                qaacErrors.Truncated,
                warnings,
                ProcessOutputPolicy.CountErrors(qaacErrors.Snapshot()));
        }

        CleanupTempDir(tempDir, executionContext);

        if (warnings > 0)
        {
            return ProcessResult.PartiallySucceeded(
                executionContext,
                $"Конвейер кодирования AAC завершён с предупреждениями: '{outputName}'",
                pipelineId,
                SafePid(ffmpegProc),
                qaacExit,
                durationMs,
                true,
                tail,
                qaacErrors.Truncated,
                warnings,
                0);
        }

        return ProcessResult.Succeeded(
            executionContext,
            pipelineId,
            SafePid(ffmpegProc),
            qaacExit,
            durationMs,
            true,
            tail,
            qaacErrors.Truncated,
            0,
            0);
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

    private static string PipelineOutcomeEventId(Process process, int? exitCode) =>
        SafePid(process) > 0 && exitCode.HasValue ? ProcessEventIds.Exit : ProcessEventIds.Completed;

    private static async Task ReadBoundedAsync(StreamReader reader, ProcessOutputBuffer buffer, Action<string>? lineSink)
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

                    if (accumulator.Length < ProcessOutputPolicy.MaxSingleLineLength)
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
            string line = ProcessOutputPolicy.NormalizeLine(rawLine);
            if (line.Length == 0)
            {
                return;
            }

            buffer.Append(line);
            if (lineSink is null)
            {
                return;
            }

            try
            {
                lineSink(line);
            }
            catch (Exception)
            {
            }
        }
    }

    private static Dictionary<string, object?> PipelineLaunchProperties(string arguments, string binaryPath)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ArgumentCount"] = ProcessExecutionContext.CountArguments(arguments),
            ["WorkingDirLabel"] = ProcessExecutionContext.LabelDirectory(Path.GetDirectoryName(binaryPath))
        };
    }

    private void WritePipelineEvent(
        string eventId,
        LogLevel level,
        LogStatus status,
        string message,
        ProcessExecutionContext executionContext,
        string processId,
        Process process,
        string tool,
        IReadOnlyDictionary<string, object?> properties,
        int? exitCode = null,
        double? durationMs = null)
    {
        try
        {
            Dictionary<string, object?> merged = executionContext.ToLogProperties();
            merged["ProcessId"] = processId;
            merged["Tool"] = tool;
            foreach (KeyValuePair<string, object?> pair in properties)
            {
                merged[pair.Key] = pair.Value;
            }

            int pid = SafePid(process);
            _logService.Write(new LogEvent
            {
                EventId = eventId,
                Level = level,
                Status = status,
                Source = "QaacRunner",
                Message = message,
                OperationId = executionContext.OperationId,
                ItemId = executionContext.ItemId,
                ProcessId = processId,
                Pid = pid > 0 ? pid : null,
                Attempt = executionContext.Attempt,
                Tool = tool,
                ExitCode = exitCode,
                DurationMs = durationMs,
                Properties = merged
            });
        }
        catch (Exception)
        {
        }
    }

    private static LogLevel ResolvePipelineLevel(int? ffmpegExit, int? qaacExit)
    {
        if (ffmpegExit == 0 && qaacExit == 0)
        {
            return LogLevel.Info;
        }

        return LogLevel.Error;
    }

    private static LogStatus ResolvePipelineStatus(int? ffmpegExit, int? qaacExit)
    {
        return ffmpegExit == 0 && qaacExit == 0 ? LogStatus.Succeeded : LogStatus.Failed;
    }

    private static string DescribeExit(int? exitCode) =>
        exitCode.HasValue ? exitCode.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown";

    private static int SafePid(Process process)
    {
        try
        {
            return process.HasExited ? process.Id : process.Id;
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

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
        }
    }

    private void DeletePartialOutput(
        string outputPath,
        string outputName,
        ProcessExecutionContext executionContext,
        string pipelineId)
    {
        if (!File.Exists(outputPath))
        {
            return;
        }

        try
        {
            File.Delete(outputPath);
            _logService.Write(
                ProcessEventIds.ArtifactMissing,
                LogLevel.Debug,
                LogStatus.Succeeded,
                $"Неполный выходной файл конвейера AAC удалён: '{outputName}'",
                source: SourceName,
                context: executionContext.ToLogContext(),
                properties: LogProps
                    .Create("OutputName", LogProps.FileName(outputName))
                    .With("CleanupState", "Removed"));
        }
        catch (Exception ex)
        {
            _logService.Write(
                ProcessEventIds.ArtifactMissing,
                LogLevel.Warning,
                LogStatus.PartiallySucceeded,
                $"Не удалось удалить неполный выходной файл '{outputName}' конвейера AAC",
                ex,
                "QaacRunner",
                executionContext.ToLogContext(),
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["ProcessId"] = pipelineId,
                    ["Stage"] = "artifact_cleanup",
                    ["ErrorCode"] = ProcessResult.ErrorArtifactMissing
                });
        }
    }

    /// <summary>
    /// Настраивает PATH для Apple Application Support в окружении процесса QAAC.
    /// </summary>
    private void SetupQaacEnvironment(ProcessStartInfo qaacStartInfo, string tempDir)
    {
        string pathKey = "PATH";
        string currentPath = string.Empty;

        foreach (var key in qaacStartInfo.Environment.Keys)
        {
            if (key.Equals("PATH", StringComparison.OrdinalIgnoreCase))
            {
                pathKey = key;
                currentPath = qaacStartInfo.Environment[key] ?? string.Empty;
                break;
            }
        }

        if (string.IsNullOrEmpty(currentPath))
        {
            currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        }

        string newPath = tempDir + Path.PathSeparator + currentPath;
        qaacStartInfo.Environment[pathKey] = newPath;
    }

    /// <summary>
    /// Безопасно удаляет временное изолированное окружение QAAC.
    /// Кэшированное окружение не удаляется, так как переиспользуется между запусками.
    /// </summary>
    private void CleanupTempDir(string tempDir, ProcessExecutionContext executionContext)
    {
        if (string.Equals(tempDir, _cachedTempDir, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Удаляем только созданные нами временные папки внутри системного Temp.
        // При отказе создания окружения tempDir может указывать на установленную
        // папку QAAC — её удаление снесло бы qaac64.exe и библиотеки Apple.
        string tempRoot = Path.GetTempPath();
        string normalizedRoot = tempRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedDir = tempDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!normalizedDir.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(normalizedDir).StartsWith("KTools_Qaac_", StringComparison.OrdinalIgnoreCase))
        {
            _logService.Write(
                "qaac.cleanup.skipped",
                LogLevel.Debug,
                LogStatus.Skipped,
                "Удаление временной папки окружения AAC пропущено: это не созданное приложением окружение",
                source: SourceName,
                context: executionContext.ToLogContext(),
                properties: LogProps
                    .Create("FileName", LogProps.FileName(tempDir))
                    .With("CleanupState", "Skipped"));
            return;
        }

        try
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
        catch (Exception ex)
        {
            _logService.Write(
                "qaac.cleanup_failed",
                LogLevel.Warning,
                LogStatus.PartiallySucceeded,
                "Не удалось удалить временную папку изолированного окружения AAC",
                ex,
                "QaacRunner",
                executionContext.ToLogContext(),
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Stage"] = "cleanup",
                    ["CleanupState"] = "failed"
                });
        }
    }
}
