// -*- coding: utf-8 -*-
using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Services.Contracts;

namespace KTools_App.Infrastructure;

/// <summary>
/// Реализация сервиса запуска и взаимодействия с дочерним процессом whisper-cli.
/// Наследуется от <see cref="AbstractProcessRunner"/> для централизованного контроля запущенных процессов.
/// </summary>
public sealed class WhisperRunner : AbstractProcessRunner, IWhisperRunner
{
    private const string SourceName = nameof(WhisperRunner);

    private readonly IDependencyManager _dependencyManager;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> AsciiModelCache =
        new(System.StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Инициализирует новый экземпляр класса <see cref="WhisperRunner"/> с внедрением зависимостей.
    /// </summary>
    public WhisperRunner(
        ILogService logService,
        IPathManager pathManager,
        IDependencyManager dependencyManager)
        : base(logService, pathManager)
    {
        _dependencyManager = dependencyManager ?? throw new ArgumentNullException(nameof(dependencyManager));
    }

    /// <inheritdoc/>
    public bool IsBackendAvailable(WhisperBackend backend)
    {
        string binaryPath = GetBinaryPathForBackend(backend);
        return File.Exists(binaryPath);
    }

    /// <summary>
    /// Получает абсолютный путь к исполняемому файлу whisper-cli.exe для указанного вычислительного бэкенда.
    /// </summary>
    private string GetBinaryPathForBackend(WhisperBackend backend)
    {
        string subfolder = WhisperBackendDetector.GetSubfolderName(backend);
        string binDir = PathManager.GetBinDirectory();
        string localAppDataBinDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KTools", "bin");
        string baseBinDir = Path.Combine(PathManager.GetBaseDirectory(), "bin");

        string[] candidateDirs = new[] { binDir, localAppDataBinDir, baseBinDir };
        foreach (var dir in candidateDirs)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;

            string fullPath = Path.Combine(dir, subfolder, "whisper-cli.exe");
            if (File.Exists(fullPath))
            {
                return fullPath;
            }
        }

        // По умолчанию возвращаем через PathManager
        return PathManager.GetBinaryPath("whisper-cli.exe");
    }

    /// <inheritdoc/>
    public async Task<ProcessResult> TranscribeAsync(
        WhisperTranscribeOptions options,
        Action<int>? onProgress = null,
        Action<string>? onSegment = null,
        CancellationToken cancellationToken = default,
        ProcessExecutionContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Определяем реальный бэкенд
        WhisperBackend effectiveBackend = options.Backend == WhisperBackend.Auto
            ? WhisperBackendDetector.DetectBestBackend(_dependencyManager)
            : options.Backend;

        string binaryPath = GetBinaryPathForBackend(effectiveBackend);
        ProcessExecutionContext executionContext = (context ?? ProcessExecutionContext.NewOperation("whisper-cli"))
            .WithExpectedArtifact(options.OutputBasePath);

        if (!File.Exists(binaryPath))
        {
            // Попытка фоллбэка на CPU, если запрошенный бэкенд отсутствует
            if (effectiveBackend != WhisperBackend.Cpu)
            {
                Log.Write(
                    "whisper.backend_fallback",
                    LogLevel.Warning,
                    LogStatus.RetryScheduled,
                    $"Рантайм Whisper для бэкенда '{effectiveBackend}' не найден, выполняется переключение на CPU",
                    null,
                    nameof(WhisperRunner),
                    executionContext.ToLogContext(),
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["Stage"] = "backend_select",
                        ["ToolVersion"] = effectiveBackend.ToString()
                    });
                effectiveBackend = WhisperBackend.Cpu;
                binaryPath = GetBinaryPathForBackend(WhisperBackend.Cpu);
            }

            if (!File.Exists(binaryPath))
            {
                return ProcessResult.NotStarted(
                    executionContext,
                    ProcessResult.ErrorBinaryMissing,
                    $"Исполняемый файл whisper-cli не найден. Требуется зависимость '{WhisperBackendDetector.GetDependencyKey(effectiveBackend)}'");
            }
        }

        if (!File.Exists(options.ModelPath))
        {
            return ProcessResult.NotStarted(
                executionContext,
                "model-missing",
                $"Файл модели Whisper не найден: '{Path.GetFileName(options.ModelPath)}'");
        }

        if (!File.Exists(options.InputWavPath))
        {
            return ProcessResult.NotStarted(
                executionContext,
                "input-missing",
                "Входной аудиофайл для распознавания не существует");
        }

        string? tempModelCopy = null;
        string? tempWavCopy = null;
        string? tempVadCopy = null;
        string? tempOutputDir = null;

        try
        {
            // Обеспечиваем ASCII-совместимые пути для whisper-cli (формат 8.3 либо временная копия при отключенных 8.3)
            var (safeModelPath, modelIsTemp) = EnsureAsciiSafePath(options.ModelPath, "ktools_model_", cacheCopy: true);
            if (modelIsTemp) tempModelCopy = safeModelPath;

            var (safeWavPath, wavIsTemp) = EnsureAsciiSafePath(options.InputWavPath, "ktools_wav_");
            if (wavIsTemp) tempWavCopy = safeWavPath;

            string safeVadPath = string.Empty;
            if (options.EnableVad && !string.IsNullOrWhiteSpace(options.VadModelPath) && File.Exists(options.VadModelPath))
            {
                var (safeVad, vadIsTemp) = EnsureAsciiSafePath(options.VadModelPath, "ktools_vad_");
                safeVadPath = safeVad;
                if (vadIsTemp) tempVadCopy = safeVadPath;
            }

            // Изоляция генерации выходных файлов во временной ASCII-директории
            string effectiveOutputBasePath;
            if (!string.IsNullOrWhiteSpace(options.OutputBasePath))
            {
                tempOutputDir = Path.Combine(Path.GetTempPath(), $"ktools_wout_{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempOutputDir);
                effectiveOutputBasePath = Path.Combine(tempOutputDir, "subtitles");
            }
            else
            {
                effectiveOutputBasePath = string.Empty;
            }

            // Формирование аргументов командной строки whisper-cli
            var args = new StringBuilder();

            // 1. Модель и входной файл
            args.Append($"-m \"{safeModelPath}\" ");
            args.Append($"-f \"{safeWavPath}\" ");

            // 2. Язык и перевод
            if (!string.IsNullOrWhiteSpace(options.Language) && !options.Language.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                args.Append($"-l {options.Language.ToLowerInvariant()} ");
            }
            else
            {
                args.Append("-l auto ");
            }

            if (options.Translate)
            {
                args.Append("--translate ");
            }

            // 3. Выходной путь и форматы
            if (!string.IsNullOrWhiteSpace(effectiveOutputBasePath))
            {
                args.Append($"-of \"{effectiveOutputBasePath}\" ");
            }

            if (options.OutputSrt) args.Append("-osrt ");
            if (options.OutputVtt) args.Append("-ovtt ");
            if (options.OutputTxt) args.Append("-otxt ");
            if (options.OutputLrc) args.Append("-olrc ");
            if (options.OutputJson) args.Append("-oj ");

            // 4. Параметры субтитров
            if (options.MaxSegmentLength > 0)
            {
                args.Append($"-ml {options.MaxSegmentLength} ");
            }

            if (options.SplitOnWord)
            {
                args.Append("--split-on-word ");
            }

            if (options.MaxContext >= 0)
            {
                args.Append($"-mc {options.MaxContext} ");
            }

            // 5. Потоки и аппаратные флаги
            int threads = Math.Clamp(options.Threads > 0 ? options.Threads : Environment.ProcessorCount, 1, 32);
            args.Append($"-t {threads} ");

            if (effectiveBackend != WhisperBackend.Cpu && options.FlashAttention)
            {
                args.Append("--flash-attn ");
            }

            if (effectiveBackend == WhisperBackend.Cpu)
            {
                args.Append("--no-gpu ");
            }

            // 6. Параметры декодирования
            if (options.BeamSize > 0)
            {
                args.Append($"-bs {options.BeamSize} ");
            }

            if (options.BestOf > 1)
            {
                args.Append($"-bo {options.BestOf} ");
            }

            if (options.NoSpeechThreshold > 0)
            {
                args.Append(CultureInfo.InvariantCulture, $"-nth {options.NoSpeechThreshold:F2} ");
            }

            if (options.SuppressNonSpeechTokens)
            {
                args.Append("-sns ");
            }

            // 7. Голосовая активность (VAD) для отсечения тишины и музыки
            if (options.EnableVad && !string.IsNullOrWhiteSpace(safeVadPath) && File.Exists(safeVadPath))
            {
                args.Append("--vad ");
                args.Append($"-vm \"{safeVadPath}\" ");
                if (options.VadThreshold > 0)
                {
                    args.Append(CultureInfo.InvariantCulture, $"-vt {options.VadThreshold:F2} ");
                }
                if (options.VadMinSilenceDurationMs > 0)
                {
                    args.Append($"-vsd {options.VadMinSilenceDurationMs} ");
                }
            }

            // 8. Печать прогресса в stderr для динамического парсинга
            args.Append("-pp");

            string workingDir = Path.GetDirectoryName(binaryPath) ?? AppContext.BaseDirectory;
            Log.Write(
                ProcessEventIds.Started,
                LogLevel.Info,
                LogStatus.Running,
                $"Запущено распознавание речи Whisper на бэкенде {effectiveBackend}",
                source: SourceName,
                context: executionContext.ToLogContext(),
                properties: LogProps
                    .Create("Tool", "whisper")
                    .With("FileName", LogProps.FileName(options.ModelPath))
                    .With("InputName", LogProps.FileName(options.InputWavPath)));

            var result = await RunProcessAsync(
                "whisper-cli",
                args.ToString().Trim(),
                onOutputLine: line =>
                {
                    if (WhisperOutputParser.TryParseSegment(line, out var start, out var end, out var text))
                    {
                        string formatted = $"[{start:hh\\:mm\\:ss} -> {end:hh\\:mm\\:ss}] {text}";
                        onSegment?.Invoke(formatted);
                    }
                },
                onErrorLine: line =>
                {
                    if (WhisperOutputParser.TryParseProgress(line, out int percent))
                    {
                        onProgress?.Invoke(percent);
                    }
                },
                cancellationToken: cancellationToken,
                workingDir: workingDir,
                context: executionContext,
                explicitBinaryPath: binaryPath);

            if (!result.IsSuccess && !result.IsCancelled)
            {
                Dictionary<string, object?> transcribeProperties = result.ToLogProperties();
                Log.Write(
                    "whisper.transcription_failed",
                    LogLevel.Error,
                    LogStatus.Failed,
                    $"Ошибка транскрибации Whisper: {result.ErrorCode ?? ProcessResult.ErrorNonZeroExit}",
                    result.Exception,
                    nameof(WhisperRunner),
                    result.Context.ToLogContext().WithProcess(result.ProcessId),
                    transcribeProperties.With("ErrorCode", result.ErrorCode ?? ProcessResult.ErrorNonZeroExit));
            }

            // Перемещение сгенерированных файлов из временной директории в целевой OutputBasePath
            if (result.IsSuccess && tempOutputDir != null && Directory.Exists(tempOutputDir) && !string.IsNullOrWhiteSpace(options.OutputBasePath))
            {
                string targetDir = Path.GetDirectoryName(options.OutputBasePath) ?? "";
                if (!string.IsNullOrWhiteSpace(targetDir) && !Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                int moved = 0;
                string[] extensions = { ".srt", ".vtt", ".txt", ".lrc", ".json", ".csv" };
                foreach (string ext in extensions)
                {
                    string sourceFile = Path.Combine(tempOutputDir, "subtitles" + ext);
                    if (File.Exists(sourceFile))
                    {
                        string destFile = options.OutputBasePath + ext;
                        File.Move(sourceFile, destFile, overwrite: true);
                        moved++;
                    }
                }

                Log.Write(
                    ProcessEventIds.Completed,
                    LogLevel.Info,
                    LogStatus.Succeeded,
                    $"Результаты распознавания перемещены в целевую папку, файлов: {moved}",
                    source: SourceName,
                    context: executionContext.ToLogContext(),
                    properties: LogProps
                        .Create("Tool", "whisper")
                        .With("FileName", LogProps.FileName(targetDir))
                        .With("Count", moved)
                        .With("ArtifactVerified", moved > 0));
            }

            return result;
        }
        finally
        {
            // Очистка временных файлов
            if (tempModelCopy != null && File.Exists(tempModelCopy) && !IsCachedAsciiCopy(tempModelCopy))
            {
                TryDeleteFile(tempModelCopy, "model_copy", executionContext);
            }

            if (tempWavCopy != null && File.Exists(tempWavCopy))
            {
                TryDeleteFile(tempWavCopy, "wav_copy", executionContext);
            }

            if (tempVadCopy != null && File.Exists(tempVadCopy))
            {
                TryDeleteFile(tempVadCopy, "vad_copy", executionContext);
            }

            if (tempOutputDir != null && Directory.Exists(tempOutputDir))
            {
                try
                {
                    Directory.Delete(tempOutputDir, recursive: true);
                }
                catch (Exception ex)
                {
                    Log.Write(
                        "whisper.cleanup_failed",
                        LogLevel.Warning,
                        LogStatus.PartiallySucceeded,
                        "Не удалось удалить временную папку вывода распознавания",
                        ex,
                        nameof(WhisperRunner),
                        executionContext.ToLogContext(),
                        new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["Stage"] = "cleanup",
                            ["CleanupState"] = "failed"
                        });
                }
            }
        }
    }

    private void TryDeleteFile(string path, string stage, ProcessExecutionContext executionContext)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Write(
                "whisper.cleanup_failed",
                LogLevel.Warning,
                LogStatus.PartiallySucceeded,
                "Не удалось удалить временный файл распознавания",
                ex,
                nameof(WhisperRunner),
                executionContext.ToLogContext(),
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Stage"] = stage,
                    ["CleanupState"] = "failed"
                });
        }
    }

    /// <summary>
    /// Обеспечивает безопасный ASCII-путь для передачи аргументов в утилиту whisper-cli.
    /// Преобразует путь в короткий формат 8.3 (DOS). Если на томе отключена генерация 8.3
    /// и в пути сохраняются не-ASCII символы, создаёт временную копию файла в ASCII-папке %TEMP%.
    /// Для файлов моделей копия кэшируется и переиспользуется между запусками.
    /// </summary>
    /// <param name="originalPath">Оригинальный полный путь к файлу.</param>
    /// <param name="tempFilePrefix">Префикс имени временного файла в случае создания копии.</param>
    /// <param name="cacheCopy">Кэшировать временную копию для повторного использования.</param>
    /// <returns>Кортеж: (безопасный путь, признак того, является ли путь созданной временной копией).</returns>
    private (string SafePath, bool IsTempCopy) EnsureAsciiSafePath(
        string originalPath,
        string tempFilePrefix,
        bool cacheCopy = false)
    {
        if (string.IsNullOrWhiteSpace(originalPath) || !File.Exists(originalPath))
        {
            return (originalPath, false);
        }

        string shortPath = PathManager.GetShortPath(originalPath);
        if (IsStrictAscii(shortPath))
        {
            return (shortPath, false);
        }

        if (cacheCopy
            && AsciiModelCache.TryGetValue(originalPath, out string? cachedCopy)
            && File.Exists(cachedCopy))
        {
            return (cachedCopy, true);
        }

        Log.Write(
            "whisper.ascii_copy_created",
            LogLevel.Warning,
            LogStatus.RetryScheduled,
            "Короткий путь 8.3 недоступен для внешнего файла, создаётся временная ASCII-копия",
            null,
            nameof(WhisperRunner),
            context: ProcessExecutionContext.NewOperation("whisper-cli").ToLogContext(),
            properties: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Stage"] = "ascii_copy",
                ["FileName"] = Path.GetFileName(originalPath)
            });
        string extension = Path.GetExtension(originalPath);
        string tempDir = Path.Combine(Path.GetTempPath(), "ktools_temp_models");
        Directory.CreateDirectory(tempDir);
        string tempFilePath = Path.Combine(tempDir, $"{tempFilePrefix}{Guid.NewGuid():N}{extension}");

        File.Copy(originalPath, tempFilePath, overwrite: true);
        string returnedTempPath = IsStrictAscii(PathManager.GetShortPath(tempFilePath))
            ? PathManager.GetShortPath(tempFilePath)
            : tempFilePath;
        if (cacheCopy)
        {
            // В кэш кладем именно тот путь, который возвращаем наружу: иначе
            // проверка кэшированной копии в finally не найдет 8.3-псевдоним
            // и удалит копию, которая переиспользуется следующими запусками.
            AsciiModelCache[originalPath] = returnedTempPath;
        }

        Log.Write(
            "subtitle.ascii_copy_created",
            LogLevel.Debug,
            LogStatus.Succeeded,
            "Создана временная копия субтитров в ASCII-кодировке",
            source: SourceName,
            properties: LogProps
                .Create("InputName", LogProps.FileName(originalPath))
                .With("Extension", Path.GetExtension(originalPath)));

        return (returnedTempPath, true);
    }

    /// <summary>
    /// Проверяет, является ли путь кэшированной моделью, которую нельзя удалять при завершении.
    /// </summary>
    private static bool IsCachedAsciiCopy(string path)
    {
        foreach (string cached in AsciiModelCache.Values)
        {
            if (string.Equals(cached, path, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Проверяет, состоит ли указанная строка исключительно из стандартных ASCII-символов (код от 0 до 127).
    /// </summary>
    private static bool IsStrictAscii(string input)
    {
        foreach (char c in input)
        {
            if (c > 127) return false;
        }
        return true;
    }
}
