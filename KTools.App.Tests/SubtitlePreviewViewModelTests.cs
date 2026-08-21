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

    [TestMethod]
    public async Task SubtitlePreviewViewModel_FileFilteringAndAllFiles_SwitchesCorrectly()
    {
        // Arrange
        string file1 = Path.Combine(Path.GetTempPath(), "test_preview_f1.ass");
        string file2 = Path.Combine(Path.GetTempPath(), "test_preview_f2.ass");
        string file3 = Path.Combine(Path.GetTempPath(), "test_preview_f3.ass");

        File.WriteAllText(file1, "[Events]\nFormat: Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0:00:01.00,0:00:02.00,Default,,0,0,0,,File1Line1\nDialogue: 0:00:02.00,0:00:03.00,Default,,0,0,0,,File1Line2");
        File.WriteAllText(file2, "[Events]\nFormat: Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0:00:01.00,0:00:02.00,Default,,0,0,0,,File2Line1\nDialogue: 0:00:02.00,0:00:03.00,Default,,0,0,0,,File2Line2");
        File.WriteAllText(file3, "[Events]\nFormat: Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0:00:01.00,0:00:02.00,Default,,0,0,0,,File3Line1");

        var assData1 = new AssData();
        assData1.Dialogues.Add(new AssDialogue("0:00:01.00", "0:00:02.00", "Default", "", "", "File1Line1"));
        assData1.Dialogues.Add(new AssDialogue("0:00:02.00", "0:00:03.00", "Default", "", "", "File1Line2"));

        var assData2 = new AssData();
        assData2.Dialogues.Add(new AssDialogue("0:00:01.00", "0:00:02.00", "Default", "", "", "File2Line1"));
        assData2.Dialogues.Add(new AssDialogue("0:00:02.00", "0:00:03.00", "Default", "", "", "File2Line2"));

        var assData3 = new AssData();
        assData3.Dialogues.Add(new AssDialogue("0:00:01.00", "0:00:02.00", "Default", "", "", "File3Line1"));

        _assParserMock.Setup(p => p.Parse(file1)).Returns(assData1);
        _assParserMock.Setup(p => p.Parse(file2)).Returns(assData2);
        _assParserMock.Setup(p => p.Parse(file3)).Returns(assData3);
        _assParserMock.Setup(p => p.StripTags(It.IsAny<string>())).Returns<string>(s => s);

        var viewModel = new SubtitlePreviewViewModel(
            _filterState,
            "test_group",
            _assParserMock.Object,
            _settingsManagerMock.Object,
            _logServiceMock.Object
        );

        try
        {
            await viewModel.LoadDataAsync(new[] { file1, file2, file3 });

            // Initially all 3 files (5 lines) are in FilteredLines
            viewModel.FilteredLines.Should().HaveCount(5);

            // Select File 2
            viewModel.SelectedFilePath = file2;
            viewModel.FilteredLines.Should().HaveCount(2);
            viewModel.FilteredLines.All(l => l.FilePath == file2).Should().BeTrue();
            viewModel.FilteredLines[0].IsFirstLineInFile.Should().BeTrue();
            viewModel.FilteredLines[1].IsFirstLineInFile.Should().BeFalse();

            // Select File 3
            viewModel.SelectedFilePath = file3;
            viewModel.FilteredLines.Should().HaveCount(1);
            viewModel.FilteredLines[0].FilePath.Should().Be(file3);
            viewModel.FilteredLines[0].IsFirstLineInFile.Should().BeTrue();

            // Switch back to "All files"
            viewModel.SelectedFilePath = null;
            viewModel.FilteredLines.Should().HaveCount(5);
            viewModel.FilteredLines.Select(l => l.FileName).Distinct().Should().HaveCount(3);
        }
        finally
        {
            if (File.Exists(file1)) File.Delete(file1);
            if (File.Exists(file2)) File.Delete(file2);
            if (File.Exists(file3)) File.Delete(file3);
        }
    }
}
