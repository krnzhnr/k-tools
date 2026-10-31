// -*- coding: utf-8 -*-
using System;
using System.Diagnostics;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KTools_App.Tests.TestHelpers;

/// <summary>
/// Хелпер юнит-замеров длительности для PERF-блоков.
/// Это триггеры регресса с щедрым бюджетом, а не бенчмарки: машина общая,
/// тайминги плавают, поэтому бюджет брать минимум с 3–5-кратным запасом
/// от медианы. Пример:
/// <code>
/// TimeSpan elapsed = PerfTiming.CompleteWithin(
///     () => manager.RefreshAllStatuses(),
///     TimeSpan.FromSeconds(5),
///     "RefreshAllStatuses");
/// </code>
/// </summary>
public static class PerfTiming
{
    /// <summary>
    /// Замеряет длительность синхронного действия.
    /// </summary>
    /// <param name="action">Замеряемое действие.</param>
    /// <returns>Прошедшее время.</returns>
    public static TimeSpan Measure(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Stopwatch watch = Stopwatch.StartNew();
        action();
        watch.Stop();
        return watch.Elapsed;
    }

    /// <summary>
    /// Замеряет длительность асинхронного действия.
    /// </summary>
    /// <param name="action">Замеряемое действие.</param>
    /// <returns>Прошедшее время.</returns>
    public static async Task<TimeSpan> MeasureAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Stopwatch watch = Stopwatch.StartNew();
        await action().ConfigureAwait(false);
        watch.Stop();
        return watch.Elapsed;
    }

    /// <summary>
    /// Выполняет действие и падает, если оно не уложилось в бюджет.
    /// В сообщение попадает фактическое время для диагностики.
    /// </summary>
    /// <param name="action">Замеряемое действие.</param>
    /// <param name="budget">Щедрый бюджет с запасом.</param>
    /// <param name="operation">Название операции для сообщения.</param>
    /// <returns>Фактическое время выполнения.</returns>
    public static TimeSpan CompleteWithin(Action action, TimeSpan budget, string operation)
    {
        TimeSpan elapsed = Measure(action);
        if (elapsed > budget)
        {
            Assert.Fail($"Операция '{operation}' заняла {elapsed.TotalSeconds:F1} с при бюджете {budget.TotalSeconds:F1} с.");
        }

        return elapsed;
    }

    /// <summary>
    /// Выполняет асинхронное действие и падает, если оно не уложилось в бюджет.
    /// </summary>
    /// <param name="action">Замеряемое действие.</param>
    /// <param name="budget">Щедрый бюджет с запасом.</param>
    /// <param name="operation">Название операции для сообщения.</param>
    /// <returns>Фактическое время выполнения.</returns>
    public static async Task<TimeSpan> CompleteWithinAsync(Func<Task> action, TimeSpan budget, string operation)
    {
        TimeSpan elapsed = await MeasureAsync(action).ConfigureAwait(false);
        if (elapsed > budget)
        {
            Assert.Fail($"Операция '{operation}' заняла {elapsed.TotalSeconds:F1} с при бюджете {budget.TotalSeconds:F1} с.");
        }

        return elapsed;
    }
}
