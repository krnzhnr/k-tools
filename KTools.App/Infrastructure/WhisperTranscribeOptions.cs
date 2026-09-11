// -*- coding: utf-8 -*-
using System;
using KTools_App.Core;

namespace KTools_App.Infrastructure;

/// <summary>
/// Набор параметров конфигурации для запуска транскрибации речи через whisper.cpp CLI.
/// </summary>
public sealed class WhisperTranscribeOptions
{
    /// <summary>
    /// Абсолютный путь к промежуточному аудиофайлу формата WAV (16 кГц, 16 бит, моно).
    /// </summary>
    public string InputWavPath { get; init; } = string.Empty;

    /// <summary>
    /// Абсолютный путь к файлу модели GGML (например, bin/whisper-models/ggml-base.bin).
    /// </summary>
    public string ModelPath { get; init; } = string.Empty;

    /// <summary>
    /// Базовый путь к выходным файлам (без расширения). whisper-cli автоматически добавит .srt, .txt и т.д.
    /// </summary>
    public string OutputBasePath { get; init; } = string.Empty;

    /// <summary>
    /// Двухбуквенный языковой код исходной речи (например, "ru", "en") или "auto" для автодетекции.
    /// </summary>
    public string Language { get; init; } = "auto";

    /// <summary>
    /// Признак необходимости прямого перевода речи на английский язык.
    /// </summary>
    public bool Translate { get; init; }

    /// <summary>
    /// Вычислительный бэкенд для выполнения расчетов (CPU, CUDA).
    /// </summary>
    public WhisperBackend Backend { get; init; } = WhisperBackend.Cpu;

    /// <summary>
    /// Экспортировать субтитры SubRip (.srt).
    /// </summary>
    public bool OutputSrt { get; init; } = true;

    /// <summary>
    /// Экспортировать субтитры WebVTT (.vtt).
    /// </summary>
    public bool OutputVtt { get; init; }

    /// <summary>
    /// Экспортировать неразмеченный текст (.txt).
    /// </summary>
    public bool OutputTxt { get; init; } = true;

    /// <summary>
    /// Экспортировать синхронизированные караоке/лирику (.lrc).
    /// </summary>
    public bool OutputLrc { get; init; }

    /// <summary>
    /// Экспортировать структуру в формате JSON (.json).
    /// </summary>
    public bool OutputJson { get; init; }

    /// <summary>
    /// Максимальная длина сегмента субтитра в символах (0 = отключить ограничение).
    /// </summary>
    public int MaxSegmentLength { get; init; } = 35;

    /// <summary>
    /// Разделять сегменты строго по границам слов при превышении лимита длины.
    /// </summary>
    public bool SplitOnWord { get; init; } = true;

    /// <summary>
    /// Максимальное количество токенов контекста из предыдущего сегмента (0 = отключить для предотвращения зацикливания и галлюцинаций).
    /// </summary>
    public int MaxContext { get; init; }

    /// <summary>
    /// Количество процессорных потоков для вычислений.
    /// </summary>
    public int Threads { get; init; } = 4;

    /// <summary>
    /// Использовать Flash Attention для снижения потребления видеопамяти и ускорения инференса на GPU.
    /// </summary>
    public bool FlashAttention { get; init; } = true;

    /// <summary>
    /// Размер луча при Beam Search декодировании (-1 = жадный режим Greedy).
    /// </summary>
    public int BeamSize { get; init; } = -1;

    /// <summary>
    /// Количество лучших кандидатов при сэмплировании.
    /// </summary>
    public int BestOf { get; init; } = 5;

    /// <summary>
    /// Порог вероятности для отсечения участков без речи и фонового шума (0..1).
    /// </summary>
    public double NoSpeechThreshold { get; init; } = 0.65;

    /// <summary>
    /// Принудительно подавлять неречевые токены и фоновые галлюцинации (--suppress-nst / -sns).
    /// </summary>
    public bool SuppressNonSpeechTokens { get; init; } = true;

    /// <summary>
    /// Использовать Voice Activity Detection (VAD) для фильтрации тишины и фоновой музыки (--vad).
    /// Требует наличия файла модели VAD (например, silero-vad.onnx / ggml-vad.bin). Если файл не указан или отсутствует, флаг не передается.
    /// </summary>
    public bool EnableVad { get; init; }

    /// <summary>
    /// Путь к файлу модели VAD для параметра -vm.
    /// </summary>
    public string VadModelPath { get; init; } = string.Empty;

    /// <summary>
    /// Порог чувствительности детектора голоса VAD (0.0..1.0, по умолчанию: 0.50).
    /// </summary>
    public double VadThreshold { get; init; } = 0.50;

    /// <summary>
    /// Минимальная длительность тишины в миллисекундах для разбивки речевых сегментов (--vad-min-silence-duration-ms).
    /// </summary>
    public int VadMinSilenceDurationMs { get; init; } = 250;
}
