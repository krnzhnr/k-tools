// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace KTools_App.Services.Contracts;

/// <summary>
/// Информация о битрейте кадра или временного бакета.
/// </summary>
public sealed class FrameBitrateInfo
{
    public double PtsTime { get; set; }
    public long SizeBytes { get; set; }
    public bool IsKeyframe { get; set; }
}

/// <summary>
/// Агрегированные статистические данные побитового битрейта медиафайла.
/// </summary>
public sealed class BitrateAnalysisResult
{
    public string FilePath { get; set; } = string.Empty;
    public string CodecName { get; set; } = string.Empty;
    public double DurationSeconds { get; set; }
    public double Fps { get; set; }
    public long TotalFrames { get; set; }
    
    /// <summary>Посекундный битрейт в Мбит/с (Mbps).</summary>
    public double[] PerSecondMbps { get; set; } = Array.Empty<double>();

    /// <summary>Покадровый битрейт каждого кадра/пакета.</summary>
    public FrameBitrateInfo[] FramePackets { get; set; } = Array.Empty<FrameBitrateInfo>();
    
    /// <summary>Временные метки ключевых I-кадров в секундах.</summary>
    public double[] KeyframeTimes { get; set; } = Array.Empty<double>();

    public double MeanMbps { get; set; }
    public double MinMbps { get; set; }
    public double MaxMbps { get; set; }
    public double StdDevMbps { get; set; }
}

/// <summary>
/// Интерфейс сервиса фонового анализа побитового битрейта медиафайлов.
/// </summary>
public interface IBitrateAnalyzerService
{
    /// <summary>
    /// Проводит детальный покадровый анализ битрейта видео- или аудиофайла через ffprobe.
    /// </summary>
    Task<BitrateAnalysisResult?> AnalyzeBitrateAsync(
        string filePath,
        Action<double, string>? progressCallback = null,
        System.Threading.CancellationToken cancellationToken = default);
}
