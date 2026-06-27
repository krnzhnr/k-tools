// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KTools_App.Services.Contracts;

/// <summary>
/// Интерфейс обертки для запуска утилиты eac3to.
/// </summary>
public interface IEac3toRunner
{
    /// <summary>
    /// Запустить утилиту eac3to асинхронно с переданными аргументами и отслеживанием прогресса.
    /// </summary>
    Task<bool> RunAsync(
        List<string> args,
        string? workingDir = null,
        Action<double>? onProgress = null,
        CancellationToken cancellationToken = default);
}
