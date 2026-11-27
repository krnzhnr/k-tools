// -*- coding: utf-8 -*-
using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using Moq;
using KTools_App.Core;
using KTools_App.Services.Contracts;
using KTools_App.Tests.TestHelpers;

namespace KTools_App.Tests.Settings;

/// <summary>
/// Тесты коллизии имён секций настроек (issue 135):
/// имена «A/B» и «A_B» нормализовались в одну секцию и затирали значения друг друга.
/// </summary>
[TestClass]
public class GetSafeGroupNameCollisionIssue135Tests
{
    private Mock<IPathManager> _pathManagerMock = null!;
    private RecordingLogService _logService = null!;
    private string _tempSettingsDir = null!;
    private readonly List<SettingsManager> _managers = new();

    [TestInitialize]
    public void Setup()
    {
        _logService = new RecordingLogService();
        _pathManagerMock = new Mock<IPathManager>();

        // Уникальная временная папка: тесты не влияют на реальные файлы.
        _tempSettingsDir = Path.Combine(Path.GetTempPath(), "KTools_Tests_135_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempSettingsDir);

        _pathManagerMock.Setup(pm => pm.GetSettingsDirectory()).Returns(_tempSettingsDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (SettingsManager manager in _managers)
        {
            try
            {
                manager.Dispose();
            }
            catch (Exception)
            {
                // Тест уже проверяет освобождение отдельно.
            }
        }

        _managers.Clear();

        if (Directory.Exists(_tempSettingsDir))
        {
            Directory.Delete(_tempSettingsDir, true);
        }
    }

    /// <summary>
    /// Проверяет, что коллидирующие имена получают разные секции.
    /// </summary>
    [TestMethod]
    public void GetSafeGroupName_CollidingScriptNames_ReturnsDistinctGroups()
    {
        // Arrange
        var manager = CreateManager();

        // Act
        string slashGroup = manager.GetSafeGroupName("A/B");
        string underscoreGroup = manager.GetSafeGroupName("A_B");

        // Assert
        slashGroup.Should().NotBe(underscoreGroup, "секции коллидирующих имён не должны совпадать");
        slashGroup.Should().StartWith("Script_");
        underscoreGroup.Should().Be("Script_A_B", "имя без опасных символов сохраняет прежнюю секцию");
    }

    /// <summary>
    /// Проверяет, что суффикс при коллизии детерминирован: повторные вызовы
    /// и новые экземпляры менеджера дают ту же секцию.
    /// </summary>
    [TestMethod]
    public void GetSafeGroupName_CollidingScriptNames_IsDeterministic()
    {
        // Arrange
        var manager = CreateManager();

        // Act
        string first = manager.GetSafeGroupName("A/B");
        string second = manager.GetSafeGroupName("A/B");
        string afterRecreate = CreateManager().GetSafeGroupName("A/B");

        // Assert
        second.Should().Be(first);
        afterRecreate.Should().Be(first, "суффикс обязан быть стабильным между запусками");
    }

    /// <summary>
    /// Проверяет сквозную изоляцию: значения коллидирующих скриптов
    /// не затирают друг друга в кэше настроек.
    /// </summary>
    [TestMethod]
    public void GetSafeGroupName_CollidingScriptNames_DoesNotShareStoredValues()
    {
        // Arrange
        var manager = CreateManager();
        string slashGroup = manager.GetSafeGroupName("A/B");
        string underscoreGroup = manager.GetSafeGroupName("A_B");

        // Act
        manager.SetSetting(slashGroup, "mode", "slash-value").IsFailure.Should().BeFalse();
        manager.SetSetting(underscoreGroup, "mode", "underscore-value").IsFailure.Should().BeFalse();

        // Assert
        manager.GetSetting(slashGroup, "mode", string.Empty).Should().Be("slash-value");
        manager.GetSetting(underscoreGroup, "mode", string.Empty).Should().Be("underscore-value");
    }

    /// <summary>
    /// Проверяет, что обычные имена без «/» и «\» сохраняют прежнее отображение
    /// (совместимость существующих секций settings.json).
    /// </summary>
    [TestMethod]
    public void GetSafeGroupName_OrdinaryNames_KeepsLegacyMapping()
    {
        // Arrange
        var manager = CreateManager();

        // Act
        string spaced = manager.GetSafeGroupName("Test Script → Speed");
        string russian = manager.GetSafeGroupName("Кодирование аудио");

        // Assert
        spaced.Should().Be("Script_Test_Script___Speed");
        russian.Should().Be("Script_Кодирование_аудио");
    }

    private SettingsManager CreateManager()
    {
        var manager = new SettingsManager(_logService, _pathManagerMock.Object);
        _managers.Add(manager);
        return manager;
    }
}
