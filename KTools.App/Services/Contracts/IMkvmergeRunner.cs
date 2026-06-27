// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KTools_App.Infrastructure;

namespace KTools_App.Services.Contracts;

/// <summary>
/// Интерфейс обертки для запуска утилиты mkvmerge.
/// </summary>
public interface IMkvmergeRunner
{
    /// <summary>
    /// Запустить процесс объединения медиапотоков в контейнер MKV через mkvmerge.
    /// </summary>
    Task<bool> RunAsync(
        string outputPath,
        List<MkvInputSource> inputs,
        string? title = null,
        List<string>? extraArgs = null,
        Action<double>? onProgress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Получить техническую информацию о MKV-файле в формате JSON через mkvmerge --identify.
    /// </summary>
    Task<JsonDocument?> IdentifyAsync(string filePath);
}
