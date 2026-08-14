// -*- coding: utf-8 -*-
using System;
using System.Threading.Tasks;
using KTools_App.Services.Contracts;

namespace KTools_App.Encoders;

/// <summary>
/// Реализация кэша аппаратных возможностей устройства.
/// Опрашивает FFmpeg при запуске приложения и кэширует результаты 
/// для синхронного и быстрого доступа из конфигураторов энкодеров.
/// </summary>
public class HardwareCapabilityCache : IHardwareCapabilityCache
{
    private readonly IFFmpegRunner _ffmpegRunner;
    private readonly ILogService _logService;
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
        if (_isInitialized)
        {
            return;
        }

        try
        {
            _logService.Info("Инициализация кэша аппаратных возможностей...", "HardwareCapabilityCache");
            IsNvencSupported = await _ffmpegRunner.CheckNvencSupportAsync();
            _logService.Info($"Поддержка NVENC: {IsNvencSupported}", "HardwareCapabilityCache");

            if (IsNvencSupported)
            {
                IsNvencTemporalAqSupported = await CheckNvencTemporalAqAsync();
                _logService.Info($"Поддержка NVENC Temporal AQ: {IsNvencTemporalAqSupported}", "HardwareCapabilityCache");
            }
            else
            {
                IsNvencTemporalAqSupported = false;
            }
            
            _isInitialized = true;
        }
        catch (Exception ex)
        {
            _logService.Error($"Ошибка при инициализации кэша аппаратных возможностей: {ex.Message}", "HardwareCapabilityCache");
            IsNvencSupported = false;
            IsNvencTemporalAqSupported = false;
        }
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
