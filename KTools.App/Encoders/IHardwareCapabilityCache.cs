// -*- coding: utf-8 -*-
using System.Threading.Tasks;

namespace KTools_App.Encoders;

/// <summary>
/// Интерфейс кэша аппаратных возможностей устройства.
/// Предоставляет синхронный доступ к информации о поддерживаемом аппаратном ускорении.
/// </summary>
public interface IHardwareCapabilityCache
{
    /// <summary>
    /// Возвращает true, если в системе обнаружена поддержка видеокарт NVIDIA (NVENC).
    /// </summary>
    bool IsNvencSupported { get; }

    /// <summary>
    /// Асинхронно инициализирует кэш, выполняя необходимые системные проверки.
    /// Должно вызываться один раз при старте приложения.
    /// </summary>
    Task InitializeAsync();
}
