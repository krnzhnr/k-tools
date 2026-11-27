// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Models;
using KTools_App.Services.Contracts;
using KTools_App.Tests.TestHelpers;
using KTools_App.ViewModels;
using ExecutionContext = KTools_App.Models.ExecutionContext;

namespace KTools_App.Tests.ViewModels;

/// <summary>
/// Проверяет изоляцию параллельных сессий очередей за индикатор панели задач (ишью #123):
/// две сессии делят один значок окна, поэтому отчёты и сброс привязаны
/// к идентификатору операции с подсчётом владельцев — чужая сессия не сбрасывает
/// чужой прогресс и не гасит индикатор раньше финиша последней очереди.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class WorkPanelTaskbarRaceTests : IsolatedMessengerTestBase
{
    private static readonly TimeSpan SyncTimeout = TimeSpan.FromSeconds(30);

    private Mock<INavigationService> _navigationMock = null!;
    private Mock<IDialogService> _dialogMock = null!;
    private Mock<ISettingsManager> _settingsMock = null!;
    private Mock<ILogService> _logMock = null!;
    private Mock<IDependencyManager> _dependencyMock = null!;
    private Mock<IMediaProbeService> _probeMock = null!;
    private Mock<ITaskbarProgressService> _taskbarMock = null!;

    private readonly List<double> _progressReports = new();
    private readonly object _reportsLock = new();

    [TestInitialize]
    public void Setup()
    {
        _navigationMock = MockBuilders.CreateNavigationMock();
        _dialogMock = MockBuilders.CreateDialogMock();
        _settingsMock = MockBuilders.CreateSettingsManagerMock();
        _logMock = MockBuilders.CreateLogServiceMock();
        _dependencyMock = MockBuilders.CreateDependencyManagerMock(allInstalled: true);
        _probeMock = MockBuilders.CreateMediaProbeMock();
        _taskbarMock = new Mock<ITaskbarProgressService>();
        lock (_reportsLock)
        {
            _progressReports.Clear();
        }

        _taskbarMock.Setup(t => t.SetProgress(It.IsAny<double>()))
            .Callback<double>(percent =>
            {
                lock (_reportsLock)
                {
                    _progressReports.Add(percent);
                }
            });
    }

    private WorkPanelViewModel CreateViewModel()
    {
        var vm = new WorkPanelViewModel(
            _navigationMock.Object,
            _dialogMock.Object,
            _settingsMock.Object,
            _logMock.Object,
            _dependencyMock.Object,
            _probeMock.Object,
            _taskbarMock.Object);
        MessengerIsolation.Track(vm);
        return vm;
    }

    private StubScript CreateScript(
        string name,
        Func<string, Dictionary<string, object>, ExecutionContext, Task<ExecutionResult>>? handler = null)
    {
        var script = new StubScript(
            _logMock.Object,
            _settingsMock.Object,
            MockBuilders.CreatePathManagerMock().Object,
            name: name);
        script.ExecuteHandler = handler ?? ((file, settings, context) =>
            Task.FromResult(ExecutionResult.Succeeded(context, $"Готово: {file}")));
        return script;
    }

    private static ObservableCollection<FileQueueItem> CreateFiles(params string[] paths)
    {
        var files = new ObservableCollection<FileQueueItem>();
        foreach (var path in paths)
        {
            files.Add(new FileQueueItem(path));
        }

        return files;
    }

    private List<double> ProgressSnapshot()
    {
        lock (_reportsLock)
        {
            return _progressReports.ToList();
        }
    }

    /// <summary>
    /// Проверяет, что старт второй сессии не сбрасывает индикатор первой:
    /// чужие нулевые отчёты подавляются, пока первая сессия владеет значком.
    /// </summary>
    [TestMethod]
    public async Task ReportTaskbarProgress_SecondSessionStartsWhileFirstRuns_FirstSessionKeepsIndicator()
    {
        // Arrange: первая сессия сообщает 50% и ждёт разрешения на финиш.
        var firstHalfReported = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = CreateScript("Скрипт А");
        first.ProgressHandler = (callback, index, total) =>
        {
            callback(index, total, "Половина очереди", 50.0);
            firstHalfReported.TrySetResult(true);
        };
        first.ExecuteHandler = async (file, settings, context) =>
        {
            await releaseFirst.Task;
            return ExecutionResult.Succeeded(context, $"Готово: {file}");
        };

        var second = CreateScript("Скрипт Б");
        second.ExecuteHandler = async (file, settings, context) =>
        {
            secondStarted.TrySetResult(true);
            await releaseSecond.Task;
            return ExecutionResult.Succeeded(context, $"Готово: {file}");
        };

        var vm = CreateViewModel();
        vm.Initialize(first, CreateFiles("C:\\media\\a1.mkv"));

        try
        {
            // Act: запускаем первую сессию, дожимаем её до 50%, затем стартуем вторую.
            var firstTask = vm.StartExecutionCommand.ExecuteAsync(null);
            await firstHalfReported.Task.WaitAsync(SyncTimeout);

            vm.Initialize(second, CreateFiles("C:\\media\\b1.mkv"));
            var secondTask = vm.StartExecutionCommand.ExecuteAsync(null);
            // Обработчик второй сессии вызван строго после её нулевых отчётов
            // (старт элемента отчитывается раньше ExecuteSingleAsync).
            await secondStarted.Task.WaitAsync(SyncTimeout);

            // Assert: после отметки 50% первой сессии нулевые отчёты чужой сессии
            // не должны попадать в индикатор.
            List<double> reports = ProgressSnapshot();
            int halfIndex = reports.IndexOf(50.0);
            halfIndex.Should().BeGreaterThanOrEqualTo(0, "первая сессия обязана отчитаться о половине очереди");
            reports.Skip(halfIndex + 1).Should().NotContain(0.0,
                "старт второй сессии не сбрасывает индикатор первой");

            // Cleanup: завершаем обе сессии и проверяем итоговый сброс один раз.
            releaseFirst.TrySetResult(true);
            await firstTask.WaitAsync(SyncTimeout);
            releaseSecond.TrySetResult(true);
            await secondTask.WaitAsync(SyncTimeout);

            first.SavedGlobalProgress.Should().Be(100.0, "сохранённый прогресс первой сессии доводится до её итога");
            second.SavedGlobalProgress.Should().Be(100.0, "сохранённый прогресс второй сессии доводится до её итога");
            _taskbarMock.Verify(t => t.SetError(It.IsAny<double>()), Times.Never,
                "успешные сессии не показывают красное состояние");
            _taskbarMock.Verify(t => t.Clear(), Times.Once,
                "индикатор сбрасывается один раз — после финиша последней сессии");
        }
        finally
        {
            releaseFirst.TrySetResult(true);
            releaseSecond.TrySetResult(true);
        }
    }

    /// <summary>
    /// Проверяет, что пустая очередь чужой сессии не гасит индикатор активной.
    /// </summary>
    [TestMethod]
    public async Task ReportTaskbarFinished_EmptyQueueWhileOtherRuns_DoesNotClearIndicator()
    {
        // Arrange: первая сессия сообщает 50% и ждёт разрешения на финиш.
        var firstHalfReported = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = CreateScript("Скрипт А");
        first.ProgressHandler = (callback, index, total) =>
        {
            callback(index, total, "Половина очереди", 50.0);
            firstHalfReported.TrySetResult(true);
        };
        first.ExecuteHandler = async (file, settings, context) =>
        {
            await releaseFirst.Task;
            return ExecutionResult.Succeeded(context, $"Готово: {file}");
        };

        var second = CreateScript("Скрипт Б");
        var vm = CreateViewModel();
        vm.Initialize(first, CreateFiles("C:\\media\\a1.mkv"));

        try
        {
            // Act: активная сессия работает, чужая запускается с пустой очередью.
            var firstTask = vm.StartExecutionCommand.ExecuteAsync(null);
            await firstHalfReported.Task.WaitAsync(SyncTimeout);

            vm.Initialize(second, CreateFiles());
            await vm.StartExecutionCommand.ExecuteAsync(null);

            // Assert: чужой сброс подавлен, пока первая сессия жива.
            _taskbarMock.Verify(t => t.Clear(), Times.Never,
                "пустая очередь чужой сессии не сбрасывает индикатор активной");
            second.SavedStatusText.Should().Be("Очередь пуста",
                "пустая очередь по-прежнему фиксирует своё состояние");

            // Cleanup: первая сессия завершается и гасит индикатор.
            releaseFirst.TrySetResult(true);
            await firstTask.WaitAsync(SyncTimeout);

            _taskbarMock.Verify(t => t.Clear(), Times.Once,
                "индикатор сбрасывается после финиша последней активной сессии");
        }
        finally
        {
            releaseFirst.TrySetResult(true);
        }
    }

    /// <summary>
    /// Проверяет, что ошибка чужой сессии не красит индикатор в красный,
    /// пока первая сессия продолжает работу.
    /// </summary>
    [TestMethod]
    public async Task ReportTaskbarFinished_FailedSessionWhileOtherRuns_DoesNotSetError()
    {
        // Arrange: первая сессия ждёт, вторая мгновенно завершается ошибкой.
        var firstItemStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = CreateScript("Скрипт А");
        first.ProgressHandler = (callback, index, total) =>
        {
            firstItemStarted.TrySetResult(true);
        };
        first.ExecuteHandler = async (file, settings, context) =>
        {
            await releaseFirst.Task;
            return ExecutionResult.Succeeded(context, $"Готово: {file}");
        };

        var second = CreateScript("Скрипт Б", (file, settings, context) =>
            Task.FromResult(ExecutionResult.Failed(
                context,
                "Ошибка декодирования",
                errorCode: "decode-failed",
                cleanupState: CleanupState.Completed)));

        var vm = CreateViewModel();
        vm.Initialize(first, CreateFiles("C:\\media\\a1.mkv"));

        try
        {
            // Act: первая сессия работает, вторая падает целиком.
            var firstTask = vm.StartExecutionCommand.ExecuteAsync(null);
            await firstItemStarted.Task.WaitAsync(SyncTimeout);

            vm.Initialize(second, CreateFiles("C:\\media\\b1.mkv"));
            await vm.StartExecutionCommand.ExecuteAsync(null);

            // Assert: чужой финиш с ошибкой не трогает общий индикатор.
            _taskbarMock.Verify(t => t.SetError(It.IsAny<double>()), Times.Never,
                "ошибка чужой сессии не красит индикатор активной сессии");
            _taskbarMock.Verify(t => t.Clear(), Times.Never,
                "индикатор не сбрасывается, пока первая сессия продолжает работу");

            // Cleanup: первая сессия успешно завершается и гасит индикатор.
            releaseFirst.TrySetResult(true);
            await firstTask.WaitAsync(SyncTimeout);

            _taskbarMock.Verify(t => t.SetError(It.IsAny<double>()), Times.Never,
                "итог успешной сессии не показывает красное состояние чужой ошибки");
            _taskbarMock.Verify(t => t.Clear(), Times.Once,
                "индикатор сбрасывается после финиша последней активной сессии");
        }
        finally
        {
            releaseFirst.TrySetResult(true);
        }
    }
}
