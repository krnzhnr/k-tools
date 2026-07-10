// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using Moq;
using KTools_App.ViewModels;
using KTools_App.Core;
using KTools_App.Services.Contracts;

namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты для WorkPanelViewModel.
/// Все комментарии и тестовые описания написаны на русском языке.
/// </summary>
[TestClass]
public class WorkPanelViewModelTests
{
    private Mock<INavigationService> _navigationServiceMock = null!;
    private Mock<IDialogService> _dialogServiceMock = null!;
    private Mock<ISettingsManager> _settingsManagerMock = null!;
    private Mock<ILogService> _logServiceMock = null!;
    private Mock<IDependencyManager> _dependencyManagerMock = null!;
    private Mock<IMediaProbeService> _mediaProbeServiceMock = null!;

    [TestInitialize]
    public void Setup()
    {
        _navigationServiceMock = new Mock<INavigationService>();
        _dialogServiceMock = new Mock<IDialogService>();
        _settingsManagerMock = new Mock<ISettingsManager>();
        _logServiceMock = new Mock<ILogService>();
        _dependencyManagerMock = new Mock<IDependencyManager>();
        _mediaProbeServiceMock = new Mock<IMediaProbeService>();

        _settingsManagerMock.Setup(m => m.GetSetting(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .Returns<string, string, bool>((g, k, d) => d);
    }

    /// <summary>
    /// Проверяет начальное состояние модели представления рабочей панели.
    /// </summary>
    [TestMethod]
    public void WorkPanelViewModel_InitialState_IsCorrect()
    {
        // Act
        var viewModel = new WorkPanelViewModel(
            _navigationServiceMock.Object,
            _dialogServiceMock.Object,
            _settingsManagerMock.Object,
            _logServiceMock.Object,
            _dependencyManagerMock.Object,
            _mediaProbeServiceMock.Object
        );

        // Assert
        viewModel.IsProcessing.Should().BeFalse();
        viewModel.GlobalProgressValue.Should().Be(0.0);
        viewModel.Files.Should().BeEmpty();
        viewModel.IsStartButtonEnabled.Should().BeTrue();
    }
}
