// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
/// Проверяет дублирование прогресса очереди в иконку панели задач (ишью 50):
/// зелёный прогресс во время обработки, красное состояние при ошибках,
/// сброс индикатора при успехе, отмене и пустой очереди.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class WorkPanelTaskbarProgressTests : IsolatedMessengerTestBase
{
    private Mock<INavigationService> _navigationMock = null!;
    private Mock<IDialogService> _dialogMock = null!;
    private Mock<ISettingsManager> _settingsMock = null!;
    private Mock<ILogService> _logMock = null!;
    private Mock<IDependencyManager> _dependencyMock = null!;
    private Mock<IMediaProbeService> _probeMock = null!;
    private Mock<ITaskbarProgressService> _taskbarMock = null!;

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
        Func<string, Dictionary<string, object>, ExecutionContext, Task<ExecutionResult>>? handler = null)
    {
        var script = new StubScript(
            _logMock.Object,
            _settingsMock.Object,
            MockBuilders.CreatePathManagerMock().Object);
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

    [TestMethod]
    public async Task StartExecutionAsync_SuccessfulQueue_ReportsProgressAndClearsTaskbar()
    {
        // Arrange
        var script = CreateScript();
        var vm = CreateViewModel();
        vm.Initialize(script, CreateFiles("C:\\media\\one.mkv", "C:\\media\\two.mp4"));

        // Act
        await vm.StartExecutionCommand.ExecuteAsync(null);

        // Assert
        _taskbarMock.Verify(t => t.SetProgress(It.IsAny<double>()), Times.AtLeastOnce,
            "общий прогресс очереди дублируется в иконку панели задач");
        _taskbarMock.Verify(t => t.Clear(), Times.AtLeastOnce,
            "успешное завершение сбрасывает индикатор панели задач");
        _taskbarMock.Verify(t => t.SetError(It.IsAny<double>()), Times.Never,
            "при успехе красное состояние не показывается");
    }

    [TestMethod]
    public async Task StartExecutionAsync_FailedQueue_SetsTaskbarError()
    {
        // Arrange
        var script = CreateScript((file, settings, context) =>
            Task.FromResult(ExecutionResult.Failed(
                context,
                "Ошибка декодирования",
                errorCode: "decode-failed",
                cleanupState: CleanupState.Completed)));
        var vm = CreateViewModel();
        vm.Initialize(script, CreateFiles("C:\\media\\bad.mkv"));

        // Act
        await vm.StartExecutionCommand.ExecuteAsync(null);

        // Assert
        _taskbarMock.Verify(t => t.SetError(It.IsAny<double>()), Times.Once,
            "очередь с ошибками показывает красное состояние в иконке");
        _taskbarMock.Verify(t => t.Clear(), Times.Never,
            "при ошибке индикатор не сбрасывается в обычное состояние");
    }

    [TestMethod]
    public async Task StartExecutionAsync_EmptyQueue_ClearsTaskbar()
    {
        // Arrange
        var script = CreateScript();
        var vm = CreateViewModel();
        vm.Initialize(script, CreateFiles());

        // Act
        await vm.StartExecutionCommand.ExecuteAsync(null);

        // Assert
        _taskbarMock.Verify(t => t.Clear(), Times.Once,
            "пустая очередь сбрасывает индикатор панели задач");
        _taskbarMock.Verify(t => t.SetError(It.IsAny<double>()), Times.Never,
            "пустая очередь не является ошибкой задачи");
    }

    [TestMethod]
    public async Task StartExecutionAsync_StartFailure_SetsTaskbarError()
    {
        // Arrange
        var script = CreateScript();
        script.ProcessableFilesFilter = _ => throw new InvalidOperationException("Сбой подготовки очереди");
        var vm = CreateViewModel();
        vm.Initialize(script, CreateFiles("C:\\media\\a.mkv", "C:\\media\\b.mkv"));

        // Act
        await vm.StartExecutionCommand.ExecuteAsync(null);

        // Assert
        _taskbarMock.Verify(t => t.SetError(It.IsAny<double>()), Times.Once,
            "сбой запуска очереди показывает красное состояние в иконке");
        _taskbarMock.Verify(t => t.Clear(), Times.Never,
            "сброс не должен затирать красное состояние сбоя запуска");
    }

    [TestMethod]
    public async Task StartExecutionAsync_AllSkipped_ClearsTaskbarWithoutError()
    {
        // Arrange
        var script = CreateScript((file, settings, context) =>
            Task.FromResult(ExecutionResult.Skipped(context, "Элемент пропущен")));
        var vm = CreateViewModel();
        vm.Initialize(script, CreateFiles("C:\\media\\one.mkv"));

        // Act
        await vm.StartExecutionCommand.ExecuteAsync(null);

        // Assert
        _taskbarMock.Verify(t => t.SetError(It.IsAny<double>()), Times.Never,
            "пропуск без отказов не является ошибкой задачи");
        _taskbarMock.Verify(t => t.Clear(), Times.AtLeastOnce,
            "очередь только из пропущенных сбрасывает индикатор панели задач");
    }
}
