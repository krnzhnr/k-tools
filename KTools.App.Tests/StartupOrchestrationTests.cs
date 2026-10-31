// -*- coding: utf-8 -*-
using System;
using System.IO;

using FluentAssertions;

using KTools_App.Tests.TestHelpers;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KTools_App.Tests;

/// <summary>
/// Проверки блока PERF-01: стартовая оркестрация не блокирует UI-поток.
/// Тесты исходников детерминированы (число запрещённых вызовов, порядок этапов),
/// поэтому не флакают на медленных машинах.
/// </summary>
[TestClass]
public class StartupOrchestrationTests
{
    /// <summary>
    /// Стартовый путь до первого кадра не должен содержать блокирующих вызовов:
    /// ранее опрос дисков, реестр shell и ретраи PendingArgs выполнялись синхронно.
    /// </summary>
    [TestMethod]
    public void OnLaunched_StartupPath_ContainsNoBlockingCalls()
    {
        // Arrange
        string source = ReadAppSource();
        string region = ExtractRegion(
            source,
            "protected override void OnLaunched(",
            "private static void WriteDiagnosticsStatus(");

        // Act and assert: синхронных блокировок в стартовом пути нет.
        region.Should().NotContain("Thread.Sleep", "UI-поток нельзя блокировать сном");
        region.Should().NotContain(".Result", "ожидание задач через Result вешает интерфейс");
        region.Should().NotContain(".Wait(", "ожидание задач через Wait вешает интерфейс");
        region.Should().NotContain("GetAwaiter().GetResult", "синхронное ожидание вешает интерфейс");
    }

    /// <summary>
    /// Тяжёлые работы обязаны стартовать после первого кадра, а не до него.
    /// </summary>
    [TestMethod]
    public void OnLaunched_BackgroundWork_QueuedAfterActivate()
    {
        // Arrange
        string source = ReadAppSource();
        string region = ExtractRegion(
            source,
            "protected override void OnLaunched(",
            "private static void WriteDiagnosticsStatus(");

        // Act
        int activateIndex = region.IndexOf("window.Activate()", StringComparison.Ordinal);
        int systemIndex = region.IndexOf("QueueSystemInfoSnapshotInBackground", StringComparison.Ordinal);
        int shellIndex = region.IndexOf("QueueShellIntegrationRefreshInBackground", StringComparison.Ordinal);

        // Assert
        activateIndex.Should().BeGreaterThanOrEqualTo(0, "окно обязано активироваться в OnLaunched");
        systemIndex.Should().BeGreaterThan(activateIndex, "снимок системы уходит в фон после Activate");
        shellIndex.Should().BeGreaterThan(activateIndex, "обновление shell уходит в фон после Activate");
        region.Should().NotContain(
            "ShellIntegration.Register(",
            "синхронной записи в реестр shell в OnLaunched быть не должно");
    }

    /// <summary>
    /// Цикл ретраев чтения PendingArgs обязан ждать через Task.Delay с отменой,
    /// а не через Thread.Sleep.
    /// </summary>
    [TestMethod]
    public void ProcessPendingArgsFilesAsync_RetryLoop_UsesTaskDelayWithCancellation()
    {
        // Arrange
        string source = ReadAppSource();
        string region = ExtractRegion(
            source,
            "public static void ProcessPendingArgsFiles()",
            "private static void StartArgsWatcher()");

        // Act and assert
        region.Should().NotContain("Thread.Sleep", "ретраи не должны блокировать поток сном");
        region.Should().Contain("Task.Delay", "паузы между попытками выполняются асинхронно");
        region.Should().Contain("cancellationToken", "ожидание прерывается при закрытии приложения");
    }

    /// <summary>
    /// Обработчики наблюдателя аргументов обязаны работать через дебаунс и фон
    /// без сна в потоке наблюдателя.
    /// </summary>
    [TestMethod]
    public void StartArgsWatcher_CreatedHandler_SchedulesDebouncedBackgroundWork()
    {
        // Arrange
        string source = ReadAppSource();
        string region = ExtractRegion(
            source,
            "private static void StartArgsWatcher()",
            "System.Runtime.InteropServices.DllImport");

        // Act and assert
        region.Should().NotContain("Thread.Sleep", "поток наблюдателя нельзя блокировать сном");
        region.Should().Contain("SchedulePendingArgsProcessing", "события уходят в планировщик фона");
        region.Should().Contain("ArgsWatcherDebounceDelay", "серии событий сливаются дебаунсом");
        region.Should().Contain("Task.Delay", "дебаунс реализован асинхронной задержкой");
    }

    /// <summary>
    /// Публичный метод обработки PendingArgs обязан быстро возвращаться без исключений
    /// даже вне приложения (диспетчер ещё не готов): вся работа выполняется в фоне.
    /// </summary>
    [TestMethod]
    public void ProcessPendingArgsFiles_WhenCalledOutsideApp_CompletesQuicklyWithoutThrow()
    {
        // Arrange and act: метод только ставит задачу в фон, исключений быть не должно.
        TimeSpan elapsed = PerfTiming.CompleteWithin(
            () => KTools_App.App.ProcessPendingArgsFiles(),
            TimeSpan.FromSeconds(10),
            "ProcessPendingArgsFiles");

        // Assert
        elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(10),
            "постановка фоновой задачи не должна блокировать вызывающий поток");
    }

    private static string ReadAppSource()
    {
        string? root = TryGetRepositoryRoot();
        root.Should().NotBeNull("корень репозитория с файлом KTools.sln обязан находиться");
        string path = Path.Combine(root!, "KTools.App", "App.xaml.cs");
        File.Exists(path).Should().BeTrue($"файл обязан существовать: {path}");
        return File.ReadAllText(path);
    }

    private static string ExtractRegion(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"маркер начала обязан существовать: {startMarker}");
        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, $"маркер конца обязан идти после начала: {endMarker}");
        return source.Substring(start, end - start);
    }

    private static string? TryGetRepositoryRoot()
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "KTools.sln")))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }
        }

        return null;
    }
}
