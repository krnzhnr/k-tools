// -*- coding: utf-8 -*-
using System;
using System.Threading.Tasks;

using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Services.Contracts;

namespace KTools_App.Encoders;

/// <summary>
/// Реализация кэша аппаратных возможностей устройства.
/// Опрашивает FFmpeg при запуске приложения и кэширует результаты 
/// для синхронного и быстрого доступа из конфигураторов энкодеров.
/// </summary>
public class HardwareCapabilityCache : IHardwareCapabilityCache
{
    private const string SourceName = nameof(HardwareCapabilityCache);

    private readonly IFFmpegRunner _ffmpegRunner;
    private readonly ILogService _logService;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _isInitialized = false;

    public HardwareCapabilityCache(IFFmpegRunner ffmpegRunner, ILogService logService)
    {
        _ffmpegRunner = ffmpegRunner ?? throw new ArgumentNullException(nameof(ffmpegRunner));
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
    }

    public bool IsNvencSupported { get; private set; }
    public bool IsNvencTemporalAqSupported { get; private set; }

    public async Task InitializeAsync()
    {
        await _initLock.WaitAsync();
        try
        {
            if (_isInitialized)
            {
                return;
            }

            try
            {
                _logService.Write(
                    "encoder.capabilities.init_started",
                    LogLevel.Debug,
                    LogStatus.Running,
                    "Начинается опрос аппаратных возможностей кодирования",
                    source: SourceName);
                IsNvencSupported = await _ffmpegRunner.CheckNvencSupportAsync();
                _logService.Write(
                    "encoder.capabilities.detected",
                    LogLevel.Info,
                    LogStatus.Succeeded,
                    $"Аппаратные возможности кодирования определены: NVENC {(IsNvencSupported ? "доступен" : "недоступен")}",
                    source: SourceName,
                    properties: LogProps
                        .Create("Verified", IsNvencSupported)
                        .With("Tool", "ffmpeg")
                        .With("Codec", "h264_nvenc"));

                if (IsNvencSupported)
                {
                    IsNvencTemporalAqSupported = await CheckNvencTemporalAqAsync();
                    _logService.Write(
                        "encoder.capabilities.temporal_aq_detected",
                        LogLevel.Debug,
                        LogStatus.Succeeded,
                        $"NVENC Temporal AQ {(IsNvencTemporalAqSupported ? "доступен" : "недоступен")}",
                        source: SourceName,
                        properties: LogProps
                            .Create("Verified", IsNvencTemporalAqSupported)
                            .With("Codec", "h264_nvenc"));
                }
                else
                {
                    IsNvencTemporalAqSupported = false;
                }

                _isInitialized = true;
            }
            catch (Exception ex)
            {
                _logService.Write(
                    "encoder.capabilities.init_failed",
                    LogLevel.Warning,
                    LogStatus.Failed,
                    "Аппаратные возможности кодирования не определены, аппаратное ускорение будет недоступно",
                    ex,
                    SourceName,
                    properties: LogProps
                        .Create("ErrorCode", "ENCODER_CAPABILITIES_INIT_FAILED")
                        .With("Tool", "ffmpeg"));
                IsNvencSupported = false;
                IsNvencTemporalAqSupported = false;
            }
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Сбрасывает кэш, чтобы при следующем вызове InitializeAsync проверки выполнились заново.
    /// </summary>
    public void Invalidate()
    {
        _isInitialized = false;
        IsNvencSupported = false;
        IsNvencTemporalAqSupported = false;
    }

    private async Task<bool> CheckNvencTemporalAqAsync()
    {
        try
        {
            return await _ffmpegRunner.CheckNvencTemporalAqSupportAsync();
        }
        catch
        {
            return false;
        }
    }
}
