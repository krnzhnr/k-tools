// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using Moq;
using KTools_App.ViewModels;
using KTools_App.Core;
using KTools_App.Services.Contracts;

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

    [TestInitialize]
    public void Setup()
    {
        _settingsManagerMock = new Mock<ISettingsManager>();
        _dialogServiceMock = new Mock<IDialogService>();
        _updateServiceMock = new Mock<IUpdateService>();
        _logServiceMock = new Mock<ILogService>();
        _pathManagerMock = new Mock<IPathManager>();
        _scriptRegistryMock = new Mock<IScriptRegistry>();

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
            _scriptRegistryMock.Object
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
    /// Проверяет, что изменение свойств во ViewModel вызывает сохранение в SettingsManager.
    /// </summary>
    [TestMethod]
    public void OverwriteExisting_PropertyChange_SavesToSettingsManager()
    {
        // Arrange
        var viewModel = new SettingsViewModel(
            _settingsManagerMock.Object,
            _dialogServiceMock.Object,
            _updateServiceMock.Object,
            _logServiceMock.Object,
            _pathManagerMock.Object,
            _scriptRegistryMock.Object
        );

        // Act
        viewModel.OverwriteExisting = true;

        // Assert
        _settingsManagerMock.VerifySet(m => m.OverwriteExisting = true, Times.Once);
    }

    /// <summary>
    /// Проверяет изменение темы и смену индекса темы.
    /// </summary>
    [TestMethod]
    public void SelectedThemeIndex_PropertyChange_UpdatesSettingsManager()
    {
        // Arrange
        var viewModel = new SettingsViewModel(
            _settingsManagerMock.Object,
            _dialogServiceMock.Object,
            _updateServiceMock.Object,
            _logServiceMock.Object,
            _pathManagerMock.Object,
            _scriptRegistryMock.Object
        );

        // Act
        viewModel.SelectedThemeIndex = 2; // Light

        // Assert
        _settingsManagerMock.VerifySet(m => m.Theme = "Light", Times.Once);
    }
}
