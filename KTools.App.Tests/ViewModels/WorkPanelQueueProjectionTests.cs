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
/// Проверяет устранение дублей очередь/прогресс/таскбар VM vs code-behind (ишью #121):
/// чтение настроек очереди живёт только во ViewModel (ReadCurrentSettings),
/// а StatusText/GlobalProgressValue являются чистой проекцией сохранённого
/// состояния активного скрипта (ProjectExecutionState).
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class WorkPanelQueueProjectionTests : IsolatedMessengerTestBase
{
    private Mock<INavigationService> _navigationMock = null!;
    private Mock<IDialogService> _dialogMock = null!;
    private Mock<ISettingsManager> _settingsMock = null!;
    private Mock<ILogService> _logMock = null!;
    private Mock<IDependencyManager> _dependencyMock = null!;
    private Mock<IMediaProbeService> _probeMock = null!;

    [TestInitialize]
    public void Setup()
    {
        _navigationMock = MockBuilders.CreateNavigationMock();
        _dialogMock = MockBuilders.CreateDialogMock();
        _settingsMock = MockBuilders.CreateSettingsManagerMock();
        _logMock = MockBuilders.CreateLogServiceMock();
        _dependencyMock = MockBuilders.CreateDependencyManagerMock(allInstalled: true);
        _probeMock = MockBuilders.CreateMediaProbeMock();
    }

    private WorkPanelViewModel CreateViewModel()
    {
        var vm = new WorkPanelViewModel(
            _navigationMock.Object,
            _dialogMock.Object,
            _settingsMock.Object,
            _logMock.Object,
            _dependencyMock.Object,
            _probeMock.Object);
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

    /// <summary>
    /// Настраивает сквозной возврат значений по умолчанию для GetSetting с типом object,
    /// чтобы ReadCurrentSettings получал дефолты схемы, а не null от несогласованного мока.
    /// </summary>
    private void SetupObjectSettingDefaults()
    {
        _settingsMock.Setup(m => m.GetSetting(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<object>()))
            .Returns<string, string, object>((group, key, defaultValue) => defaultValue);
    }

    [TestMethod]
    public void ReadCurrentSettings_InitializedScript_MergesSavedGroupAndSchemaDefaults()
    {
        // Arrange
        SetupObjectSettingDefaults();
        _settingsMock.Setup(m => m.GetAllSettingsInGroup(It.IsAny<string>()))
            .Returns(new Dictionary<string, object>
            {
                ["LocalRenameSearch"] = "сохранённое значение"
            });
        var script = CreateScript();
        var vm = CreateViewModel();
        vm.Initialize(script, CreateFiles("C:\\media\\one.mkv"));

        // Act
        Dictionary<string, object> settings = vm.ReadCurrentSettings();

        // Assert
        settings["LocalRenameSearch"].Should().Be("сохранённое значение",
            "сохранённое значение группы не должно затираться дефолтом схемы");
        settings.Should().ContainKey("LocalRenameOverride",
            "дефолты схемы скрипта дополняют словарь настроек");
        settings["LocalRenameOverride"].Should().Be(false);
    }

    [TestMethod]
    public void ReadCurrentSettings_NoActiveScript_ReturnsEmpty()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        Dictionary<string, object> settings = vm.ReadCurrentSettings();

        // Assert
        settings.Should().BeEmpty("без активного скрипта читать настройки не из чего");
    }

    [TestMethod]
    public async Task ReadCurrentSettings_Result_FlowsIntoExecutionHandler()
    {
        // Arrange
        SetupObjectSettingDefaults();
        _settingsMock.Setup(m => m.GetAllSettingsInGroup(It.IsAny<string>()))
            .Returns(new Dictionary<string, object>
            {
                ["LocalRenameSearch"] = "сохранённое значение"
            });
        Dictionary<string, object>? captured = null;
        var script = CreateScript((file, settings, context) =>
        {
            captured = settings;
            return Task.FromResult(ExecutionResult.Succeeded(context, "Готово"));
        });
        var vm = CreateViewModel();
        vm.Initialize(script, CreateFiles("C:\\media\\one.mkv"));

        // Act
        Dictionary<string, object> settings = vm.ReadCurrentSettings();
        await vm.StartExecutionCommand.ExecuteAsync(settings);

        // Assert
        captured.Should().NotBeNull("настройки ViewModel передаются в обработчик скрипта");
        captured!["LocalRenameSearch"].Should().Be("сохранённое значение");
    }

    [TestMethod]
    public async Task StartExecutionAsync_SuccessfulQueue_ProjectsSavedStateToViewModel()
    {
        // Arrange
        var script = CreateScript();
        var vm = CreateViewModel();
        vm.Initialize(script, CreateFiles("C:\\media\\one.mkv"));

        // Act
        await vm.StartExecutionCommand.ExecuteAsync(null);

        // Assert
        vm.StatusText.Should().Be(script.SavedStatusText,
            "ViewModel проецирует сохранённый статус скрипта, а не ведёт собственную копию");
        vm.GlobalProgressValue.Should().Be(script.SavedGlobalProgress,
            "ViewModel проецирует сохранённый прогресс скрипта, а не ведёт собственную копию");
    }

    [TestMethod]
    public async Task StartExecutionAsync_EmptyQueue_ProjectsSavedStateToViewModel()
    {
        // Arrange
        var script = CreateScript();
        var vm = CreateViewModel();
        vm.Initialize(script, CreateFiles());

        // Act
        await vm.StartExecutionCommand.ExecuteAsync(null);

        // Assert
        vm.StatusText.Should().Be(script.SavedStatusText);
        vm.GlobalProgressValue.Should().Be(script.SavedGlobalProgress);
        script.SavedStatusText.Should().Be("Очередь пуста");
        script.SavedGlobalProgress.Should().Be(0);
    }

    [TestMethod]
    public async Task StartExecutionAsync_StartFailure_ProjectsSavedStateToViewModel()
    {
        // Arrange
        var script = CreateScript();
        script.ProcessableFilesFilter = _ => throw new InvalidOperationException("Сбой подготовки очереди");
        var vm = CreateViewModel();
        vm.Initialize(script, CreateFiles("C:\\media\\a.mkv", "C:\\media\\b.mkv"));

        // Act
        await vm.StartExecutionCommand.ExecuteAsync(null);

        // Assert
        vm.StatusText.Should().Be(script.SavedStatusText,
            "даже при сбое запуска UI показывает состояние скрипта без второй копии правды");
        vm.GlobalProgressValue.Should().Be(script.SavedGlobalProgress);
    }
}
