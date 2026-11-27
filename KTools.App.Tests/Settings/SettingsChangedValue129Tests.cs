// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Services.Contracts;
using KTools_App.Tests.TestHelpers;

namespace KTools_App.Tests.Settings;

/// <summary>
/// Контрактные проверки issue #129: событие settings.changed обязано содержать
/// понятные Key/Value и человекочитаемое сообщение по LOGGING.md п.7,
/// а ValueHash сохраняется как дополнение.
/// </summary>
[TestClass]
public class SettingsChangedValue129Tests
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
        _tempSettingsDir = Path.Combine(Path.GetTempPath(), "KTools_Tests_129_" + Guid.NewGuid().ToString("N"));
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
    /// Проверяет, что одиночное сохранение фиксирует Key, открытое Value и ValueHash.
    /// </summary>
    [TestMethod]
    public void LogChangedPersisted_SingleSetting_ContainsValueAndValueHash()
    {
        // Arrange
        var manager = CreateManager();
        _logService.Clear();

        // Act
        PersistenceResult result = manager.SetSetting("General", "Theme", "Light");

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Persisted.Should().BeTrue("первая запись выполняется синхронно без дебаунса");

        IReadOnlyList<RecordedLogEvent> changed = _logService.EventsById(SettingsEventIds.Changed);
        changed.Should().HaveCount(1);
        changed[0].GetProperty<string>("Key").Should().Be("Theme");
        changed[0].GetProperty<string>("Value").Should().Be("Light");
        string? valueHash = changed[0].GetProperty<string>("ValueHash");
        valueHash.Should().NotBeNull();
        valueHash.Should().StartWith(PersistenceValueHash.Prefix);
        changed[0].Message.Should().Contain("Theme");
        changed[0].Message.Should().Contain("Light");
    }

    private SettingsManager CreateManager()
    {
        var manager = new SettingsManager(_logService, _pathManagerMock.Object);
        _managers.Add(manager);
        return manager;
    }
}
