// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Services.Contracts;

namespace KTools_App.Infrastructure;

/// <summary>
/// Синглтон-обертка для запуска утилиты eac3to.
/// Используется для изменения скорости и тона аудио (PAL ↔ NTSC), демуксинга и обработки DTS/AC3.
/// </summary>
public sealed class Eac3toRunner : AbstractProcessRunner, IEac3toRunner
{
    /// <summary>
    /// Инициализирует новый экземпляр Eac3toRunner с внедрением зависимостей.
    /// </summary>
    /// <param name="logService">Сервис логирования.</param>
    public Eac3toRunner(ILogService logService, IPathManager pathManager)
        : base(logService, pathManager)
    {
    }

    /// <summary>
    /// Запустить eac3to асинхронно с переданными аргументами командной строки.
    /// </summary>
    /// <param name="args">Список аргументов для запуска.</param>
    /// <param name="workingDir">Рабочая папка для запуска (если null, используется папка бинарника).</param>
    /// <param name="onProgress">Колбек для передачи прогресса (процентов от 0 до 100).</param>
    /// <param name="cancellationToken">Токен отмены задачи.</param>
    public async Task<ProcessResult> RunAsync(
        List<string> args,
        string? workingDir = null,
        Action<double>? onProgress = null,
        CancellationToken cancellationToken = default,
        ProcessExecutionContext? context = null,
        string? expectedArtifact = null)
    {
        var finalArgs = new List<string>(args);
        if (!finalArgs.Exists(a => a.StartsWith("-log=", StringComparison.OrdinalIgnoreCase)))
        {
            finalArgs.Add("-log=nul");
        }

        string arguments = string.Join(" ", finalArgs);
        string workingCwd = workingDir ?? Path.GetDirectoryName(PathManager.GetBinaryPath("eac3to")) ?? AppContext.BaseDirectory;
        ProcessExecutionContext executionContext = (context ?? ProcessExecutionContext.NewOperation("eac3to"))
            .WithExpectedArtifact(expectedArtifact);

        return await RunProcessAsync(
            "eac3to",
            arguments,
            onOutputLine: line =>
            {
                if (onProgress is null)
                {
                    return;
                }

                double? percent = Eac3toOutputParser.ParseLine(line);
                if (percent.HasValue)
                {
                    onProgress(percent.Value);
                }
            },
            onErrorLine: null,
            cancellationToken,
            workingDir: workingCwd,
            context: executionContext,
            expectedArtifact: expectedArtifact);
    }

}
