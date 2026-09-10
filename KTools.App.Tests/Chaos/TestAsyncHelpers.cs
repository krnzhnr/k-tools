// -*- coding: utf-8 -*-
using System;
using System.Threading.Tasks;

namespace KTools_App.Tests.Chaos;

/// <summary>
/// Асинхронные хелперы для стресс-тестов: поллинг условий с таймаутом
/// вместо недетерминированных Thread.Sleep.
/// </summary>
internal static class TestAsyncHelpers
{
    /// <summary>
    /// Ожидает выполнения условия, опрашивая его с фиксированным интервалом.
    /// </summary>
    /// <param name="condition">Условие завершения ожидания.</param>
    /// <param name="timeout">Максимальное время ожидания (по умолчанию 10 секунд).</param>
    /// <param name="interval">Интервал опроса (по умолчанию 50 мс).</param>
    /// <returns>true, если условие выполнено до истечения таймаута.</returns>
    public static async Task<bool> WaitForAsync(
        Func<bool> condition,
        TimeSpan? timeout = null,
        TimeSpan? interval = null)
    {
        TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromSeconds(10);
        TimeSpan effectiveInterval = interval ?? TimeSpan.FromMilliseconds(50);
        var deadline = DateTime.UtcNow + effectiveTimeout;

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(effectiveInterval);
        }

        return condition();
    }
}
