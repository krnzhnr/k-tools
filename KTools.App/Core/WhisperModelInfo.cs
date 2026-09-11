// -*- coding: utf-8 -*-
using System;

namespace KTools_App.Core;

/// <summary>
/// Модель данных, описывающая отдельную нейросетевую модель Whisper в формате GGML/GGUF.
/// Предоставляет метаданные для отображения в интерфейсе, проверки наличия на диске и загрузки.
/// </summary>
public sealed class WhisperModelInfo
{
    /// <summary>
    /// Уникальный строковый идентификатор модели (например, "tiny", "base", "large-v3-turbo").
    /// </summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>
    /// Отображаемое имя модели в интерфейсе пользователя с указанием размера и рекомендаций.
    /// </summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>
    /// Имя файла модели на диске (например, "ggml-base.bin").
    /// </summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>
    /// Приблизительный размер файла модели на диске в мегабайтах.
    /// </summary>
    public double SizeMb { get; init; }

    /// <summary>
    /// Приблизительный требуемый объем оперативной/видеопамяти в мегабайтах для инференса.
    /// </summary>
    public double RequiredMemoryMb { get; init; }

    /// <summary>
    /// Прямой URL-адрес для загрузки модели из официального репозитория Hugging Face.
    /// </summary>
    public string DownloadUrl { get; init; } = string.Empty;

    /// <summary>
    /// Признак поддержки мультиязычного распознавания (включая русский язык).
    /// </summary>
    public bool IsMultilingual { get; init; } = true;

    /// <summary>
    /// Краткое описание качества и скорости работы модели на русском языке.
    /// </summary>
    public string Description { get; init; } = string.Empty;
}
