// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Moq;
using KTools_App.Core;
using KTools_App.Services.Contracts;
using KTools_App.Tests.TestHelpers;
using KTools_App.UI.Pages;
using KTools_App.ViewModels;

namespace KTools_App.Tests.ViewModels;

/// <summary>
/// Юнит-тесты HomeViewModel — модели представления домашней страницы.
/// Проверяют динамическую группировку скриптов реестра по категориям,
/// наличие встроенного инструмента "Калькулятор сдвига" и команду
/// перехода к скрипту (по ScriptInfo и по строковому имени).
/// Все комментарии выполнены на русском языке.
/// </summary>
[TestClass]
public class HomeViewModelTests
{
    private Mock<INavigationService> _navigationMock = null!;
    private Mock<IScriptRegistry> _scriptRegistryMock = null!;

    [TestInitialize]
    public void Setup()
    {
        _navigationMock = MockBuilders.CreateNavigationMock();
        _scriptRegistryMock = MockBuilders.CreateScriptRegistryMock();
    }

    /// <summary>
    /// Проверяет, что пустой реестр дает пустые списки категорий,
    /// но ToolScripts всегда содержит калькулятор.
    /// </summary>
    [TestMethod]
    public void Constructor_EmptyRegistry_AllCategoriesEmptyExceptTools()
    {
        // Act
        var vm = new HomeViewModel(_navigationMock.Object, _scriptRegistryMock.Object);

        // Assert
        vm.VideoScripts.Should().BeEmpty();
        vm.AudioScripts.Should().BeEmpty();
        vm.ContainerScripts.Should().BeEmpty();
        vm.SubtitleScripts.Should().BeEmpty();
        vm.NetworkScripts.Should().BeEmpty();
        vm.ToolScripts.Should().HaveCount(1, "калькулятор добавляется всегда");
        vm.ToolScripts[0].Name.Should().Be("Калькулятор сдвига");
    }

    /// <summary>
    /// Проверяет распределение скриптов по категориям согласно Category.
    /// </summary>
    [TestMethod]
    public void Constructor_MultipleCategories_ScriptsGroupedCorrectly()
    {
        // Arrange
        var logMock = MockBuilders.CreateLogServiceMock();
        var settingsMock = MockBuilders.CreateSettingsManagerMock();
        var pathMock = MockBuilders.CreatePathManagerMock();
        var video = new NamedStubScript(AppConstants.ScriptCategory.Video, "Видеоскрипт");
        var audio = new NamedStubScript(AppConstants.ScriptCategory.Audio, "Аудиоскрипт");
        var container = new NamedStubScript(AppConstants.ScriptCategory.Containers, "Контейнерный скрипт");
        var subtitles = new NamedStubScript(AppConstants.ScriptCategory.Subtitles, "Субтитровый скрипт");
        var network = new NamedStubScript(AppConstants.ScriptCategory.Network, "Сетевой скрипт");
        _scriptRegistryMock = MockBuilders.CreateScriptRegistryMock(video, audio, container, subtitles, network);

        // Act
        var vm = new HomeViewModel(_navigationMock.Object, _scriptRegistryMock.Object);

        // Assert
        vm.VideoScripts.Should().ContainSingle(s => s.Name == "Видеоскрипт");
        vm.AudioScripts.Should().ContainSingle(s => s.Name == "Аудиоскрипт");
        vm.ContainerScripts.Should().ContainSingle(s => s.Name == "Контейнерный скрипт");
        vm.SubtitleScripts.Should().ContainSingle(s => s.Name == "Субтитровый скрипт");
        vm.NetworkScripts.Should().ContainSingle(s => s.Name == "Сетевой скрипт");
    }

    /// <summary>
    /// Проверяет, что ScriptInfo в списках переносит метаданные скрипта.
    /// </summary>
    [TestMethod]
    public void Constructor_ScriptMetadata_MappedToScriptInfo()
    {
        // Arrange
        var video = new NamedStubScript(AppConstants.ScriptCategory.Video, "Видеоскрипт");
        _scriptRegistryMock = MockBuilders.CreateScriptRegistryMock(video);

        // Act
        var vm = new HomeViewModel(_navigationMock.Object, _scriptRegistryMock.Object);

        // Assert
        var info = vm.VideoScripts.Single();
        info.Name.Should().Be("Видеоскрипт");
        info.Category.Should().Be(AppConstants.ScriptCategory.Video);
        info.IconName.Should().Be("TestIcon");
        info.Description.Should().Be("Тестовый скрипт для юнит-тестов");
        info.IsAvailable.Should().BeTrue();
    }

    /// <summary>
    /// Проверяет, что скрипты категории "Инструменты" добавляются после калькулятора.
    /// </summary>
    [TestMethod]
    public void Constructor_ToolCategoryScript_AppendedAfterCalculator()
    {
        // Arrange
        var tool = new NamedStubScript(AppConstants.ScriptCategory.Tools, "Мой инструмент");
        _scriptRegistryMock = MockBuilders.CreateScriptRegistryMock(tool);

        // Act
        var vm = new HomeViewModel(_navigationMock.Object, _scriptRegistryMock.Object);

        // Assert
        vm.ToolScripts.Should().HaveCount(2);
        vm.ToolScripts[0].Name.Should().Be("Калькулятор сдвига", "калькулятор всегда первый");
        vm.ToolScripts[1].Name.Should().Be("Мой инструмент");
    }

    /// <summary>
    /// Проверяет NavigateToScriptCommand с ScriptInfo известного скрипта.
    /// </summary>
    [TestMethod]
    public void NavigateToScriptCommand_KnownScriptInfo_NavigatesToWorkPanelWithScript()
    {
        // Arrange
        var script = new NamedStubScript(AppConstants.ScriptCategory.Video, "Видеоскрипт");
        _scriptRegistryMock = MockBuilders.CreateScriptRegistryMock(script);
        var vm = new HomeViewModel(_navigationMock.Object, _scriptRegistryMock.Object);
        var info = vm.VideoScripts.Single();

        // Act
        vm.NavigateToScriptCommand.Execute(info);

        // Assert
        _navigationMock.Verify(n => n.NavigateTo(typeof(WorkPanel), script), Times.Once);
    }

    /// <summary>
    /// Проверяет NavigateToScriptCommand со строковым именем скрипта.
    /// </summary>
    [TestMethod]
    public void NavigateToScriptCommand_ScriptNameString_NavigatesToWorkPanel()
    {
        // Arrange
        var script = new NamedStubScript(AppConstants.ScriptCategory.Video, "Видеоскрипт");
        _scriptRegistryMock = MockBuilders.CreateScriptRegistryMock(script);
        var vm = new HomeViewModel(_navigationMock.Object, _scriptRegistryMock.Object);

        // Act
        vm.NavigateToScriptCommand.Execute("Видеоскрипт");

        // Assert
        _navigationMock.Verify(n => n.NavigateTo(typeof(WorkPanel), script), Times.Once);
    }

    /// <summary>
    /// Проверяет, что ScriptInfo "Калькулятор сдвига" ведёт на TimingCalculatorPage.
    /// </summary>
    [TestMethod]
    public void NavigateToScriptCommand_CalculatorScriptInfo_NavigatesToTimingCalculatorPage()
    {
        // Arrange
        var vm = new HomeViewModel(_navigationMock.Object, _scriptRegistryMock.Object);
        var calculatorInfo = vm.ToolScripts[0];

        // Act
        vm.NavigateToScriptCommand.Execute(calculatorInfo);

        // Assert
        _navigationMock.Verify(n => n.NavigateTo(typeof(TimingCalculatorPage), It.IsAny<object?>()), Times.Once);
        _navigationMock.Verify(n => n.NavigateTo(typeof(WorkPanel), It.IsAny<object?>()), Times.Never);
    }

    /// <summary>
    /// Проверяет, что строка "Калькулятор сдвига" ведёт на TimingCalculatorPage.
    /// </summary>
    [TestMethod]
    public void NavigateToScriptCommand_CalculatorNameString_NavigatesToTimingCalculatorPage()
    {
        // Arrange
        var vm = new HomeViewModel(_navigationMock.Object, _scriptRegistryMock.Object);

        // Act
        vm.NavigateToScriptCommand.Execute("Калькулятор сдвига");

        // Assert
        _navigationMock.Verify(n => n.NavigateTo(typeof(TimingCalculatorPage), It.IsAny<object?>()), Times.Once);
    }

    /// <summary>
    /// Проверяет, что ScriptInfo неизвестного скрипта не приводит к навигации.
    /// </summary>
    [TestMethod]
    public void NavigateToScriptCommand_UnknownScriptInfo_DoesNotNavigate()
    {
        // Arrange
        var vm = new HomeViewModel(_navigationMock.Object, _scriptRegistryMock.Object);
        var unknown = new ScriptInfo { Name = "Призрачный скрипт" };

        // Act
        vm.NavigateToScriptCommand.Execute(unknown);

        // Assert
        _navigationMock.Verify(n => n.NavigateTo(It.IsAny<Type>(), It.IsAny<object?>()), Times.Never);
    }

    /// <summary>
    /// Проверяет, что null-аргумент команды не приводит к навигации и не бросает исключений.
    /// </summary>
    [TestMethod]
    public void NavigateToScriptCommand_NullArgument_DoesNotThrowOrNavigate()
    {
        // Arrange
        var vm = new HomeViewModel(_navigationMock.Object, _scriptRegistryMock.Object);

        // Act
        Action act = () => vm.NavigateToScriptCommand.Execute(null);

        // Assert
        act.Should().NotThrow();
        _navigationMock.Verify(n => n.NavigateTo(It.IsAny<Type>(), It.IsAny<object?>()), Times.Never);
    }

    /// <summary>
    /// Проверяет, что неподдерживаемый тип аргумента (число) игнорируется.
    /// </summary>
    [TestMethod]
    public void NavigateToScriptCommand_UnsupportedArgumentType_DoesNotNavigate()
    {
        // Arrange
        var vm = new HomeViewModel(_navigationMock.Object, _scriptRegistryMock.Object);

        // Act
        vm.NavigateToScriptCommand.Execute(42);

        // Assert
        _navigationMock.Verify(n => n.NavigateTo(It.IsAny<Type>(), It.IsAny<object?>()), Times.Never);
    }

    /// <summary>
    /// Стаб скрипта с настраиваемыми категорией и именем для тестов группировки.
    /// Наследует напрямую AbstractScript, так как StubScript запечатан.
    /// </summary>
    private sealed class NamedStubScript : AbstractScript
    {
        private readonly string _name;
        private readonly string _category;

        public NamedStubScript(string category, string? name = null)
            : base(
                MockBuilders.CreateLogServiceMock().Object,
                MockBuilders.CreateSettingsManagerMock().Object,
                MockBuilders.CreatePathManagerMock().Object)
        {
            _category = category;
            _name = name ?? "Тестовый скрипт";
        }

        public override string Name => _name;
        public override string Description => "Тестовый скрипт для юнит-тестов";
        public override string Category => _category;
        public override string IconName => "TestIcon";
        public override string[] FileExtensions => new[] { ".mkv", ".mp4" };

        public override Task<List<string>> ExecuteSingleAsync(
            string filePath,
            Dictionary<string, object> settings,
            string? outputPath,
            ScriptProgressCallback progressCallback,
            int fileIndex,
            int totalCount)
        {
            return Task.FromResult(new List<string> { $"✅ Готово: {filePath}" });
        }
    }
}
