// -*- coding: utf-8 -*-
using System;
using System.Threading;
using System.Threading.Tasks;

namespace KTools_App.Services.Contracts;

/// <summary>
/// Структура для хранения мин/макс значений аудиосигнала.
/// Все комментарии и документация выполнены на русском языке в соответствии с регламентом.
/// </summary>
public readonly record struct WaveformPeak(float Min, float Max);

/// <summary>
/// Данные пирамиды уровней детализации (LOD) для мгновенной отрисовки осциллограмм любого масштаба уровня Adobe Audition.
/// </summary>
public sealed class WaveformLevelData
{
    /// <summary>
    /// Частота дискретизации извлеченного сигнала (обычно 44100 Гц).
    /// </summary>
    public required int SampleRate { get; init; }

    /// <summary>
    /// Общая длительность аудиозаписи в секундах.
    /// </summary>
    public required double TotalDurationSeconds { get; init; }

    /// <summary>
    /// Ультра-высокая детализация (1000 отсчетов в секунду / 1 мс на пик).
    /// </summary>
    public required WaveformPeak[] Peaks1000Hz { get; init; }

    /// <summary>
    /// Высокая детализация (200 отсчетов в секунду / 5 мс на пик).
    /// </summary>
    public required WaveformPeak[] Peaks200Hz { get; init; }

    /// <summary>
    /// Средне-высокая детализация (50 отсчетов в секунду).
    /// </summary>
    public required WaveformPeak[] Peaks50Hz { get; init; }

    /// <summary>
    /// Средняя детализация (10 отсчетов в секунду).
    /// </summary>
    public required WaveformPeak[] Peaks10Hz { get; init; }

    /// <summary>
    /// Низкая детализация (1 отсчет в секунду).
    /// </summary>
    public required WaveformPeak[] Peaks1Hz { get; init; }
}

/// <summary>
/// Интерфейс сервиса быстрой генерации нативных осциллограмм аудиопотоков.
/// </summary>
public interface IAudioWaveformService
{
    /// <summary>
    /// Фоновое извлечение аудио и генерация LOD-пиков с использованием SIMD-векторизации.
    /// </summary>
    /// <param name="mediaFilePath">Путь к медиафайлу.</param>
    /// <param name="audioTrackIndex">Индекс аудиодорожки в FFmpeg.</param>
    /// <param name="progressCallback">Коллбек прогресса (процент 0..100, статус).</param>
    /// <param name="cancellationToken">Токен отмены задачи.</param>
    /// <returns>Объект данных LOD осциллограммы.</returns>
    Task<WaveformLevelData> ExtractAndGeneratePeaksAsync(
        string mediaFilePath,
        int audioTrackIndex,
        Action<double, string>? progressCallback = null,
        CancellationToken cancellationToken = default);
}
