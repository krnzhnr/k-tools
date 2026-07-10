// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using Moq;
using KTools_App.ViewModels;
using KTools_App.Core;
using KTools_App.Services.Contracts;
using KTools_App.Infrastructure;
using KTools_App.Models;

namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты для SubtitlePreviewViewModel.
/// Все комментарии и тестовые описания написаны на русском языке.
/// </summary>
[TestClass]
public class SubtitlePreviewViewModelTests
{
    private Mock<IAssParser> _assParserMock = null!;
    private Mock<ISettingsManager> _settingsManagerMock = null!;
    private Mock<ILogService> _logServiceMock = null!;
    private SubtitleFilterState _filterState = null!;

    [TestInitialize]
    public void Setup()
    {
        _assParserMock = new Mock<IAssParser>();
        _settingsManagerMock = new Mock<ISettingsManager>();
        _logServiceMock = new Mock<ILogService>();

        _filterState = new SubtitleFilterState
        {
            StripFormatting = false,
            StripCaps = false
        };
    }

    /// <summary>
    /// Проверяет начальное состояние и привязку флагов фильтрации.
    /// </summary>
    [TestMethod]
    public void SubtitlePreviewViewModel_Initialization_BindsFlagsCorrectly()
    {
        // Act
        var viewModel = new SubtitlePreviewViewModel(
            _filterState,
            "test_group",
            _assParserMock.Object,
            _settingsManagerMock.Object,
            _logServiceMock.Object
        );

        // Assert
        viewModel.StripFormatting.Should().BeFalse();
        viewModel.StripCaps.Should().BeFalse();
        viewModel.FilteredLines.Should().BeEmpty();
    }
}
