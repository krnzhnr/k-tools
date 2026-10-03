// -*- coding: utf-8 -*-
using System;
using System.Threading;
using System.Threading.Tasks;

using KTools_App.Infrastructure;

namespace KTools_App.Services.Contracts;

/// <summary>
/// Интерфейс обертки для запуска утилиты eac3to.
/// </summary>
public interface IEac3toRunner
{
    /// <summary>
    /// Запустить утилиту eac3to асинхронно с переданными аргументами и отслеживанием прогресса.
    /// Возвращает типизированный результат с проверкой кода возврата и ожидаемого артефакта.
    /// </summary>
    Task<ProcessResult> RunAsync(
        List<string> args,
        string? workingDir = null,
        Action<double>? onProgress = null,
        CancellationToken cancellationToken = default,
        ProcessExecutionContext? context = null,
        string? expectedArtifact = null);
}
