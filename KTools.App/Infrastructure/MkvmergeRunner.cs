// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Services.Contracts;

namespace KTools_App.Infrastructure;

/// <summary>
/// Структура, описывающая входной медиа-источник для сборки контейнера в mkvmerge.
/// </summary>
public record MkvInputSource(string Path, List<string>? Args = null);

/// <summary>
/// Синглтон-обертка для запуска утилиты mkvmerge (из пакета MKVToolNix).
/// Обеспечивает объединение видео, аудио и субтитров в единый файл матроски (.mkv).
/// </summary>
public sealed class MkvmergeRunner : AbstractProcessRunner, IMkvmergeRunner
{
    private const string SourceName = nameof(MkvmergeRunner);

    public const int MaxWarningExitCode = 1;

    /// <summary>
    /// Инициализирует новый экземпляр MkvmergeRunner с внедрением зависимостей.
    /// </summary>
    /// <param name="logService">Сервис логирования.</param>
    public MkvmergeRunner(ILogService logService, IPathManager pathManager)
        : base(logService, pathManager)
    {
    }

    /// <summary>
    /// Запустить процесс сборки контейнера MKV через mkvmerge.
    /// </summary>
    /// <param name="outputPath">Абсолютный путь к выходному собираемому MKV-файлу.</param>
    /// <param name="inputs">Список входных медиа-источников с индивидуальными флагами.</param>
    /// <param name="title">Глобальный заголовок (метаданные) собираемого MKV.</param>
    /// <param name="extraArgs">Глобальные дополнительные аргументы для mkvmerge.</param>
    /// <param name="onProgress">Колбек для передачи процентов прогресса (от 0 до 100).</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    public async Task<ProcessResult> RunAsync(
        string outputPath,
        List<MkvInputSource> inputs,
        string? title = null,
        List<string>? extraArgs = null,
        Action<double>? onProgress = null,
        CancellationToken cancellationToken = default,
        ProcessExecutionContext? context = null)
    {
        ProcessExecutionContext executionContext = (context ?? ProcessExecutionContext.NewOperation("mkvmerge"))
            .WithExpectedArtifact(outputPath);

        if (inputs == null || inputs.Count == 0)
        {
            Log.Write(
                "mkvmerge.inputs_missing",
                LogLevel.Error,
                LogStatus.Failed,
                "Не переданы входные файлы для сборки в mkvmerge",
                null,
                "MkvmergeRunner",
                executionContext.ToLogContext(),
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["ErrorCode"] = "inputs-missing"
                });
            return ProcessResult.NotStarted(
                executionContext,
                "inputs-missing",
                "Не переданы входные файлы для сборки в mkvmerge");
        }

        // Базовые аргументы: выходной файл
        var argsList = new List<string>
        {
            "--output", $"\"{outputPath}\""
        };

        // Глобальный заголовок контейнера
        if (!string.IsNullOrWhiteSpace(title))
        {
            argsList.Add("--title");
            argsList.Add($"\"{title}\"");
        }

        // Глобальные аргументы
        if (extraArgs != null)
        {
            argsList.AddRange(extraArgs);
        }

        // Индивидуальные аргументы каждого источника и сами файлы
        foreach (var input in inputs)
        {
            if (input.Args != null && input.Args.Count > 0)
            {
                argsList.AddRange(input.Args);
            }

            argsList.Add($"\"{input.Path}\"");
        }

        string arguments = string.Join(" ", argsList);

        Log.Write(
            ProcessEventIds.Started,
            LogLevel.Info,
            LogStatus.Running,
            $"Запущена сборка контейнера MKV, входов: {inputs.Count}",
            source: SourceName,
            context: executionContext.ToLogContext(),
            properties: LogProps
                .Create("Tool", "mkvmerge")
                .With("OutputName", LogProps.FileName(outputPath))
                .With("Count", inputs.Count));

        var result = await RunProcessAsync(
            "mkvmerge",
            arguments,
            onOutputLine: line =>
            {
                if (onProgress is null)
                {
                    return;
                }

                double? percent = MkvmergeOutputParser.ParseLine(line);
                if (percent.HasValue)
                {
                    onProgress(percent.Value);
                }
            },
            onErrorLine: null,
            cancellationToken,
            context: executionContext,
            expectedArtifact: outputPath,
            maxSuccessExitCode: MaxWarningExitCode);

        if (result.IsSuccess)
        {
            return result;
        }

        if (result.IsCancelled)
        {
            return result;
        }

        if (File.Exists(outputPath))
        {
            try
            {
                File.Delete(outputPath);
                Log.Write(
                ProcessEventIds.ArtifactMissing,
                LogLevel.Debug,
                LogStatus.Succeeded,
                "Повреждённый выходной файл удалён после сбоя mkvmerge",
                source: SourceName,
                context: executionContext.ToLogContext(),
                properties: LogProps
                    .Create("Tool", "mkvmerge")
                    .With("OutputName", LogProps.FileName(outputPath))
                    .With("CleanupState", "Removed"));
            }
            catch (Exception deleteEx)
            {
                Log.Write(
                    ProcessEventIds.ArtifactMissing,
                    LogLevel.Warning,
                    LogStatus.PartiallySucceeded,
                    $"Не удалось удалить повреждённый выходной файл '{Path.GetFileName(outputPath)}' после сбоя mkvmerge",
                    deleteEx,
                    "MkvmergeRunner",
                    executionContext.ToLogContext().WithProcess(result.ProcessId),
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["Stage"] = "artifact_cleanup",
                        ["ErrorCode"] = ProcessResult.ErrorArtifactMissing
                    });
            }
        }

        return result;
    }

    /// <summary>
    /// Получить техническую информацию о MKV-файле в формате JSON через mkvmerge.
    /// </summary>
    /// <param name="filePath">Абсолютный путь к исследуемому MKV-файлу.</param>
    /// <returns>Документ JsonDocument со свойствами дорожек и вложений, или null.</returns>
    public async Task<JsonDocument?> IdentifyAsync(string filePath, ProcessExecutionContext? context = null)
    {
        string arguments = $"--identify --identification-format json \"{filePath}\"";
        string fileName = Path.GetFileName(filePath);
        ProcessExecutionContext executionContext = context ?? ProcessExecutionContext.NewOperation("mkvmerge");
        var outputLines = new List<string>();

        // mkvmerge --identify возвращает 0 при успехе или 1 при наличии предупреждений
        var result = await RunProcessAsync(
            "mkvmerge",
            arguments,
            onOutputLine: line => outputLines.Add(line),
            onErrorLine: null,
            CancellationToken.None,
            context: executionContext,
            maxSuccessExitCode: MaxWarningExitCode,
            verifyParseResult: () => outputLines.Count > 0);

        if (!result.IsSuccess)
        {
            Dictionary<string, object?> identifyProperties = result.ToLogProperties();
            Log.Write(
                ProcessEventIds.PostconditionFailed,
                LogLevel.Error,
                LogStatus.Failed,
                $"Ошибка вызова mkvmerge --identify для файла '{fileName}'",
                null,
                "MkvmergeRunner",
                result.Context.ToLogContext().WithProcess(result.ProcessId),
                identifyProperties.With("ErrorCode", result.ErrorCode ?? "MKVMERGE_IDENTIFY_FAILED"));
            return null;
        }

        string fullOutput = string.Join("", outputLines);
        if (string.IsNullOrWhiteSpace(fullOutput))
        {
            Log.Write(
                ProcessEventIds.PostconditionFailed,
                LogLevel.Error,
                LogStatus.Failed,
                $"mkvmerge --identify вернул пустой вывод для '{fileName}'",
                null,
                "MkvmergeRunner",
                result.Context.ToLogContext().WithProcess(result.ProcessId),
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["ErrorCode"] = ProcessResult.ErrorOutputEmpty,
                    ["OutputName"] = fileName
                });
            return null;
        }

        try
        {
            return JsonDocument.Parse(fullOutput);
        }
        catch (JsonException ex)
        {
            Log.Write(
                ProcessEventIds.PostconditionFailed,
                LogLevel.Error,
                LogStatus.Failed,
                $"Ошибка парсинга JSON от mkvmerge для '{fileName}'",
                ex,
                "MkvmergeRunner",
                result.Context.ToLogContext().WithProcess(result.ProcessId),
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["ErrorCode"] = ProcessResult.ErrorParseInvalid,
                    ["OutputName"] = fileName
                });
            return null;
        }
    }
}
