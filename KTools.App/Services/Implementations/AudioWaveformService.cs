// -*- coding: utf-8 -*-
using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using KTools_App.Services.Contracts;

namespace KTools_App.Services.Implementations;

/// <summary>
/// Реализация высокопроизводительного сервиса извлечения и подготовки осциллограмм (Waveform) с помощью SIMD и LOD уровня Adobe Audition.
/// Все комментарии, логи и исключения выполнены на русском языке в соответствии с регламентом.
/// </summary>
public sealed class AudioWaveformService : IAudioWaveformService
{
    private readonly ILogService _logService;
    private readonly IDependencyManager _dependencyManager;
    private readonly IPathManager _pathManager;

    public AudioWaveformService(
        ILogService logService,
        IDependencyManager dependencyManager,
        IPathManager pathManager)
    {
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _dependencyManager = dependencyManager ?? throw new ArgumentNullException(nameof(dependencyManager));
        _pathManager = pathManager ?? throw new ArgumentNullException(nameof(pathManager));
    }

    /// <inheritdoc />
    public async Task<WaveformLevelData> ExtractAndGeneratePeaksAsync(
        string mediaFilePath,
        int audioTrackIndex,
        Action<double, string>? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mediaFilePath))
        {
            throw new ArgumentException("Путь к медиафайлу не может быть пустым.", nameof(mediaFilePath));
        }

        string originalName = Path.GetFileName(mediaFilePath);
        _logService.Info($"Начало сверхточной генерации осциллограммы Audition-уровня для файла '{originalName}', дорожка: a:{audioTrackIndex}", "AudioWaveformService");

        string ffmpegPath = _pathManager.GetBinaryPath("ffmpeg");
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
        {
            _logService.Warn("kt-ffmpeg не найден, построение осциллограммы невозможно", "AudioWaveformService");
            return new WaveformLevelData
            {
                SampleRate = 44100,
                TotalDurationSeconds = 0.0,
                Peaks1000Hz = Array.Empty<WaveformPeak>(),
                Peaks200Hz = Array.Empty<WaveformPeak>(),
                Peaks50Hz = Array.Empty<WaveformPeak>(),
                Peaks10Hz = Array.Empty<WaveformPeak>(),
                Peaks1Hz = Array.Empty<WaveformPeak>()
            };
        }

        string tempWavPath = Path.Combine(Path.GetTempPath(), $"waveform_{Guid.NewGuid():N}.wav");

        try
        {
            progressCallback?.Invoke(5.0, "Извлечение аудиопотока в PCM 16-бит...");

            var processStartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-y -i \"{mediaFilePath}\" -map 0:a:{audioTrackIndex} -ac 1 -ar 44100 -c:a pcm_s16le -vn \"{tempWavPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            using (var process = new Process { StartInfo = processStartInfo })
            {
                var stderrBuilder = new System.Text.StringBuilder();

                process.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                    {
                        stderrBuilder.AppendLine(e.Data);
                    }
                };

                process.Start();
                process.BeginErrorReadLine();
                process.BeginOutputReadLine();

                var tcs = new TaskCompletionSource<bool>();
                using (cancellationToken.Register(() =>
                {
                    try
                    {
                        if (!process.HasExited) process.Kill();
                    }
                    catch { }
                    tcs.TrySetCanceled();
                }))
                {
                    process.EnableRaisingEvents = true;
                    process.Exited += (s, e) => tcs.TrySetResult(true);

                    await tcs.Task;
                }

                if (process.ExitCode != 0 && !cancellationToken.IsCancellationRequested)
                {
                    throw new InvalidOperationException($"FFmpeg завершился с кодом ошибки {process.ExitCode}: {stderrBuilder}");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            progressCallback?.Invoke(40.0, "Расчет пиков ультра-детализированной осциллограммы (1 мс / SIMD)...");

            var waveformData = await Task.Run(() => ComputePeaksFromWavFile(tempWavPath, progressCallback, cancellationToken), cancellationToken);

            _logService.Info($"Осциллограмма сверхвысокой детализации для '{originalName}' сгенерирована. Длительность: {waveformData.TotalDurationSeconds:F2} сек.", "AudioWaveformService");
            progressCallback?.Invoke(100.0, "Готово");

            return waveformData;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logService.Exception(ex, $"Ошибка генерации осциллограммы для файла '{originalName}': {ex.Message}", "AudioWaveformService");
            throw;
        }
        finally
        {
            if (File.Exists(tempWavPath))
            {
                try
                {
                    File.Delete(tempWavPath);
                }
                catch (Exception ex)
                {
                    _logService.DebugLog($"Не удалось удалить временный файл осциллограммы '{tempWavPath}': {ex.Message}", "AudioWaveformService");
                }
            }
        }
    }

    /// <summary>
    /// Вспомогательный метод быстрой обработки WAV файла с использованием SIMD (1000 Гц = 1 мс).
    /// </summary>
    private WaveformLevelData ComputePeaksFromWavFile(
        string wavPath,
        Action<double, string>? progressCallback,
        CancellationToken cancellationToken)
    {
        const int sampleRate = 44100;
        const int wavHeaderSize = 44;

        using var fileStream = new FileStream(wavPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536, useAsync: false);
        if (fileStream.Length <= wavHeaderSize)
        {
            return new WaveformLevelData
            {
                SampleRate = sampleRate,
                TotalDurationSeconds = 0.0,
                Peaks1000Hz = Array.Empty<WaveformPeak>(),
                Peaks200Hz = Array.Empty<WaveformPeak>(),
                Peaks50Hz = Array.Empty<WaveformPeak>(),
                Peaks10Hz = Array.Empty<WaveformPeak>(),
                Peaks1Hz = Array.Empty<WaveformPeak>()
            };
        }

        fileStream.Seek(wavHeaderSize, SeekOrigin.Begin);
        long pcmBytesLength = fileStream.Length - wavHeaderSize;
        long totalSamples = pcmBytesLength / 2; // 16-бит mono

        double duration = (double)totalSamples / sampleRate;

        // 1000 Гц = 44.1 сэмпла на 1 мс пика
        int samplesPerPeak1000Hz = 44;
        int count1000Hz = (int)Math.Ceiling((double)totalSamples / samplesPerPeak1000Hz);

        var peaks1000Hz = new WaveformPeak[count1000Hz];

        byte[] rawBuffer = new byte[samplesPerPeak1000Hz * 2];
        short[] sampleBuffer = new short[samplesPerPeak1000Hz];

        long currentSampleIndex = 0;
        int peakIndex = 0;

        while (currentSampleIndex < totalSamples)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int bytesToRead = (int)Math.Min(rawBuffer.Length, (totalSamples - currentSampleIndex) * 2);
            int bytesRead = fileStream.Read(rawBuffer, 0, bytesToRead);
            if (bytesRead <= 0) break;

            int samplesRead = bytesRead / 2;

            MemoryMarshal.Cast<byte, short>(rawBuffer.AsSpan(0, bytesRead)).CopyTo(sampleBuffer);

            short min = short.MaxValue;
            short max = short.MinValue;

            int i = 0;
            if (Vector.IsHardwareAccelerated && samplesRead >= Vector<short>.Count)
            {
                var minVec = new Vector<short>(short.MaxValue);
                var maxVec = new Vector<short>(short.MinValue);

                int vectorSize = Vector<short>.Count;
                int limit = samplesRead - vectorSize;

                for (; i <= limit; i += vectorSize)
                {
                    var vec = new Vector<short>(sampleBuffer, i);
                    minVec = Vector.Min(minVec, vec);
                    maxVec = Vector.Max(maxVec, vec);
                }

                for (int v = 0; v < vectorSize; v++)
                {
                    if (minVec[v] < min) min = minVec[v];
                    if (maxVec[v] > max) max = maxVec[v];
                }
            }

            for (; i < samplesRead; i++)
            {
                short val = sampleBuffer[i];
                if (val < min) min = val;
                if (val > max) max = val;
            }

            if (min > max)
            {
                min = 0;
                max = 0;
            }

            float minNorm = min / 32768.0f;
            float maxNorm = max / 32768.0f;

            if (peakIndex < count1000Hz)
            {
                peaks1000Hz[peakIndex++] = new WaveformPeak(minNorm, maxNorm);
            }

            currentSampleIndex += samplesRead;

            if (peakIndex % 2000 == 0)
            {
                double progressPct = 40.0 + (50.0 * currentSampleIndex / totalSamples);
                progressCallback?.Invoke(progressPct, "Расчет миллисекундных пиков (1000 Гц)...");
            }
        }

        // Пирамида понижения разрешения для масштабов (200Гц, 50Гц, 10Гц, 1Гц)
        var peaks200Hz = DownsamplePeaks(peaks1000Hz, 5);
        var peaks50Hz = DownsamplePeaks(peaks200Hz, 4);
        var peaks10Hz = DownsamplePeaks(peaks50Hz, 5);
        var peaks1Hz = DownsamplePeaks(peaks10Hz, 10);

        return new WaveformLevelData
        {
            SampleRate = sampleRate,
            TotalDurationSeconds = duration,
            Peaks1000Hz = peaks1000Hz,
            Peaks200Hz = peaks200Hz,
            Peaks50Hz = peaks50Hz,
            Peaks10Hz = peaks10Hz,
            Peaks1Hz = peaks1Hz
        };
    }

    private static WaveformPeak[] DownsamplePeaks(WaveformPeak[] input, int factor)
    {
        int count = (int)Math.Ceiling((double)input.Length / factor);
        var output = new WaveformPeak[count];
        for (int b = 0; b < count; b++)
        {
            int start = b * factor;
            int end = Math.Min(start + factor, input.Length);
            float min = float.MaxValue;
            float max = float.MinValue;
            for (int k = start; k < end; k++)
            {
                if (input[k].Min < min) min = input[k].Min;
                if (input[k].Max > max) max = input[k].Max;
            }
            output[b] = new WaveformPeak(min == float.MaxValue ? 0f : min, max == float.MinValue ? 0f : max);
        }
        return output;
    }
}
