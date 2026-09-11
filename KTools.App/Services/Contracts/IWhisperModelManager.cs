// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KTools_App.Core;

namespace KTools_App.Services.Contracts;

/// <summary>
/// Интерфейс службы управления и загрузки моделей Whisper (GGML/GGUF).
/// Обеспечивает получение реестра моделей, проверку наличия на диске, скачивание и удаление.
/// </summary>
public interface IWhisperModelManager
{
    /// <summary>
    /// Возвращает список всех поддерживаемых моделей Whisper.
    /// </summary>
    IReadOnlyList<WhisperModelInfo> GetAvailableModels();

    /// <summary>
    /// Получить информацию о конкретной модели по ее строковому ключу.
    /// </summary>
    /// <param name="modelKey">Строковый ключ модели (например, "base", "large-v3-turbo").</param>
    WhisperModelInfo? GetModelInfo(string modelKey);

    /// <summary>
    /// Проверяет, загружен ли файл указанной модели на диск.
    /// </summary>
    /// <param name="modelKey">Строковый ключ модели.</param>
    bool IsModelDownloaded(string modelKey);

    /// <summary>
    /// Возвращает абсолютный путь к файлу модели на диске.
    /// </summary>
    /// <param name="modelKey">Строковый ключ модели.</param>
    string GetModelFilePath(string modelKey);

    /// <summary>
    /// Асинхронно скачивает выбранную модель из официального источника Hugging Face.
    /// </summary>
    /// <param name="modelKey">Строковый ключ модели.</param>
    /// <param name="progress">Прогресс скачивания в процентах (0..100).</param>
    /// <param name="speed">Строковое представление текущей скорости скачивания.</param>
    /// <param name="cancellationToken">Токен отмены задачи.</param>
    Task<bool> DownloadModelAsync(
        string modelKey,
        IProgress<int>? progress = null,
        IProgress<string>? speed = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Физически удаляет файл модели с диска.
    /// </summary>
    /// <param name="modelKey">Строковый ключ модели.</param>
    bool DeleteModel(string modelKey);

    /// <summary>
    /// Проверяет, выполняется ли в данный момент процесс скачивания указанной модели.
    /// </summary>
    /// <param name="modelKey">Строковый ключ модели.</param>
    bool IsModelDownloading(string modelKey);

    /// <summary>
    /// Возвращает текущий процент выполнения скачивания модели (0..100).
    /// </summary>
    /// <param name="modelKey">Строковый ключ модели.</param>
    int GetModelDownloadProgress(string modelKey);

    /// <summary>
    /// Принудительно отменяет текущее скачивание указанной модели, если оно выполняется.
    /// </summary>
    /// <param name="modelKey">Строковый ключ модели.</param>
    void CancelDownload(string modelKey);

    /// <summary>
    /// Событие изменения прогресса скачивания модели (modelKey, percent).
    /// </summary>
    event Action<string, int>? DownloadProgressChanged;

    /// <summary>
    /// Событие завершения скачивания модели (modelKey, isSuccess, errorMessage).
    /// </summary>
    event Action<string, bool, string?>? DownloadCompleted;

    /// <summary>
    /// Возвращает путь к директории хранения моделей Whisper.
    /// </summary>
    string GetModelsDirectory();

    /// <summary>
    /// Проверяет, загружен ли файл модели детекции голоса Silero VAD на диск.
    /// </summary>
    bool IsVadModelDownloaded();

    /// <summary>
    /// Возвращает абсолютный путь к файлу модели Silero VAD на диске.
    /// </summary>
    string GetVadModelFilePath();

    /// <summary>
    /// Гарантирует наличие файла модели Silero VAD на диске, скачивая его при необходимости.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    Task<bool> EnsureVadModelDownloadedAsync(CancellationToken cancellationToken = default);
}
