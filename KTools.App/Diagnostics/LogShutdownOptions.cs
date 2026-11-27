// -*- coding: utf-8 -*-
using System;

namespace KTools_App.Diagnostics;

/// <summary>
/// Единые таймауты завершения журналирования (ишью 140).
/// Раньше сброс размазывался по трём местам: Flush 5 с в LogService,
/// ожидание Dispose 750 мс и ShutdownFlushTimeout 1 с в App — краш-события
/// усекались. Все стадии завершения используют один набор лимитов.
/// </summary>
public sealed record LogShutdownOptions
{
    /// <summary>
    /// Таймаут сброса очереди по требованию Flush.
    /// </summary>
    public TimeSpan FlushTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Ожидание дренажа очереди и выхода писателя при Dispose.
    /// </summary>
    public TimeSpan DisposeDrainTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Ожидание насосов подписчиков при Dispose.
    /// </summary>
    public TimeSpan SubscriberDrainTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Сброс журнала на стадии контролируемого завершения (App).
    /// Совпадает с FlushTimeout, чтобы краш-события не терялись за 1 с.
    /// </summary>
    public TimeSpan ShutdownFlushTimeout => FlushTimeout;

    /// <summary>
    /// Значения по умолчанию для production и тестов.
    /// </summary>
    public static LogShutdownOptions Default => new();
}
