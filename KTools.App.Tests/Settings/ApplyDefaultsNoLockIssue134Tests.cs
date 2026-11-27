// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using FluentAssertions;
using Moq;
using KTools_App.Core;
using KTools_App.Services.Contracts;
using KTools_App.Tests.TestHelpers;

namespace KTools_App.Tests.Settings;

/// <summary>
/// Тесты полноты значений по умолчанию первого запуска (issue 134):
/// файл, записанный InitializeDefaults на пустом кэше, обязан содержать
/// все типизированные ключи, иначе persisted:true является ложным.
/// </summary>
[TestClass]
public class ApplyDefaultsNoLockIssue134Tests
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
        _tempSettingsDir = Path.Combine(Path.GetTempPath(), "KTools_ApplyDefaults134_" + Guid.NewGuid().ToString("N"));
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
            }
        }

        _managers.Clear();

        if (Directory.Exists(_tempSettingsDir))
        {
            Directory.Delete(_tempSettingsDir, true);
        }
    }

    /// <summary>
    /// Проверяет, что инициализация на пустом кэше записывает на диск
    /// все типизированные значения по умолчанию.
    /// </summary>
    [TestMethod]
    public void InitializeDefaults_EmptyCache_PersistsAllTypedDefaults()
    {
        // Arrange — каталог без файла настроек: первый запуск приложения.
        var manager = CreateManager();

        // Act
        PersistenceResult result = manager.InitializeDefaults(StubSettingsDefaultsProvider.Empty);

        // Assert — запись подтверждена, иначе persisted:true был бы ложью.
        result.IsSuccess.Should().BeTrue();
        result.Persisted.Should().BeTrue("файл первого запуска обязан содержать все значения по умолчанию");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(_tempSettingsFile));
        JsonElement root = document.RootElement;

        root.GetProperty("General").GetProperty("RememberOutputPaths").GetBoolean().Should().BeTrue();
        root.GetProperty("General").GetProperty("SearchTemplates").GetArrayLength()
            .Should().Be(SettingsDefaults.GetDefaultSearchTemplates().Count);
        root.GetProperty("General").GetProperty("ReplaceTemplates").GetArrayLength()
            .Should().Be(SettingsDefaults.GetDefaultReplaceTemplates().Count);
        root.GetProperty("Debug").GetProperty("DebugDisableUpdateAction").GetBoolean().Should().BeFalse();

        // Типизированные свойства согласуются с записанным файлом.
        manager.RememberOutputPaths.Should().BeTrue();
        manager.SearchTemplates.Should().HaveCount(SettingsDefaults.GetDefaultSearchTemplates().Count);
        manager.ReplaceTemplates.Should().HaveCount(SettingsDefaults.GetDefaultReplaceTemplates().Count);
        manager.DebugDisableUpdateAction.Should().BeFalse();
    }

    private SettingsManager CreateManager()
    {
        var manager = new SettingsManager(_logService, _pathManagerMock.Object);
        _managers.Add(manager);
        return manager;
    }
}
