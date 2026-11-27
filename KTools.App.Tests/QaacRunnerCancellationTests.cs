// -*- coding: utf-8 -*-
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Infrastructure;

namespace KTools_App.Tests;

/// <summary>
/// Тесты отмены конвейера QAAC: завершение обоих процессов (ffmpeg и qaac64)
/// доказывается ожиданием, а не только вызовом Kill; снятие процессов с учёта
/// выполняется только после подтверждённого выхода, без окна невидимости для KillAll.
/// В качестве фейковых процессов используются долгоживущие команды ping,
/// как в тестах слоя процессов. Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class QaacRunnerCancellationTests
{
    private static string ComSpec => Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";

    private readonly List<Process> _ownedProcesses = new();

    [TestCleanup]
    public void Cleanup()
    {
        foreach (Process proc in _ownedProcesses)
        {
            try
            {
                ActiveProcessTracker.TerminateProcess(proc, TimeSpan.FromMilliseconds(500));
            }
            catch (Exception)
            {
            }

            try
            {
                ActiveProcessTracker.Unregister(proc);
            }
            catch (Exception)
            {
            }

            try
            {
                proc.Dispose();
            }
            catch (Exception)
            {
            }
        }

        _ownedProcesses.Clear();
    }

    /// <summary>
    /// Отмена конвейера обязана завершить оба процесса с доказанным выходом,
    /// дождаться ридеров конвейера и снять процессы с учёта.
    /// </summary>
    [TestMethod]
    public async Task TerminatePipelineAsync_LiveFakeProcesses_KillsBothWaitsReadersAndUnregistersAfterExit()
    {
        // Arrange: два живых фейковых процесса — стенды ffmpeg и qaac64.
        // Регистрируем их так же, как это делает QaacRunner.RunAsync при запуске.
        Process ffmpegFake = StartFakePipelineProcess();
        Process qaacFake = StartFakePipelineProcess();
        ActiveProcessTracker.Register(ffmpegFake);
        ActiveProcessTracker.Register(qaacFake);

        // Ридер, который завершается только после наблюдаемой смерти обоих процессов
        // с дополнительной задержкой: доказывает, что помощник действительно
        // дожидается ридеров, а не возвращается сразу после Kill.
        var readersGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task readerMonitor = Task.Run(async () =>
        {
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while ((!ffmpegFake.HasExited || !qaacFake.HasExited) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10).ConfigureAwait(false);
            }

            await Task.Delay(500).ConfigureAwait(false);
            readersGate.TrySetResult(true);
        });

        MethodInfo? method = typeof(QaacRunner).GetMethod(
            "TerminatePipelineAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull(
            "отмена конвейера AAC обязана завершать оба процесса проверяемым способом");

        // Act
        var stopwatch = Stopwatch.StartNew();
        Task<bool> terminateTask = (Task<bool>)method!.Invoke(
            null,
            new object[] { ffmpegFake, qaacFake, readersGate.Task, Task.CompletedTask, Task.CompletedTask })!;
        bool terminationVerified = await terminateTask.ConfigureAwait(false);
        stopwatch.Stop();
        await readerMonitor.ConfigureAwait(false);

        // Assert
        terminationVerified.Should().BeTrue(
            "оба процесса завершены, факт выхода доказан ожиданием TerminateProcess");
        ffmpegFake.HasExited.Should().BeTrue(
            "процесс декодера не должен оставаться сиротой после отмены");
        qaacFake.HasExited.Should().BeTrue(
            "процесс кодировщика не должен оставаться сиротой после отмены");
        readersGate.Task.IsCompletedSuccessfully.Should().BeTrue(
            "помощник обязан дождаться ридеров конвейера перед возвратом");
        stopwatch.Elapsed.Should().BeGreaterThanOrEqualTo(
            TimeSpan.FromMilliseconds(300),
            "возврат до завершения ридеров означает пропуск ожидания");
        IsTracked(ffmpegFake).Should().BeFalse(
            "завершённый декодер снимается с учёта и не засоряет трекер");
        IsTracked(qaacFake).Should().BeFalse(
            "завершённый кодировщик снимается с учёта и не засоряет трекер");
    }

    /// <summary>
    /// Завершение живого процесса обязано доказывать факт выхода ожиданием
    /// и возвращать проверяемый результат вместо fire-and-forget Kill.
    /// </summary>
    [TestMethod]
    public void TryTerminate_LiveFakeProcess_ProvesExitByWaiting()
    {
        // Arrange
        Process fake = StartFakePipelineProcess();
        ActiveProcessTracker.Register(fake);

        MethodInfo? method = typeof(QaacRunner).GetMethod(
            "TryTerminate",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull(
            "завершение процесса конвейера обязано существовать как проверяемая операция");

        // Act
        object? raw = method!.Invoke(null, new object[] { fake });

        // Assert
        raw.Should().BeOfType<ProcessTerminationResult>(
            "завершение обязано возвращать проверяемый результат, а не void без ожидания");
        var result = (ProcessTerminationResult)raw!;
        result.Verified.Should().BeTrue(
            "факт выхода доказывается ожиданием, а не только вызовом Kill");
        fake.HasExited.Should().BeTrue(
            "процесс обязан быть мёртв к моменту возврата");
    }

    /// <summary>
    /// Уже завершённый процесс считается корректно остановленным без исключений.
    /// </summary>
    [TestMethod]
    public void TryTerminate_AlreadyExitedProcess_ReportsVerifiedWithoutThrowing()
    {
        // Arrange
        Process fake = StartFakePipelineProcess();
        fake.Kill(entireProcessTree: true);
        fake.WaitForExit(5000);

        MethodInfo? method = typeof(QaacRunner).GetMethod(
            "TryTerminate",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        // Act
        object? raw = method!.Invoke(null, new object[] { fake });

        // Assert
        raw.Should().BeOfType<ProcessTerminationResult>();
        var result = (ProcessTerminationResult)raw!;
        result.Verified.Should().BeTrue(
            "уже завершённый процесс считается корректно остановленным");
    }

    private Process StartFakePipelineProcess()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ComSpec,
            Arguments = "/c ping -n 30 127.0.0.1 > nul",
            CreateNoWindow = true,
            UseShellExecute = false
        };

        Process? proc = Process.Start(startInfo);
        proc.Should().NotBeNull("фейковый процесс конвейера обязан запускаться");
        _ownedProcesses.Add(proc!);
        return proc!;
    }

    private static bool IsTracked(Process proc)
    {
        FieldInfo? field = typeof(ActiveProcessTracker).GetField(
            "Tracked",
            BindingFlags.NonPublic | BindingFlags.Static);
        field.Should().NotBeNull("контракт ActiveProcessTracker изменился: поле Tracked недоступно");

        var tracked = (IDictionary?)field!.GetValue(null);
        tracked.Should().NotBeNull("реестр трекера обязан быть доступен");

        foreach (object? entry in tracked!.Values)
        {
            PropertyInfo? sourceProperty = entry?.GetType().GetProperty("Source");
            if (ReferenceEquals(sourceProperty?.GetValue(entry), proc))
            {
                return true;
            }
        }

        return false;
    }
}
