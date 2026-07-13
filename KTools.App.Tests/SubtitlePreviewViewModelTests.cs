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

    /// <summary>
    /// Проверяет, что фильтрация по регулярным выражениям применяется к строкам предпросмотра при сохранении паттернов.
    /// </summary>
    [TestMethod]
    public void SubtitlePreviewViewModel_SavePatterns_AppliesFiltersToLines()
    {
        // Arrange
        string tempSourceFile = Path.Combine(Path.GetTempPath(), "test_preview_regex.ass");
        var assData = new AssData();
        assData.Dialogues.Add(new AssDialogue("0:00:01.00", "0:00:03.00", "Style1", "Actor1", "", "[вздох] Привет"));
        assData.Dialogues.Add(new AssDialogue("0:00:04.00", "0:00:06.00", "Style1", "Actor1", "", "Удалить строку целиком"));
        
        File.WriteAllText(tempSourceFile, "[Script Info]\nTitle: Test\n[Events]\nFormat: Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0:00:01.00,0:00:03.00,Style1,Actor1,,0,0,,[вздох] Привет\nDialogue: 0:00:04.00,0:00:06.00,Style1,Actor1,,0,0,,Удалить строку целиком");

        _assParserMock.Setup(p => p.Parse(tempSourceFile)).Returns(assData);
        _assParserMock.Setup(p => p.StripTags(It.IsAny<string>())).Returns<string>(s => s.Replace("[вздох]", "").Trim());

        var viewModel = new SubtitlePreviewViewModel(
            _filterState,
            "test_group",
            _assParserMock.Object,
            _settingsManagerMock.Object,
            _logServiceMock.Object
        );

        try
        {
            // Загружаем данные
            var task = viewModel.LoadDataAsync(new[] { tempSourceFile });
            task.Wait();

            viewModel.SubtitleLines.Should().HaveCount(2);

            // Act
            var patterns = new List<Dictionary<string, object>>
            {
                new() { { "word", "\\[[^\\]]+\\]" }, { "active", true }, { "only_part", true } },
                new() { { "word", "Удалить строку" }, { "active", true }, { "only_part", false } }
            };
            viewModel.LoadPatterns(patterns);

            // Assert
            // Первая строка должна быть изменена (без скобок)
            viewModel.SubtitleLines[0].FinalText.Should().Be(" Привет");
            viewModel.SubtitleLines[0].IsChecked.Should().BeTrue();
            viewModel.SubtitleLines[0].Status.Should().Be("ОК");

            // Вторая строка должна быть удалена по regex
            viewModel.SubtitleLines[1].IsChecked.Should().BeFalse();
            viewModel.SubtitleLines[1].Status.Should().Be("Удалено (Regex)");
        }
        finally
        {
            if (File.Exists(tempSourceFile)) File.Delete(tempSourceFile);
        }
    }

    [TestMethod]
    public void PatternItemViewModel_UpdateSampleText_GeneratesCorrectSamples()
    {
            // Case 1: Empty word
            var item1 = new PatternItemViewModel("", true, true);
            item1.SampleText.Should().BeEmpty();

            // Case 2: Simple regex matching numbers
            var item2 = new PatternItemViewModel(@"\d{3}-\d{2}", true, true);
            item2.SampleText.Should().StartWith("Пример совпадения: ");
            string value2 = item2.SampleText.Replace("Пример совпадения: \"", "").TrimEnd('"');
            System.Text.RegularExpressions.Regex.IsMatch(value2, @"^\d{3}-\d{2}$").Should().BeTrue();

            // Case 3: Invalid regex
            var item3 = new PatternItemViewModel(@"[0-9", true, true);
            item3.SampleText.Should().Be("Некорректный regex");

            // Case 4: Complex regex not fully supported by Fare
            var item4 = new PatternItemViewModel(@"(?<=foo)bar", true, true);
            item4.SampleText.Should().Be("Сложный regex (пример недоступен)");
        }
    }
