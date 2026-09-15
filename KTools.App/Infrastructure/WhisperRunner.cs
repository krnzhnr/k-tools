// -*- coding: utf-8 -*-
using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KTools_App.Core;
using KTools_App.Services.Contracts;

namespace KTools_App.Infrastructure;

/// <summary>
/// Реализация сервиса запуска и взаимодействия с дочерним процессом whisper-cli.
/// Наследуется от <see cref="AbstractProcessRunner"/> для централизованного контроля запущенных процессов.
/// </summary>
public sealed class WhisperRunner : AbstractProcessRunner, IWhisperRunner
{
    private readonly IDependencyManager _dependencyManager;

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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Определяем реальный бэкенд
        WhisperBackend effectiveBackend = options.Backend == WhisperBackend.Auto
            ? WhisperBackendDetector.DetectBestBackend(_dependencyManager)
            : options.Backend;

        string binaryPath = GetBinaryPathForBackend(effectiveBackend);
        if (!File.Exists(binaryPath))
        {
            // Попытка фоллбэка на CPU, если запрошенный бэкенд отсутствует
            if (effectiveBackend != WhisperBackend.Cpu)
            {
                Log.Warn($"Исполняемый файл для бэкенда '{effectiveBackend}' не найден по пути '{binaryPath}'. Попытка переключения на CPU-рантайм...", nameof(WhisperRunner));
                effectiveBackend = WhisperBackend.Cpu;
                binaryPath = GetBinaryPathForBackend(WhisperBackend.Cpu);
            }

            if (!File.Exists(binaryPath))
            {
                string errMsg = $"Исполняемый файл whisper-cli.exe не найден на диске. Убедитесь, что установлена зависимость '{WhisperBackendDetector.GetDependencyKey(effectiveBackend)}'.";
                Log.Error(errMsg, nameof(WhisperRunner));
                return new ProcessResult(false, -1, errMsg);
            }
        }

        if (!File.Exists(options.ModelPath))
        {
            string errMsg = $"Файл модели Whisper не найден по пути: '{options.ModelPath}'";
            Log.Error(errMsg, nameof(WhisperRunner));
            return new ProcessResult(false, -1, errMsg);
        }

        if (!File.Exists(options.InputWavPath))
        {
            string errMsg = $"Входной аудиофайл для распознавания не существует: '{options.InputWavPath}'";
            Log.Error(errMsg, nameof(WhisperRunner));
            return new ProcessResult(false, -1, errMsg);
        }

        // Формирование аргументов командной строки whisper-cli
        var args = new StringBuilder();

        // 1. Модель и входной файл
        args.Append($"-m \"{options.ModelPath}\" ");
        args.Append($"-f \"{options.InputWavPath}\" ");

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
        if (!string.IsNullOrWhiteSpace(options.OutputBasePath))
        {
            args.Append($"-of \"{options.OutputBasePath}\" ");
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
        if (options.EnableVad && !string.IsNullOrWhiteSpace(options.VadModelPath) && File.Exists(options.VadModelPath))
        {
            args.Append("--vad ");
            args.Append($"-vm \"{options.VadModelPath}\" ");
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
        Log.Info($"Запуск Whisper ({effectiveBackend}) с моделью '{Path.GetFileName(options.ModelPath)}'", nameof(WhisperRunner));

        var recentErrorLines = new System.Collections.Concurrent.ConcurrentQueue<string>();

        var result = await RunProcessAsync(
            binaryPath,
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
                else if (!string.IsNullOrWhiteSpace(line))
                {
                    recentErrorLines.Enqueue(line);
                    if (recentErrorLines.Count > 15)
                    {
                        recentErrorLines.TryDequeue(out _);
                    }
                }
            },
            cancellationToken: cancellationToken,
            workingDir: workingDir);

        if (!result.IsSuccess && !recentErrorLines.IsEmpty)
        {
            string details = string.Join("\n", recentErrorLines);
            Log.Error($"Подробности ошибки whisper-cli:\n{details}", nameof(WhisperRunner));
        }

        return result;
    }
}
