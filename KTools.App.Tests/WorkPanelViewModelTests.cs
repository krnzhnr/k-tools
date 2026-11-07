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

    /// <summary>
    /// Скрипт без поддержки параллельности всегда идёт последовательно.
    /// </summary>
    [TestMethod]
    public void ResolveMaxParallelism_UnsupportedScript_ReturnsOne()
    {
        // Act
        int maxParallel = WorkPanelViewModel.ResolveMaxParallelism(
            supportsParallel: false, usesDemuxLimit: false,
            enableParallel: true, maxParallelTasks: 8,
            enableParallelDemux: true, maxParallelDemuxTasks: 4);

        // Assert
        maxParallel.Should().Be(1);
    }

    /// <summary>
    /// Разборка с включённым тумблером берёт лимит разборки, а не общий.
    /// </summary>
    [TestMethod]
    public void ResolveMaxParallelism_DemuxEnabled_ReturnsDemuxLimit()
    {
        // Act
        int maxParallel = WorkPanelViewModel.ResolveMaxParallelism(
            supportsParallel: true, usesDemuxLimit: true,
            enableParallel: true, maxParallelTasks: 8,
            enableParallelDemux: true, maxParallelDemuxTasks: 3);

        // Assert
        maxParallel.Should().Be(3);
    }

    /// <summary>
    /// Разборка с выключенным тумблером идёт последовательно, даже при включённой общей параллельности.
    /// </summary>
    [TestMethod]
    public void ResolveMaxParallelism_DemuxDisabled_ReturnsOne()
    {
        // Act
        int maxParallel = WorkPanelViewModel.ResolveMaxParallelism(
            supportsParallel: true, usesDemuxLimit: true,
            enableParallel: true, maxParallelTasks: 8,
            enableParallelDemux: false, maxParallelDemuxTasks: 3);

        // Assert
        maxParallel.Should().Be(1);
    }

    /// <summary>
    /// Обычный параллельный скрипт берёт общий лимит.
    /// </summary>
    [TestMethod]
    public void ResolveMaxParallelism_GenericEnabled_ReturnsGenericLimit()
    {
        // Act
        int maxParallel = WorkPanelViewModel.ResolveMaxParallelism(
            supportsParallel: true, usesDemuxLimit: false,
            enableParallel: true, maxParallelTasks: 6,
            enableParallelDemux: true, maxParallelDemuxTasks: 3);

        // Assert
        maxParallel.Should().Be(6);
    }

    /// <summary>
    /// Нулевые лимиты нормализуются к единице, а не к нулю.
    /// </summary>
    [TestMethod]
    public void ResolveMaxParallelism_ZeroLimits_ReturnsOne()
    {
        // Act
        int demux = WorkPanelViewModel.ResolveMaxParallelism(
            supportsParallel: true, usesDemuxLimit: true,
            enableParallel: false, maxParallelTasks: 0,
            enableParallelDemux: true, maxParallelDemuxTasks: 0);
        int generic = WorkPanelViewModel.ResolveMaxParallelism(
            supportsParallel: true, usesDemuxLimit: false,
            enableParallel: true, maxParallelTasks: 0,
            enableParallelDemux: false, maxParallelDemuxTasks: 0);

        // Assert
        demux.Should().Be(1);
        generic.Should().Be(1);
    }
}
