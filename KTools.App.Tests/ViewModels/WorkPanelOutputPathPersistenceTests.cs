// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using Moq;
using CommunityToolkit.Mvvm.Messaging;
using KTools_App.Core;
using KTools_App.Services.Contracts;
using KTools_App.Tests.TestHelpers;
using KTools_App.ViewModels;

namespace KTools_App.Tests.ViewModels;

/// <summary>
/// Юнит-тесты глобального запоминаемого выходного пути (ишью #42, глобальный скоуп:
/// один запоминаемый путь по умолчанию для всех скриптов, индивидуально по скриптам — позже).
/// Хранение — напрямую через ISettingsManager (группа/ключ
/// <see cref="WorkPanelViewModel.DefaultOutputPathSettingsGroup"/> /
/// <see cref="WorkPanelViewModel.DefaultOutputPathSettingsKey"/>),
/// без типизированных свойств (файлы настроек заняты другим агентом).
/// Все комментарии выполнены на русском языке.
/// </summary>
[TestClass]
public class WorkPanelOutputPathPersistenceTests : IsolatedMessengerTestBase
{
    private static string SettingsKey =>
        WorkPanelViewModel.DefaultOutputPathSettingsGroup + "/" + WorkPanelViewModel.DefaultOutputPathSettingsKey;

    /// <summary>
    /// Создаёт мок настроек со словарным хранилищем строковых значений.
    /// Несохранённые ключи возвращают переданное значение по умолчанию (как настоящий менеджер).
    /// </summary>
    private static Mock<ISettingsManager> CreateStoreSettingsMock(Dictionary<string, object?> store)
    {
        var mock = new Mock<ISettingsManager>(MockBehavior.Loose);
        mock.Setup(m => m.GetSetting(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns<string, string, string>((group, key, @default) =>
                store.TryGetValue(group + "/" + key, out object? raw) ? raw as string ?? @default : @default);
        mock.Setup(m => m.GetSetting(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .Returns<string, string, bool>((group, key, @default) =>
                store.TryGetValue(group + "/" + key, out object? raw) && raw is bool flag ? flag : @default);
        mock.Setup(m => m.SetSetting(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns<string, string, string>((group, key, value) =>
            {
                store[group + "/" + key] = value;
                return PersistenceResult.Succeeded(new[] { group + "/" + key });
            });
        mock.SetupProperty(m => m.UseAutoSubfolder, false);
        mock.SetupProperty(m => m.DefaultOutputSubfolder, string.Empty);
        return mock;
    }

    private static WorkPanelViewModel CreateViewModel(ISettingsManager settings, ILogService log)
    {
        var vm = new WorkPanelViewModel(
            MockBuilders.CreateNavigationMock().Object,
            MockBuilders.CreateDialogMock().Object,
            settings,
            log,
            MockBuilders.CreateDependencyManagerMock(allInstalled: true).Object,
            MockBuilders.CreateMediaProbeMock().Object);
        MessengerIsolation.Track(vm);
        return vm;
    }

    private static StubScript CreateScript(ISettingsManager settings, ILogService log)
    {
        return new StubScript(log, settings, MockBuilders.CreatePathManagerMock().Object);
    }

    /// <summary>
    /// Проверяет, что смена выходного пути сохраняется в настройки.
    /// </summary>
    [TestMethod]
    public void OutputPath_ExistingFolderSet_PersistsToSettings()
    {
        // Arrange — существующая папка и пустое хранилище
        using var dir = new TempDirectoryScope();
        var store = new Dictionary<string, object?>(StringComparer.Ordinal);
        var settingsMock = CreateStoreSettingsMock(store);
        var log = MockBuilders.CreateLogServiceMock().Object;
        var vm = CreateViewModel(settingsMock.Object, log);
        var script = CreateScript(settingsMock.Object, log);
        vm.Initialize(script, new ObservableCollection<FileQueueItem>());

        // Act — смена пути (единая точка для Обзор/перетаскивание/ручной ввод)
        vm.OutputPath = dir.RootPath;

        // Assert — значение сохранено в настройки и синхронизировано с памятью скрипта
        store.Should().ContainKey(SettingsKey, "смена выходного пути обязана сохраняться в настройки");
        store[SettingsKey].Should().Be(dir.RootPath);
        script.SavedOutputPath.Should().Be(dir.RootPath, "память скрипта синхронизируется с глобальным путём");
    }

    /// <summary>
    /// Проверяет, что сохранённый путь восстанавливается для другого скрипта (глобальность).
    /// </summary>
    [TestMethod]
    public void RestoreState_PersistedExistingFolder_RestoresItForAnotherScript()
    {
        // Arrange — путь заранее сохранён в настройки
        using var dir = new TempDirectoryScope();
        var store = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [SettingsKey] = dir.RootPath
        };
        var settingsMock = CreateStoreSettingsMock(store);
        var log = MockBuilders.CreateLogServiceMock().Object;

        // Act — активация другого скрипта с тем же хранилищем настроек
        var vm = CreateViewModel(settingsMock.Object, log);
        vm.Initialize(CreateScript(settingsMock.Object, log), new ObservableCollection<FileQueueItem>());

        // Assert — путь общий для всех скриптов
        vm.OutputPath.Should().Be(dir.RootPath, "глобальный путь восстанавливается независимо от скрипта");
    }

    /// <summary>
    /// Проверяет, что несохранённой папки (удалена, сеть недоступна) путь откатывается к дефолту.
    /// </summary>
    [TestMethod]
    public void RestoreState_PersistedFolderDeleted_FallsBackToDefault()
    {
        // Arrange — в настройках лежит путь удалённой папки
        using var dir = new TempDirectoryScope();
        string doomed = Path.Combine(dir.RootPath, "Удалённая");
        Directory.CreateDirectory(doomed);
        var store = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [SettingsKey] = doomed
        };
        Directory.Delete(doomed);
        var settingsMock = CreateStoreSettingsMock(store);
        var log = MockBuilders.CreateLogServiceMock().Object;
        var vm = CreateViewModel(settingsMock.Object, log);

        // Act
        vm.Initialize(CreateScript(settingsMock.Object, log), new ObservableCollection<FileQueueItem>());

        // Assert — битый путь не подставляется, молча дефолт
        vm.OutputPath.Should().BeEmpty("несуществующий путь не подставляется, откат к папке исходников");
        store[SettingsKey].Should().Be(doomed, "временная недоступность папки не стирает сохранённое значение");
    }

    /// <summary>
    /// Проверяет, что очистка пути пользователем затирает сохранённое значение и не воскрешается.
    /// </summary>
    [TestMethod]
    public void OutputPath_ClearedByUser_ClearsPersistedValue()
    {
        // Arrange — путь сохранён и восстановлен
        using var dir = new TempDirectoryScope();
        var store = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [SettingsKey] = dir.RootPath
        };
        var settingsMock = CreateStoreSettingsMock(store);
        var log = MockBuilders.CreateLogServiceMock().Object;
        var vm = CreateViewModel(settingsMock.Object, log);
        vm.Initialize(CreateScript(settingsMock.Object, log), new ObservableCollection<FileQueueItem>());
        vm.OutputPath.Should().Be(dir.RootPath, "предусловие: путь восстановлен из настроек");

        // Act — пользователь очистил поле
        vm.OutputPath = string.Empty;

        // Assert — сохранённое затёрто
        store[SettingsKey].Should().Be(string.Empty, "очистка пути затирает сохранённое значение");

        // Act — активация другого скрипта с «протухшей» памятью
        var staleScript = CreateScript(settingsMock.Object, log);
        staleScript.SavedOutputPath = dir.RootPath;
        var vm2 = CreateViewModel(settingsMock.Object, log);
        vm2.Initialize(staleScript, new ObservableCollection<FileQueueItem>());

        // Assert — очищенный путь не воскрешается из памяти скрипта
        vm2.OutputPath.Should().BeEmpty("очищенный путь не воскрешается при следующем заходе");
    }

    /// <summary>
    /// Проверяет, что без записи в настройках (первый запуск) действует прежнее поведение в памяти.
    /// </summary>
    [TestMethod]
    public void RestoreState_NoPersistedValue_KeepsInMemorySavedPath()
    {
        // Arrange — в настройках записи нет, в памяти скрипта путь есть
        var store = new Dictionary<string, object?>(StringComparer.Ordinal);
        var settingsMock = CreateStoreSettingsMock(store);
        var log = MockBuilders.CreateLogServiceMock().Object;
        var script = CreateScript(settingsMock.Object, log);
        script.SavedOutputPath = "C:\\output\\results";
        var vm = CreateViewModel(settingsMock.Object, log);

        // Act
        vm.Initialize(script, new ObservableCollection<FileQueueItem>());

        // Assert — поведение в памяти сохранено для обратной совместимости
        vm.OutputPath.Should().Be("C:\\output\\results", "без записи в настройках действует прежнее поведение в памяти");
    }

    /// <summary>
    /// Запоминание выключено: смена пути не пишется в настройки, но живёт в памяти скрипта.
    /// </summary>
    [TestMethod]
    public void OutputPath_WhenRememberingDisabled_DoesNotPersist()
    {
        // Arrange — тогл «Запоминать выходные пути» выключен
        using var dir = new TempDirectoryScope();
        var store = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [WorkPanelViewModel.DefaultOutputPathSettingsGroup + "/" + WorkPanelViewModel.RememberOutputPathsSettingsKey] = false
        };
        var settingsMock = CreateStoreSettingsMock(store);
        var log = MockBuilders.CreateLogServiceMock().Object;
        var vm = CreateViewModel(settingsMock.Object, log);
        var script = CreateScript(settingsMock.Object, log);
        vm.Initialize(script, new ObservableCollection<FileQueueItem>());

        // Act
        vm.OutputPath = dir.RootPath;

        // Assert — в настройки не пишем, внутрисессионная память работает
        store.Should().NotContainKey(SettingsKey, "при выключенном тогле путь не сохраняется");
        script.SavedOutputPath.Should().Be(dir.RootPath, "внутрисессионная память скрипта остаётся");
    }

    /// <summary>
    /// Запоминание выключено: сохранённый ранее путь не подставляется при активации скрипта.
    /// </summary>
    [TestMethod]
    public void RestoreState_WhenRememberingDisabled_IgnoresPersistedPath()
    {
        // Arrange — в настройках есть путь, но тогл выключен
        using var dir = new TempDirectoryScope();
        var store = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [SettingsKey] = dir.RootPath,
            [WorkPanelViewModel.DefaultOutputPathSettingsGroup + "/" + WorkPanelViewModel.RememberOutputPathsSettingsKey] = false
        };
        var settingsMock = CreateStoreSettingsMock(store);
        var log = MockBuilders.CreateLogServiceMock().Object;
        var vm = CreateViewModel(settingsMock.Object, log);

        // Act
        vm.Initialize(CreateScript(settingsMock.Object, log), new ObservableCollection<FileQueueItem>());

        // Assert — чистый дефолт, сохранённое не тронуто
        vm.OutputPath.Should().BeEmpty("при выключенном тогле путь из настроек не восстанавливается");
        store[SettingsKey].Should().Be(dir.RootPath, "выключение тогла не стирает ранее сохранённый путь");
    }

    /// <summary>
    /// Запоминание выключено: даже внутрисессионная память скрипта не подставляется.
    /// </summary>
    [TestMethod]
    public void RestoreState_WhenRememberingDisabled_IgnoresInMemoryPath()
    {
        // Arrange — в памяти скрипта остался путь от прежней сессии
        var store = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [WorkPanelViewModel.DefaultOutputPathSettingsGroup + "/" + WorkPanelViewModel.RememberOutputPathsSettingsKey] = false
        };
        var settingsMock = CreateStoreSettingsMock(store);
        var log = MockBuilders.CreateLogServiceMock().Object;
        var script = CreateScript(settingsMock.Object, log);
        script.SavedOutputPath = "C:\\output\\results";
        var vm = CreateViewModel(settingsMock.Object, log);

        // Act
        vm.Initialize(script, new ObservableCollection<FileQueueItem>());

        // Assert — никакого запомненного пути, каждый файл пойдёт в свою папку
        vm.OutputPath.Should().BeEmpty("при выключенном запоминании путь не подставляется вообще");
        script.SavedOutputPath.Should().BeEmpty("память скрипта очищается, иначе путь всплывёт позже");
    }

    /// <summary>
    /// Выключение тогла в настройках сразу сбрасывает текущий путь в открытой панели.
    /// </summary>
    [TestMethod]
    public void RememberingDisabledMessage_ClearsCurrentOutputPath()
    {
        // Arrange — запоминание включено, путь установлен
        using var dir = new TempDirectoryScope();
        var store = new Dictionary<string, object?>(StringComparer.Ordinal);
        var settingsMock = CreateStoreSettingsMock(store);
        var log = MockBuilders.CreateLogServiceMock().Object;
        var vm = CreateViewModel(settingsMock.Object, log);
        vm.Initialize(CreateScript(settingsMock.Object, log), new ObservableCollection<FileQueueItem>());
        vm.OutputPath = dir.RootPath;
        vm.OutputPath.Should().Be(dir.RootPath, "предусловие: путь выбран");

        // Act — настройка уже записана, затем приходит уведомление
        store[WorkPanelViewModel.DefaultOutputPathSettingsGroup + "/" + WorkPanelViewModel.RememberOutputPathsSettingsKey] = false;
        CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default
            .Send(new OutputPathRememberingChangedMessage(false));

        // Assert
        vm.OutputPath.Should().BeEmpty("выключение запоминания сразу возвращает пустое поле");
    }
}
