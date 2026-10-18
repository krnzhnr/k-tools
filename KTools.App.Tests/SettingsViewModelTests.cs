// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using Moq;
using KTools_App.ViewModels;
using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Services.Contracts;
using KTools_App.Tests.TestHelpers;

namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты для SettingsViewModel.
/// Все комментарии и тестовые описания написаны на русском языке.
/// </summary>
[TestClass]
public class SettingsViewModelTests
{
    private Mock<ISettingsManager> _settingsManagerMock = null!;
    private Mock<IDialogService> _dialogServiceMock = null!;
    private Mock<IUpdateService> _updateServiceMock = null!;
    private Mock<ILogService> _logServiceMock = null!;
    private Mock<IPathManager> _pathManagerMock = null!;
    private Mock<IScriptRegistry> _scriptRegistryMock = null!;
    private Mock<IDependencyManager> _dependencyManagerMock = null!;

    [TestInitialize]
    public void Setup()
    {
        _settingsManagerMock = new Mock<ISettingsManager>();
        _dialogServiceMock = new Mock<IDialogService>();
        _updateServiceMock = new Mock<IUpdateService>();
        _logServiceMock = new Mock<ILogService>();
        _pathManagerMock = new Mock<IPathManager>();
        _scriptRegistryMock = new Mock<IScriptRegistry>();
        _dependencyManagerMock = new Mock<IDependencyManager>();

        // Настройка возвращаемых значений по умолчанию
        _settingsManagerMock.SetupGet(m => m.OverwriteExisting).Returns(false);
        _settingsManagerMock.SetupGet(m => m.ClearListOnAdd).Returns(false);
        _settingsManagerMock.SetupGet(m => m.EnableParallel).Returns(true);
        _settingsManagerMock.SetupGet(m => m.MaxParallelTasks).Returns(4);
        _settingsManagerMock.SetupGet(m => m.DefaultOutputSubfolder).Returns("KTools_Result");
        _settingsManagerMock.SetupGet(m => m.UseAutoSubfolder).Returns(false);
        _settingsManagerMock.SetupGet(m => m.Theme).Returns("Dark");
        _settingsManagerMock.SetupGet(m => m.BackdropType).Returns("Mica");

        _pathManagerMock.Setup(m => m.GetSettingsDirectory()).Returns("C:\\Temp");
    }

    /// <summary>
    /// Проверяет, что при инициализации значения свойств загружаются из SettingsManager.
    /// </summary>
    [TestMethod]
    public void SettingsViewModel_Initialization_LoadsSettingsCorrectly()
    {
        // Act
        var viewModel = new SettingsViewModel(
            _settingsManagerMock.Object,
            _dialogServiceMock.Object,
            _updateServiceMock.Object,
            _logServiceMock.Object,
            _pathManagerMock.Object,
            _scriptRegistryMock.Object,
            _dependencyManagerMock.Object,
            Mock.Of<IInstallLocationService>()
        );

        // Assert
        viewModel.OverwriteExisting.Should().BeFalse();
        viewModel.ClearListOnAdd.Should().BeFalse();
        viewModel.EnableParallel.Should().BeTrue();
        viewModel.MaxParallelTasks.Should().Be(4);
        viewModel.DefaultOutputSubfolder.Should().Be("KTools_Result");
        viewModel.UseAutoSubfolder.Should().BeFalse();
        viewModel.SelectedThemeIndex.Should().Be(1); // Dark = 1
        viewModel.SelectedBackdropIndex.Should().Be(0); // Mica = 0
    }

    /// <summary>
    /// Проверяет, что изменение свойств во ViewModel фиксируется типизированным
    /// вызовом SetSetting и публикует состояние Persisted без утечки значения в журнал.
    /// </summary>
    [TestMethod]
    public void OverwriteExisting_PropertyChange_SavesToSettingsManager()
    {
        // Arrange
        _settingsManagerMock
            .Setup(m => m.SetSetting("General", "OverwriteExisting", It.IsAny<bool>()))
            .Returns<string, string, bool>((group, key, value) => PersistenceResult.Succeeded(new[] { group + "/" + key }));
        var viewModel = new SettingsViewModel(
            _settingsManagerMock.Object,
            _dialogServiceMock.Object,
            _updateServiceMock.Object,
            _logServiceMock.Object,
            _pathManagerMock.Object,
            _scriptRegistryMock.Object,
            _dependencyManagerMock.Object,
            Mock.Of<IInstallLocationService>()
        );

        // Act
        viewModel.OverwriteExisting = true;

        // Assert
        _settingsManagerMock.Verify(
            m => m.SetSetting("General", "OverwriteExisting", true),
            Times.Once,
            "изменение сохраняется типизированным вызовом SetSetting, а не через свойство менеджера");
        viewModel.LastCommitState.Should().Be(SettingCommitState.Persisted);
        viewModel.LastCommitErrorCode.Should().Be(PersistenceErrorCodes.None);
        _logServiceMock.Verify(
            l => l.Write(
                It.IsAny<string>(),
                It.IsAny<LogLevel>(),
                It.IsAny<LogStatus>(),
                It.Is<string>(m => m.Contains("true", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<Exception>(),
                It.IsAny<string>(),
                It.IsAny<LogContext?>(),
                It.IsAny<IReadOnlyDictionary<string, object?>>()),
            Times.Never,
            "значение настройки не должно попадать в текст журнала");
    }

    /// <summary>
    /// Проверяет, что неподтверждённый результат сохранения не выдаётся за успех:
    /// пустой результат loose-мока приводит к типизированной ошибке SETTINGS_NO_RESULT.
    /// </summary>
    [TestMethod]
    public void OverwriteExisting_PersistenceReturnsNoResult_ReportsTypedFailure()
    {
        // Arrange
        _settingsManagerMock
            .Setup(m => m.SetSetting("General", "OverwriteExisting", It.IsAny<bool>()))
            .Returns<string, string, bool>((group, key, value) => (PersistenceResult)null!);
        var viewModel = new SettingsViewModel(
            _settingsManagerMock.Object,
            _dialogServiceMock.Object,
            _updateServiceMock.Object,
            _logServiceMock.Object,
            _pathManagerMock.Object,
            _scriptRegistryMock.Object,
            _dependencyManagerMock.Object,
            Mock.Of<IInstallLocationService>()
        );
        _logServiceMock.Invocations.Clear();

        // Act
        viewModel.OverwriteExisting = true;

        // Assert
        viewModel.LastCommitState.Should().Be(SettingCommitState.Failed,
            "отсутствие подтверждения сохранения не должно выдаваться за успех");
        viewModel.LastCommitErrorCode.Should().Be(PersistenceErrorCodes.NoResult);
        _logServiceMock.Verify(
            l => l.Write(
                "settings.persistence.deferred",
                LogLevel.Warning,
                LogStatus.Changed,
                It.IsAny<string>(),
                It.IsAny<Exception>(),
                "SettingsViewModel",
                It.IsAny<LogContext?>(),
                It.Is<IReadOnlyDictionary<string, object?>>(p => Equals(p["Persisted"], false))),
            Times.Once,
            "неподтверждённая фиксация настройки фиксируется структурированным событием без Persisted");
    }

    /// <summary>
    /// Проверяет изменение темы и смену индекса темы через типизированный SetSetting.
    /// </summary>
    [TestMethod]
    public void SelectedThemeIndex_PropertyChange_UpdatesSettingsManager()
    {
        // Arrange
        _settingsManagerMock
            .Setup(m => m.SetSetting("General", "Theme", It.IsAny<string>()))
            .Returns<string, string, string>((group, key, value) => PersistenceResult.Succeeded(new[] { group + "/" + key }));
        var viewModel = new SettingsViewModel(
            _settingsManagerMock.Object,
            _dialogServiceMock.Object,
            _updateServiceMock.Object,
            _logServiceMock.Object,
            _pathManagerMock.Object,
            _scriptRegistryMock.Object,
            _dependencyManagerMock.Object,
            Mock.Of<IInstallLocationService>()
        );

        // Act
        viewModel.SelectedThemeIndex = 2; // Light

        // Assert
        _settingsManagerMock.Verify(
            m => m.SetSetting("General", "Theme", "Light"),
            Times.Once,
            "тема сохраняется типизированным вызовом SetSetting, а не через свойство менеджера");
        viewModel.LastCommitState.Should().Be(SettingCommitState.Persisted);
        viewModel.LastCommitErrorCode.Should().Be(PersistenceErrorCodes.None);
        _logServiceMock.Verify(
            l => l.Write(
                It.IsAny<string>(),
                It.IsAny<LogLevel>(),
                It.IsAny<LogStatus>(),
                It.Is<string>(m => m.Contains("Light", StringComparison.Ordinal)),
                It.IsAny<Exception>(),
                It.IsAny<string>(),
                It.IsAny<LogContext?>(),
                It.IsAny<IReadOnlyDictionary<string, object?>>()),
            Times.Never,
            "значение темы не должно попадать в текст журнала");
    }

    /// <summary>
    /// Проверяет отображение сохранённого значения фона на индекс выбора,
    /// включая обычное значение и неизвестные значения из старых конфигураций.
    /// </summary>
    [TestMethod]
    [DataRow("Mica", 0)]
    [DataRow("Acrylic", 1)]
    [DataRow("None", 2)]
    [DataRow("Solid", 2)]
    [DataRow("acrylic", 1)]
    [DataRow("Blurple", 0)]
    public void SelectedBackdropIndex_BackdropValue_MapsToExpectedIndex(string backdrop, int expected)
    {
        // Arrange
        _settingsManagerMock.SetupGet(m => m.BackdropType).Returns(backdrop);

        // Act
        var viewModel = new SettingsViewModel(
            _settingsManagerMock.Object,
            _dialogServiceMock.Object,
            _updateServiceMock.Object,
            _logServiceMock.Object,
            _pathManagerMock.Object,
            _scriptRegistryMock.Object,
            _dependencyManagerMock.Object,
            Mock.Of<IInstallLocationService>()
        );

        // Assert
        viewModel.SelectedBackdropIndex.Should().Be(expected);
    }

    /// <summary>
    /// Проверяет, что выбор индекса фона фиксируется каноническим значением,
    /// а выход за границы списка безопасно отображается на Mica.
    /// </summary>
    [TestMethod]
    [DataRow(0, "Mica")]
    [DataRow(1, "Acrylic")]
    [DataRow(2, "None")]
    [DataRow(99, "Mica")]
    [DataRow(-1, "Mica")]
    public void SelectedBackdropIndex_IndexChange_PersistsExpectedBackdrop(int index, string expected)
    {
        // Arrange
        _settingsManagerMock
            .Setup(m => m.SetSetting("General", "BackdropType", It.IsAny<string>()))
            .Returns<string, string, string>((group, key, value) => PersistenceResult.Succeeded(new[] { group + "/" + key }));
        var viewModel = new SettingsViewModel(
            _settingsManagerMock.Object,
            _dialogServiceMock.Object,
            _updateServiceMock.Object,
            _logServiceMock.Object,
            _pathManagerMock.Object,
            _scriptRegistryMock.Object,
            _dependencyManagerMock.Object,
            Mock.Of<IInstallLocationService>()
        );
        viewModel.SelectedBackdropIndex = index == 2 ? 1 : 2;
        _settingsManagerMock.Invocations.Clear();

        // Act
        viewModel.SelectedBackdropIndex = index;

        // Assert
        _settingsManagerMock.Verify(
            m => m.SetSetting("General", "BackdropType", expected),
            Times.Once,
            "выбор фона сохраняется типизированным вызовом SetSetting каноническим значением");
        viewModel.LastCommitState.Should().Be(SettingCommitState.Persisted);
    }

    /// <summary>
    /// Строит ViewModel с управляемым сервисом папки установки для тестов обновления.
    /// </summary>
    private SettingsViewModel CreateViewModelForUpdate(
        Mock<IInstallLocationService> installLocationMock,
        Mock<IDialogService> dialogMock,
        Mock<IUpdateService> updateMock)
    {
        return new SettingsViewModel(
            _settingsManagerMock.Object,
            dialogMock.Object,
            updateMock.Object,
            _logServiceMock.Object,
            _pathManagerMock.Object,
            _scriptRegistryMock.Object,
            _dependencyManagerMock.Object,
            installLocationMock.Object);
    }

    private static StaleCopyInfo CreateStaleCopy() => new(
        @"D:\Old\KTools",
        "2.1.0",
        @"C:\Program Files\KTools",
        @"C:\Program Files\KTools\KTools.App.exe",
        "2.2.0",
        true);

    private static UpdateInfo CreateUpdate() => new(
        "2.3.0",
        "K-Tools 2.3.0",
        "Изменения",
        "https://github.com/krnzhnr/k-tools/releases/download/v2.3.0/setup.exe",
        "setup.exe",
        1024,
        false);

    /// <summary>
    /// Проверяет, что обновление из неосновной копии при согласии пользователя
    /// перенаправляет в основную копию и не запускает скачивание.
    /// </summary>
    [TestMethod]
    public async Task DownloadAndInstallUpdateAsync_StaleCopyConfirmed_RedirectsAndSkipsDownload()
    {
        // Arrange
        var staleCopy = CreateStaleCopy();
        var installLocationMock = new Mock<IInstallLocationService>();
        installLocationMock.Setup(m => m.DetectStaleCopy()).Returns(staleCopy);
        installLocationMock.Setup(m => m.TryLaunchRegisteredCopy(staleCopy)).Returns(true);
        var dialogMock = new Mock<IDialogService>();
        dialogMock.Setup(m => m.ShowConfirmationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        var updateMock = MockBuilders.CreateUpdateMock(CreateUpdate());
        var viewModel = CreateViewModelForUpdate(installLocationMock, dialogMock, updateMock);
        viewModel.NewUpdateInfo = CreateUpdate();

        // Act
        await viewModel.DownloadAndInstallUpdateAsync();

        // Assert
        installLocationMock.Verify(m => m.TryLaunchRegisteredCopy(staleCopy), Times.Once,
            "согласие обязано запустить основную копию");
        updateMock.Verify(m => m.DownloadAndInstallUpdateAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Action<double>>(), It.IsAny<System.Threading.CancellationToken>()),
            Times.Never, "скачивание из неосновной копии запускаться не должно");
    }

    /// <summary>
    /// Проверяет, что отказ от перехода продолжает обновление текущей копии.
    /// </summary>
    [TestMethod]
    public async Task DownloadAndInstallUpdateAsync_StaleCopyDeclined_ProceedsWithDownload()
    {
        // Arrange
        var staleCopy = CreateStaleCopy();
        var installLocationMock = new Mock<IInstallLocationService>();
        installLocationMock.Setup(m => m.DetectStaleCopy()).Returns(staleCopy);
        var dialogMock = new Mock<IDialogService>();
        dialogMock.Setup(m => m.ShowConfirmationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(false);
        var updateMock = MockBuilders.CreateUpdateMock(CreateUpdate());
        var viewModel = CreateViewModelForUpdate(installLocationMock, dialogMock, updateMock);
        viewModel.NewUpdateInfo = CreateUpdate();

        // Act
        await viewModel.DownloadAndInstallUpdateAsync();

        // Assert
        installLocationMock.Verify(m => m.TryLaunchRegisteredCopy(It.IsAny<StaleCopyInfo>()), Times.Never,
            "отказ от перехода не запускает основную копию");
        updateMock.Verify(m => m.DownloadAndInstallUpdateAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Action<double>>(), It.IsAny<System.Threading.CancellationToken>()),
            Times.Once, "явный выбор пользователя продолжает обновление");
    }

    /// <summary>
    /// Проверяет, что из основной копии обновление идёт сразу без лишних диалогов.
    /// </summary>
    [TestMethod]
    public async Task DownloadAndInstallUpdateAsync_MainCopy_ProceedsWithoutDialog()
    {
        // Arrange
        var installLocationMock = new Mock<IInstallLocationService>();
        installLocationMock.Setup(m => m.DetectStaleCopy()).Returns((StaleCopyInfo?)null);
        var dialogMock = new Mock<IDialogService>();
        var updateMock = MockBuilders.CreateUpdateMock(CreateUpdate());
        var viewModel = CreateViewModelForUpdate(installLocationMock, dialogMock, updateMock);
        viewModel.NewUpdateInfo = CreateUpdate();

        // Act
        await viewModel.DownloadAndInstallUpdateAsync();

        // Assert
        dialogMock.Verify(m => m.ShowConfirmationAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never, "основной копии диалог не нужен");
        updateMock.Verify(m => m.DownloadAndInstallUpdateAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Action<double>>(), It.IsAny<System.Threading.CancellationToken>()),
            Times.Once, "обновление основной копии идёт как обычно");
    }
}
