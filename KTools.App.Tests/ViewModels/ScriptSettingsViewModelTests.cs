// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using FluentAssertions;
using Moq;
using KTools_App.Core;
using KTools_App.Services.Contracts;
using KTools_App.Tests.TestHelpers;
using KTools_App.ViewModels;

namespace KTools_App.Tests.ViewModels;

/// <summary>
/// Юнит-тесты ScriptSettingsViewModel — модели динамических настроек скрипта.
/// Проверяют инициализацию по скрипту (SettingsGroup из GetSafeGroupName),
/// чтение и сохранение настроек с делегированием ISettingsManager,
/// поведение при неинициализированной группе и null-защиту конструктора.
/// Все комментарии выполнены на русском языке.
/// </summary>
[TestClass]
public class ScriptSettingsViewModelTests
{
    private Mock<ISettingsManager> _settingsManagerMock = null!;
    private Mock<ILogService> _logServiceMock = null!;
    private ScriptSettingsViewModel _vm = null!;

    [TestInitialize]
    public void Setup()
    {
        _settingsManagerMock = MockBuilders.CreateSettingsManagerMock();
        _logServiceMock = MockBuilders.CreateLogServiceMock();
        _vm = new ScriptSettingsViewModel(_settingsManagerMock.Object, _logServiceMock.Object);
    }

    /// <summary>
    /// Создаёт StubScript с моками зависимостей.
    /// </summary>
    private static StubScript CreateScript()
    {
        return new StubScript(
            MockBuilders.CreateLogServiceMock().Object,
            MockBuilders.CreateSettingsManagerMock().Object,
            MockBuilders.CreatePathManagerMock().Object);
    }

    /// <summary>
    /// Проверяет, что конструктор с null бросает ArgumentNullException.
    /// </summary>
    [TestMethod]
    public void Constructor_NullArguments_ThrowArgumentNullException()
    {
        // Act
        Action settingsNull = () => new ScriptSettingsViewModel(null!, _logServiceMock.Object);
        Action logNull = () => new ScriptSettingsViewModel(_settingsManagerMock.Object, null!);

        // Assert
        settingsNull.Should().Throw<ArgumentNullException>();
        logNull.Should().Throw<ArgumentNullException>();
    }

    /// <summary>
    /// Проверяет начальное состояние до инициализации скрипта.
    /// </summary>
    [TestMethod]
    public void ViewModel_BeforeInitialize_HasEmptyGroupAndNoScript()
    {
        // Assert
        _vm.ActiveScript.Should().BeNull();
        _vm.SettingsGroup.Should().BeEmpty();
    }

    /// <summary>
    /// Проверяет, что InitializeScript устанавливает активный скрипт
    /// и группу настроек через GetSafeGroupName.
    /// </summary>
    [TestMethod]
    public void InitializeScript_ValidScript_SetsScriptAndSettingsGroup()
    {
        // Arrange
        var script = CreateScript();
        _settingsManagerMock
            .Setup(m => m.GetSafeGroupName(StubScript.DefaultName))
            .Returns("Безопасное_Имя");

        // Act
        _vm.InitializeScript(script);

        // Assert
        _vm.ActiveScript.Should().BeSameAs(script);
        _vm.SettingsGroup.Should().Be("Безопасное_Имя");
        _settingsManagerMock.Verify(m => m.GetSafeGroupName(StubScript.DefaultName), Times.Once);
    }

    /// <summary>
    /// Проверяет, что GetSetting делегирует менеджеру настроек с группой скрипта.
    /// </summary>
    [TestMethod]
    public void GetSetting_InitializedScript_DelegatesWithSettingsGroup()
    {
        // Arrange
        var script = CreateScript();
        _vm.InitializeScript(script);
        _settingsManagerMock
            .Setup(m => m.GetSetting(It.IsAny<string>(), "quality", 0))
            .Returns(75);

        // Act
        int result = _vm.GetSetting("quality", 0);

        // Assert
        result.Should().Be(75);
        _settingsManagerMock.Verify(
            m => m.GetSetting(StubScript.DefaultName, "quality", 0),
            Times.Once,
            "группа должна передаваться по имени скрипта (identity-маппинг мока)");
    }

    /// <summary>
    /// Проверяет, что GetSetting без инициализации возвращает значение по умолчанию.
    /// </summary>
    [TestMethod]
    public void GetSetting_NotInitialized_ReturnsDefaultValue()
    {
        // Act
        string result = _vm.GetSetting("some_key", "default_value");

        // Assert
        result.Should().Be("default_value", "без группы настройки должно возвращаться значение по умолчанию");
        _settingsManagerMock.Verify(
            m => m.GetSetting(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never,
            "менеджер настроек не должен вызываться без инициализации");
    }

    /// <summary>
    /// Проверяет, что SaveSetting сохраняет значение через менеджер и пишет Info-лог.
    /// </summary>
    [TestMethod]
    public void SaveSetting_InitializedScript_PersistsAndLogs()
    {
        // Arrange
        _vm.InitializeScript(CreateScript());

        // Act
        _vm.SaveSetting("crf", "23");

        // Assert
        _settingsManagerMock.Verify(
            m => m.SetSetting(StubScript.DefaultName, "crf", "23"),
            Times.Once);
        _logServiceMock.Verify(
            l => l.Info(It.Is<string>(s => s.Contains("crf") && s.Contains("23")), It.IsAny<string>()),
            Times.Once);
    }

    /// <summary>
    /// Проверяет, что SaveSetting без инициализации не вызывает менеджер настроек.
    /// </summary>
    [TestMethod]
    public void SaveSetting_NotInitialized_DoesNotPersist()
    {
        // Act
        _vm.SaveSetting("crf", "23");

        // Assert
        _settingsManagerMock.Verify(
            m => m.SetSetting(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>()),
            Times.Never);
        _logServiceMock.Verify(l => l.Info(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// Проверяет сборку значений по умолчанию из схемы скрипта:
    /// GetFullSettingsSchema всегда содержит поля переименования.
    /// </summary>
    [TestMethod]
    public void InitializeScript_StubSchema_FullSchemaContainsRenameFields()
    {
        // Arrange
        var script = CreateScript();

        // Act
        var schema = script.GetFullSettingsSchema();

        // Assert — базовая схема StubScript пуста, но полная схема содержит вкладку "Переименование"
        schema.Should().NotBeNull();
        schema.Should().Contain(f => f.Key == "LocalRenameOverride");
        schema.Should().Contain(f => f.Key == "LocalRenameUseRegex");
        schema.Should().Contain(f => f.Key == "LocalRenameSearch");
        schema.Should().Contain(f => f.Key == "LocalRenameReplace");
        schema.All(f => f.Group == "Переименование").Should().BeTrue();
    }

    /// <summary>
    /// Проверяет значения по умолчанию локальных полей переименования из полной схемы.
    /// </summary>
    [TestMethod]
    public void GetFullSettingsSchema_RenameFields_HaveCorrectDefaults()
    {
        // Arrange
        var script = CreateScript();

        // Act
        var schema = script.GetFullSettingsSchema();
        var overrideField = schema.First(f => f.Key == "LocalRenameOverride");
        var useRegexField = schema.First(f => f.Key == "LocalRenameUseRegex");
        var caseField = schema.First(f => f.Key == "LocalRenameCaseSensitive");
        var searchField = schema.First(f => f.Key == "LocalRenameSearch");

        // Assert
        ((bool)overrideField.DefaultValue).Should().BeFalse();
        ((bool)useRegexField.DefaultValue).Should().BeTrue();
        ((bool)caseField.DefaultValue).Should().BeFalse();
        searchField.DefaultValue.Should().Be(string.Empty);
        searchField.PlaceholderText.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// Проверяет, что повторная инициализация другим скриптом переключает группу.
    /// </summary>
    [TestMethod]
    public void InitializeScript_SecondScript_SwitchesSettingsGroup()
    {
        // Arrange
        var first = CreateScript();
        var second = CreateScript();
        _settingsManagerMock
            .Setup(m => m.GetSafeGroupName(It.IsAny<string>()))
            .Returns<string>(name => $"group_{name}");

        // Act
        _vm.InitializeScript(first);
        string firstGroup = _vm.SettingsGroup;
        _vm.InitializeScript(second);
        string secondGroup = _vm.SettingsGroup;

        // Assert
        firstGroup.Should().Be($"group_{StubScript.DefaultName}");
        secondGroup.Should().Be(firstGroup, "StubScript возвращает одно имя — группа идентична");
        _vm.ActiveScript.Should().BeSameAs(second);
    }

    /// <summary>
    /// Проверяет кастомные значения настроек: сохранение и чтение через менеджера.
    /// </summary>
    [TestMethod]
    public void GetSetting_AfterSave_RetrievesCustomValueFromManager()
    {
        // Arrange
        _vm.InitializeScript(CreateScript());
        var saved = new Dictionary<string, object>();
        _settingsManagerMock
            .Setup(m => m.SetSetting(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>()))
            .Callback<string, string, object>((g, k, v) => saved[k] = v);
        _settingsManagerMock
            .Setup(m => m.GetSetting(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
            .Returns<string, string, int>((g, k, d) => saved.TryGetValue(k, out var v) && v is int i ? i : d);

        // Act
        _vm.SaveSetting("threads", 8);
        int loaded = _vm.GetSetting("threads", 0);

        // Assert
        loaded.Should().Be(8, "кастомное значение должно читаться после сохранения");
    }
}
