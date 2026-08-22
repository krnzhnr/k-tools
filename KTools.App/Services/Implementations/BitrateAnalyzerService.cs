// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KTools_App.Core;
using KTools_App.Infrastructure;
using KTools_App.Services.Contracts;

namespace KTools_App.Services.Implementations;

/// <summary>
/// Реализация сервиса анализа побитового битрейта медиафайлов.
/// Нативно задействует ffprobe с zero-allocation разбором пакетов для формирования
/// высокоточной временной сетки посекундного битрейта и ключевых кадров.
/// Все комментарии и логи строго на русском языке в соответствии с правилами проекта.
/// </summary>
public sealed class BitrateAnalyzerService : AbstractProcessRunner, IBitrateAnalyzerService
{
    private readonly IMediaProbeService _mediaProbeService;

    public BitrateAnalyzerService(
        ILogService logService,
        IPathManager pathManager,
        IMediaProbeService mediaProbeService)
        : base(logService, pathManager)
    {
        _mediaProbeService = mediaProbeService ?? throw new ArgumentNullException(nameof(mediaProbeService));
    }

    public async Task<BitrateAnalysisResult?> AnalyzeBitrateAsync(
        string filePath,
        Action<double, string>? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            Log.Error($"Файл не найден для анализа битрейта: '{filePath}'", "BitrateAnalyzerService");
            return null;
        }

        Log.Info($"Начало покадрового анализа битрейта для файла '{Path.GetFileName(filePath)}'", "BitrateAnalyzerService");
        progressCallback?.Invoke(0, "Анализ структуры метаданных...");

        // 1. Получаем структуру медиафайла
        var structure = await _mediaProbeService.ProbeAsync(filePath);
        var videoTracks = structure?.GetVideoTracks() ?? (IReadOnlyList<MediaTrack>)Array.Empty<MediaTrack>();
        var audioTracks = structure?.GetAudioTracks() ?? (IReadOnlyList<MediaTrack>)Array.Empty<MediaTrack>();

        var videoTrack = videoTracks.FirstOrDefault();
        var audioTrack = audioTracks.FirstOrDefault();

        var selectorsToTry = new List<string>();
        string codecName = "Unknown";
        double fps = 25.0;
        double duration = structure?.Duration ?? 0.0;

        if (videoTrack != null)
        {
            int relVideoIdx = videoTracks.ToList().IndexOf(videoTrack);
            selectorsToTry.Add($"v:{relVideoIdx}");
            selectorsToTry.Add("v:0");
            selectorsToTry.Add("v");
            selectorsToTry.Add("0");
            codecName = !string.IsNullOrEmpty(videoTrack.Codec) ? videoTrack.Codec : "Video";
        }
        else if (audioTrack != null)
        {
            int relAudioIdx = audioTracks.ToList().IndexOf(audioTrack);
            selectorsToTry.Add($"a:{relAudioIdx}");
            selectorsToTry.Add("a:0");
            selectorsToTry.Add("a");
            selectorsToTry.Add("0");
            codecName = !string.IsNullOrEmpty(audioTrack.Codec) ? audioTrack.Codec : "Audio";
        }
        else
        {
            selectorsToTry.Add("v:0");
            selectorsToTry.Add("a:0");
            selectorsToTry.Add("0");
        }

        selectorsToTry = selectorsToTry.Distinct().ToList();

        var perSecondBits = new Dictionary<int, long>();
        var framePacketsList = new List<FrameBitrateInfo>();
        var keyframeList = new List<double>();
        long totalBytesRead = 0;
        long totalPacketsCount = 0;

        progressCallback?.Invoke(5, "Покадровое считывание пакетов...");

        // 2. Вызов ffprobe с каскадным перебором селекторов в случае отсутствия пакетов
        foreach (var streamSelector in selectorsToTry)
        {
            if (cancellationToken.IsCancellationRequested) break;

            perSecondBits.Clear();
            framePacketsList.Clear();
            keyframeList.Clear();
            totalBytesRead = 0;
            totalPacketsCount = 0;

            string arguments = $"-hide_banner -loglevel quiet -select_streams {streamSelector} " +
                              $"-show_entries packet=flags,size,pts_time,dts_time -print_format compact \"{filePath}\"";

            Log.Info($"Попытка чтения пакетов битрейта с селектором '{streamSelector}' для '{Path.GetFileName(filePath)}'", "BitrateAnalyzerService");

            var runResult = await RunProcessAsync(
                "ffprobe",
                arguments,
                onOutputLine: line =>
                {
                    if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("packet|", StringComparison.Ordinal))
                        return;

                    totalPacketsCount++;

                    // Быстрый zero-allocation парсинг компактной строки: packet|flags=K_|pts_time=0.000000|dts_time=0.000000|size=12345
                    ReadOnlySpan<char> span = line.AsSpan();
                    
                    long size = 0;
                    double pts = -1.0;
                    double dts = -1.0;
                    bool isKey = false;

                    int start = 0;
                    while (start < span.Length)
                    {
                        int pipeIdx = span.Slice(start).IndexOf('|');
                        ReadOnlySpan<char> segment = pipeIdx < 0 ? span.Slice(start) : span.Slice(start, pipeIdx);
                        
                        int eqIdx = segment.IndexOf('=');
                        if (eqIdx > 0)
                        {
                            ReadOnlySpan<char> key = segment.Slice(0, eqIdx);
                            ReadOnlySpan<char> val = segment.Slice(eqIdx + 1);

                            if (key.Equals("size", StringComparison.Ordinal))
                            {
                                long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
                            }
                            else if (key.Equals("pts_time", StringComparison.Ordinal))
                            {
                                double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out pts);
                            }
                            else if (key.Equals("dts_time", StringComparison.Ordinal))
                            {
                                double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out dts);
                            }
                            else if (key.Equals("flags", StringComparison.Ordinal))
                            {
                                if (val.IndexOf('K') >= 0)
                                {
                                    isKey = true;
                                }
                            }
                        }

                        if (pipeIdx < 0) break;
                        start += pipeIdx + 1;
                    }

                    double packetTime = pts >= 0 ? pts : dts;
                    if (packetTime < 0) return;

                    int secondIndex = (int)Math.Floor(packetTime);
                    if (secondIndex >= 0)
                    {
                        perSecondBits.TryGetValue(secondIndex, out long currentBits);
                        perSecondBits[secondIndex] = currentBits + (size * 8);
                    }

                    framePacketsList.Add(new FrameBitrateInfo
                    {
                        PtsTime = packetTime,
                        SizeBytes = size,
                        IsKeyframe = isKey
                    });

                    totalBytesRead += size;

                    if (isKey)
                    {
                        keyframeList.Add(packetTime);
                    }

                    if (totalPacketsCount % 500 == 0 && duration > 0)
                    {
                        double pct = Math.Min(95.0, 5.0 + (packetTime / duration * 90.0));
                        progressCallback?.Invoke(pct, $"Анализ пакетов: {packetTime:F1} сек. / {duration:F1} сек.");
                    }
                },
                onErrorLine: null,
                cancellationToken
            );

            if (runResult.IsSuccess && perSecondBits.Count > 0)
            {
                Log.Info($"Успешно извлечено {totalPacketsCount} пакетов с селектором '{streamSelector}'", "BitrateAnalyzerService");
                break;
            }
            else
            {
                Log.Warn($"Селектор '{streamSelector}' не вернул пакетов (RunSuccess: {runResult.IsSuccess}, Packets: {totalPacketsCount}), проверяем следующий вариант...", "BitrateAnalyzerService");
            }
        }

        if (perSecondBits.Count == 0)
        {
            Log.Error($"Не удалось извлечь пакеты битрейта из файла '{filePath}' (все селекторы вернули 0 пакетов)", "BitrateAnalyzerService");
            return null;
        }

        if (duration <= 0 && framePacketsList.Count > 0)
        {
            duration = framePacketsList.Max(f => f.PtsTime);
        }

        if (duration > 0 && totalPacketsCount > 1 && videoTrack != null)
        {
            double calculatedFps = totalPacketsCount / duration;
            if (calculatedFps >= 1.0 && calculatedFps <= 360.0)
            {
                fps = Math.Round(calculatedFps, 3);
            }
        }

        progressCallback?.Invoke(95, "Расчет статистических показателей...");

        // 3. Формирование сплошного массива секунда-за-секундой
        int maxSec = perSecondBits.Keys.Max();
        double[] perSecondMbps = new double[maxSec + 1];

        for (int i = 0; i <= maxSec; i++)
        {
            if (perSecondBits.TryGetValue(i, out long bits))
            {
                perSecondMbps[i] = Math.Round(bits / 1_000_000.0, 3);
            }
            else
            {
                perSecondMbps[i] = 0.0;
            }
        }

        // 4. Расчет стат. показателей (Mean, Min, Max, StdDev)
        double mean = perSecondMbps.Length > 0 ? perSecondMbps.Average() : 0.0;
        double min = perSecondMbps.Length > 0 ? perSecondMbps.Min() : 0.0;
        double max = perSecondMbps.Length > 0 ? perSecondMbps.Max() : 0.0;
        
        double sumSquares = perSecondMbps.Sum(val => Math.Pow(val - mean, 2));
        double stdDev = perSecondMbps.Length > 1 ? Math.Sqrt(sumSquares / (perSecondMbps.Length - 1)) : 0.0;

        var result = new BitrateAnalysisResult
        {
            FilePath = filePath,
            CodecName = codecName,
            DurationSeconds = duration > 0 ? duration : perSecondMbps.Length,
            Fps = fps,
            TotalFrames = totalPacketsCount,
            PerSecondMbps = perSecondMbps,
            FramePackets = framePacketsList.ToArray(),
            KeyframeTimes = keyframeList.ToArray(),
            MeanMbps = Math.Round(mean, 2),
            MinMbps = Math.Round(min, 2),
            MaxMbps = Math.Round(max, 2),
            StdDevMbps = Math.Round(stdDev, 2)
        };

        progressCallback?.Invoke(100, "Завершено!");
        Log.Info($"Анализ битрейта завершен для '{Path.GetFileName(filePath)}'. Средний: {result.MeanMbps} Mbps, Мин: {result.MinMbps} Mbps, Макс: {result.MaxMbps} Mbps, Ключевых кадров: {result.KeyframeTimes.Length}", "BitrateAnalyzerService");

        return result;
    }
}
