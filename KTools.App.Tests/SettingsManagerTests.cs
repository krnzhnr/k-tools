// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using Moq;
using KTools_App.Core;
using KTools_App.Services.Contracts;

namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты для менеджера настроек SettingsManager.
/// Все комментарии и тестовые описания написаны на русском языке.
/// </summary>
[TestClass]
public class SettingsManagerTests
{
    private Mock<ILogService> _logServiceMock = null!;
    private Mock<IPathManager> _pathManagerMock = null!;
    private string _tempSettingsDir = null!;
    private string _tempSettingsFile = null!;

    [TestInitialize]
    public void Setup()
    {
        _logServiceMock = new Mock<ILogService>();
        _pathManagerMock = new Mock<IPathManager>();

        // Создаем временную директорию для настроек, чтобы тесты не влияли на реальные файлы
        _tempSettingsDir = Path.Combine(Path.GetTempPath(), "KTools_Tests_" + Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempSettingsDir);
        _tempSettingsFile = Path.Combine(_tempSettingsDir, "settings.json");

        _pathManagerMock.Setup(pm => pm.GetSettingsDirectory()).Returns(_tempSettingsDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        // Удаляем временную директорию с файлом настроек
        if (Directory.Exists(_tempSettingsDir))
        {
            Directory.Delete(_tempSettingsDir, true);
        }
    }

    /// <summary>
    /// Проверяет, что при отсутствии файла настроек используются значения по умолчанию.
    /// </summary>
    [TestMethod]
    public void SettingsManager_WhenNoFileExists_UsesDefaultValues()
    {
        // Act
        var manager = new SettingsManager(_logServiceMock.Object, _pathManagerMock.Object);

        // Assert
        manager.OverwriteExisting.Should().BeFalse();
        manager.Theme.Should().Be("Dark");
        manager.DefaultOutputSubfolder.Should().Be("KTools_Result");
        manager.UseAutoSubfolder.Should().BeFalse();
        manager.BackdropType.Should().Be("Mica");
        manager.EnableParallel.Should().BeTrue();
        manager.ClearListOnAdd.Should().BeFalse();
        manager.AutoCheckUpdates.Should().BeTrue();
        manager.IncludePreReleases.Should().Be(SettingsManager.IsPreviewBuild);
        manager.DebugSimulateOldVersion.Should().BeFalse();
    }

    /// <summary>
    /// Проверяет чтение и запись логических параметров в менеджере настроек.
    /// </summary>
    [TestMethod]
    public void SetSetting_BooleanValue_SavesAndReadsCorrectly()
    {
        // Arrange
        var manager = new SettingsManager(_logServiceMock.Object, _pathManagerMock.Object);

        // Act
        manager.OverwriteExisting = true;

        // Assert
        manager.OverwriteExisting.Should().BeTrue();
        File.Exists(_tempSettingsFile).Should().BeTrue();

        // Проверяем перезагрузку с диска
        var newManager = new SettingsManager(_logServiceMock.Object, _pathManagerMock.Object);
        newManager.OverwriteExisting.Should().BeTrue();
    }

    /// <summary>
    /// Проверяет чтение и запись строковых параметров в менеджере настроек.
    /// </summary>
    [TestMethod]
    public void SetSetting_StringValue_SavesAndReadsCorrectly()
    {
        // Arrange
        var manager = new SettingsManager(_logServiceMock.Object, _pathManagerMock.Object);

        // Act
        manager.Theme = "Light";

        // Assert
        manager.Theme.Should().Be("Light");

        // Проверяем перезагрузку
        var newManager = new SettingsManager(_logServiceMock.Object, _pathManagerMock.Object);
        newManager.Theme.Should().Be("Light");
    }

    /// <summary>
    /// Проверяет чтение и запись целочисленных параметров в менеджере настроек.
    /// </summary>
    [TestMethod]
    public void SetSetting_IntValue_SavesAndReadsCorrectly()
    {
        // Arrange
        var manager = new SettingsManager(_logServiceMock.Object, _pathManagerMock.Object);

        // Act
        manager.MaxParallelTasks = 8;

        // Assert
        manager.MaxParallelTasks.Should().Be(8);

        // Проверяем перезагрузку
        var newManager = new SettingsManager(_logServiceMock.Object, _pathManagerMock.Object);
        newManager.MaxParallelTasks.Should().Be(8);
    }

    /// <summary>
    /// Проверяет чтение и запись списков сложных объектов (шаблонов) в настройках.
    /// </summary>
    [TestMethod]
    public void SetSetting_ListTemplateItem_SavesAndReadsCorrectly()
    {
        // Arrange
        var manager = new SettingsManager(_logServiceMock.Object, _pathManagerMock.Object);
        var customTemplates = new List<TemplateItem>
        {
            new() { Pattern = "test_pattern", Description = "тестовый шаблон" }
        };

        // Act
        manager.SearchTemplates = customTemplates;

        // Assert
        manager.SearchTemplates.Should().HaveCount(1);
        manager.SearchTemplates[0].Pattern.Should().Be("test_pattern");
        manager.SearchTemplates[0].Description.Should().Be("тестовый шаблон");

        // Проверяем перезагрузку
        var newManager = new SettingsManager(_logServiceMock.Object, _pathManagerMock.Object);
        newManager.SearchTemplates.Should().HaveCount(1);
        newManager.SearchTemplates[0].Pattern.Should().Be("test_pattern");
    }

    /// <summary>
    /// Проверяет, что при возникновении ошибки чтения поврежденного JSON файла, SettingsManager не падает, а инициализирует пустой кэш.
    /// </summary>
    [TestMethod]
    public void LoadSettings_CorruptedJson_FallbacksToEmptyCache()
    {
        // Arrange
        File.WriteAllText(_tempSettingsFile, "{ corrupted json: ");

        // Act
        var manager = new SettingsManager(_logServiceMock.Object, _pathManagerMock.Object);

        // Assert
        manager.OverwriteExisting.Should().BeFalse(); // Должно вернуться дефолтное значение
    }

    /// <summary>
    /// Проверяет нормализацию имени группы для секций JSON.
    /// </summary>
    [TestMethod]
    public void GetSafeGroupName_VariousCharacters_ReturnsNormalizedString()
    {
        // Arrange
        var manager = new SettingsManager(_logServiceMock.Object, _pathManagerMock.Object);

        // Act
        string result = manager.GetSafeGroupName("Test Script → Speed");

        // Assert
        result.Should().Be("Script_Test_Script___Speed");
    }
}
