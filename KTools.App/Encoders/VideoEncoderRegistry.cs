// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;

namespace KTools_App.Encoders;

/// <summary>
/// Реестр всех доступных видео-энкодеров в системе.
/// Отфильтровывает энкодеры, которые не поддерживаются аппаратным обеспечением.
/// </summary>
public class VideoEncoderRegistry
{
    private readonly IEnumerable<IVideoEncoder> _allEncoders;
    private readonly IHardwareCapabilityCache _hardwareCache;

    public VideoEncoderRegistry(IEnumerable<IVideoEncoder> allEncoders, IHardwareCapabilityCache hardwareCache)
    {
        _allEncoders = allEncoders ?? throw new ArgumentNullException(nameof(allEncoders));
        _hardwareCache = hardwareCache ?? throw new ArgumentNullException(nameof(hardwareCache));
    }

    /// <summary>
    /// Возвращает список энкодеров, которые поддерживаются в текущей конфигурации оборудования.
    /// Этот список используется для заполнения UI и валидации.
    /// </summary>
    public IReadOnlyList<IVideoEncoder> GetAvailableEncoders()
    {
        return _allEncoders.Where(IsEncoderSupported).ToList();
    }

    /// <summary>
    /// Находит энкодер по его стабильному идентификатору (например, "nvenc", "x265").
    /// Если энкодер не найден или не поддерживается, возвращает null.
    /// </summary>
    public IVideoEncoder? GetEncoderById(string stableId)
    {
        return GetAvailableEncoders().FirstOrDefault(e => e.StableId == stableId);
    }

    private bool IsEncoderSupported(IVideoEncoder encoder)
    {
        return encoder.IsSupported(_hardwareCache);
    }
}
