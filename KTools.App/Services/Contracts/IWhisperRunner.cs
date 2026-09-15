// -*- coding: utf-8 -*-
using System;
using System.Threading;
using System.Threading.Tasks;
using KTools_App.Infrastructure;

namespace KTools_App.Services.Contracts;

/// <summary>
/// Контракт сервиса запуска процесса транскрибации речи через утилиту whisper-cli.
/// </summary>
public interface IWhisperRunner
{
    /// <summary>
    /// Асинхронно запускает консольный процесс whisper-cli для транскрибации аудиофайла.
    /// </summary>
    /// <param name="options">Параметры запуска и конфигурация выходных форматов.</param>
    /// <param name="onProgress">Делегат уведомления о прогрессе транскрибации в процентах (0..100).</param>
    /// <param name="onSegment">Делегат получения распознанных сегментов субтитров в реальном времени.</param>
    /// <param name="cancellationToken">Токен отмены задачи.</param>
    /// <returns>Результат выполнения дочернего процесса.</returns>
    Task<ProcessResult> TranscribeAsync(
        WhisperTranscribeOptions options,
        Action<int>? onProgress = null,
        Action<string>? onSegment = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Проверяет, доступен ли исполняемый файл whisper-cli для указанного бэкенда.
    /// </summary>
    /// <param name="backend">Тип вычислительного бэкенда.</param>
    /// <returns>True, если исполняемый файл найден.</returns>
    bool IsBackendAvailable(KTools_App.Core.WhisperBackend backend);
}
