// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using KTools_App.Core;
using KTools_App.Services.Contracts;
using KTools_App.Tests.TestHelpers;

namespace KTools_App.Tests.Settings;

/// <summary>
/// Регрессия issue #133: строковые значения из JSON ("false", "23") обязаны
/// разбираться в bool/int, а не глотаться с молчаливым возвратом значения по умолчанию.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class GetSettingStringJsonIssue133Tests
{
    private Mock<IPathManager> _pathManagerMock = null!;
    private RecordingLogService _logService = null!;
    private string _tempSettingsDir = null!;
    private string _tempSettingsFile = null!;
    private readonly List<SettingsManager> _managers = new();

    [TestInitialize]
    public void Setup()
    {
        _logService = new RecordingLogService();
        _pathManagerMock = new Mock<IPathManager>();

        // Уникальная временная папка: тест не влияет на реальные файлы настроек
        _tempSettingsDir = Path.Combine(Path.GetTempPath(), "KTools_Tests_133_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempSettingsDir);
        _tempSettingsFile = Path.Combine(_tempSettingsDir, "settings.json");

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
                // Освобождение проверяется отдельными тестами.
            }
        }

        _managers.Clear();

        if (Directory.Exists(_tempSettingsDir))
        {
            Directory.Delete(_tempSettingsDir, true);
        }
    }

    [TestMethod]
    public void GetSetting_StringFalseJson_WithTrueDefault_ReturnsFalse()
    {
        // Arrange — строковый "false" в файле при дефолте true
        File.WriteAllText(_tempSettingsFile, "{ \"General\": { \"EnableParallel\": \"false\" } }");
        var manager = CreateManager();

        // Act
        bool actual = manager.GetSetting("General", "EnableParallel", true);

        // Assert — инверсия без сигнала недопустима
        actual.Should().BeFalse("строковый false обязан разбираться, а не глотаться в дефолт true");
    }

    [TestMethod]
    public void GetSetting_StringNumberJson_WithDefault_ReturnsParsedInt()
    {
        // Arrange — строковое число в файле при дефолте 0
        File.WriteAllText(_tempSettingsFile, "{ \"General\": { \"QueueBadgeMode\": \"3\" } }");
        var manager = CreateManager();

        // Act
        int actual = manager.GetSetting("General", "QueueBadgeMode", 0);

        // Assert
        actual.Should().Be(3, "строковое число обязано разбираться, а не глотаться в дефолт");
    }

    private SettingsManager CreateManager()
    {
        var manager = new SettingsManager(_logService, _pathManagerMock.Object);
        manager.InitializeAsync().GetAwaiter().GetResult();
        _managers.Add(manager);
        return manager;
    }
}
