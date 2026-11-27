// -*- coding: utf-8 -*-
using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using KTools_App.Core;
using KTools_App.Services.Contracts;
using KTools_App.Tests.TestHelpers;

namespace KTools_App.Tests.Settings;

/// <summary>
/// Проверки ишью 142: конструктор SettingsManager не выполняет побочек
/// (диск, журнал, разрешение путей), а путь к файлу настроек вычисляется
/// при каждом обращении, а не фиксируется в конструкторе.
/// </summary>
[TestClass]
public class SettingsCtorSideEffectsIssue142Tests
{
    /// <summary>
    /// Проверяет, что конструктор не обращается к путям, диску и журналу:
    /// только сторожевые проверки аргументов.
    /// </summary>
    [TestMethod]
    public void Constructor_WithoutInitialization_DoesNotTouchPathsDiskOrLogger()
    {
        // Arrange: каталог осознанно не создаём — конструктор не должен его трогать.
        string dir = Path.Combine(Path.GetTempPath(), "KTools_142_" + Guid.NewGuid().ToString("N"));
        var pathManagerMock = new Mock<IPathManager>();
        pathManagerMock.Setup(pm => pm.GetSettingsDirectory()).Returns(dir);
        var log = new RecordingLogService();
        SettingsManager? manager = null;

        try
        {
            // Act
            manager = new SettingsManager(log, pathManagerMock.Object);

            // Assert
            pathManagerMock.Verify(pm => pm.GetSettingsDirectory(), Times.Never,
                "конструктор не должен разрешать каталог настроек");
            log.Events.Should().BeEmpty("конструктор не должен писать в журнал");
            log.InitializeLogFileCalls.Should().BeEmpty("конструктор не должен перенастраивать журнал");
            Directory.Exists(dir).Should().BeFalse("конструктор не должен создавать каталоги");
            File.Exists(Path.Combine(dir, "settings.json")).Should().BeFalse();
        }
        finally
        {
            manager?.Dispose();
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    /// <summary>
    /// Проверяет, что смена базового каталога после создания менеджера меняет
    /// место записи: сохранение уходит в новый каталог, а не в зафиксированный.
    /// </summary>
    [TestMethod]
    public void SaveSettings_BaseDirChangedAfterCreation_WritesToNewDirectory()
    {
        // Arrange
        using var firstScope = new TempDirectoryScope();
        using var secondScope = new TempDirectoryScope();
        string currentDir = firstScope.RootPath;
        var pathManagerMock = new Mock<IPathManager>();
        pathManagerMock.Setup(pm => pm.GetSettingsDirectory()).Returns(() => currentDir);
        var log = new RecordingLogService();
        var manager = new SettingsManager(log, pathManagerMock.Object);

        try
        {
            manager.SetSetting("General", "Theme", "Dark");
            manager.SaveSettings().IsSuccess.Should().BeTrue();

            // Act: базовый каталог сменился после создания менеджера.
            currentDir = secondScope.RootPath;
            manager.SetSetting("General", "Theme", "Light");
            manager.SaveSettings().IsSuccess.Should().BeTrue();

            // Assert
            string newFile = Path.Combine(secondScope.RootPath, "settings.json");
            File.Exists(newFile).Should().BeTrue("запись обязана уходить в актуальный каталог");
            File.ReadAllText(newFile).Should().Contain("Light");
            File.ReadAllText(Path.Combine(firstScope.RootPath, "settings.json"))
                .Should().NotContain("Light", "старый каталог перезаписываться не должен");
        }
        finally
        {
            manager.Dispose();
        }
    }

    /// <summary>
    /// Проверяет сторожевые проверки конструктора: нулевые зависимости отклоняются сразу.
    /// </summary>
    [TestMethod]
    public void Constructor_NullDependencies_ThrowsArgumentNullException()
    {
        // Act
        Action nullLog = () => new SettingsManager(null!, Mock.Of<IPathManager>());
        Action nullPaths = () => new SettingsManager(new RecordingLogService(), null!);

        // Assert
        nullLog.Should().Throw<ArgumentNullException>();
        nullPaths.Should().Throw<ArgumentNullException>();
    }

    /// <summary>
    /// Проверяет явную инициализацию: читает файл из актуального каталога
    /// и настраивает каталог журнала до первого бизнес-события.
    /// </summary>
    [TestMethod]
    public void InitializeAsync_AfterMigration_LoadsFileAndConfiguresLogDirectory()
    {
        // Arrange
        using var scope = new TempDirectoryScope();
        scope.CreateFile(
            "settings.json",
            "{ \"General\": { \"Theme\": \"Light\" }, \"Logging\": { \"LogDir\": \"C:\\\\Логи\" } }");
        var pathManagerMock = new Mock<IPathManager>();
        pathManagerMock.Setup(pm => pm.GetSettingsDirectory()).Returns(scope.RootPath);
        var log = new RecordingLogService();
        var manager = new SettingsManager(log, pathManagerMock.Object);

        try
        {
            // Act
            manager.InitializeAsync().GetAwaiter().GetResult();

            // Assert
            manager.GetSetting("General", "Theme", "Dark").Should().Be("Light");
            log.InitializeLogFileCalls.Should().ContainSingle()
                .Which.Should().Be("C:\\Логи");
            int firstInitialize = log.CallOrder
                .ToList()
                .FindIndex(entry => entry.StartsWith("init:", StringComparison.Ordinal));
            int firstEvent = log.CallOrder
                .ToList()
                .FindIndex(entry => entry.StartsWith("event:", StringComparison.Ordinal));
            firstInitialize.Should().BeGreaterThanOrEqualTo(0);
            firstEvent.Should().BeGreaterThan(firstInitialize);
        }
        finally
        {
            manager.Dispose();
        }
    }

    /// <summary>
    /// Проверяет, что повторная инициализация безопасна и не сбрасывает кэш.
    /// </summary>
    [TestMethod]
    public void InitializeAsync_CalledTwice_DoesNotReloadOrReconfigure()
    {
        // Arrange
        using var scope = new TempDirectoryScope();
        scope.CreateFile("settings.json", "{ \"General\": { \"Theme\": \"Light\" } }");
        var pathManagerMock = new Mock<IPathManager>();
        pathManagerMock.Setup(pm => pm.GetSettingsDirectory()).Returns(scope.RootPath);
        var log = new RecordingLogService();
        var manager = new SettingsManager(log, pathManagerMock.Object);

        try
        {
            manager.InitializeAsync().GetAwaiter().GetResult();
            File.WriteAllText(
                Path.Combine(scope.RootPath, "settings.json"),
                "{ \"General\": { \"Theme\": \"Changed\" } }");

            // Act
            manager.InitializeAsync().GetAwaiter().GetResult();

            // Assert
            manager.GetSetting("General", "Theme", "Dark").Should().Be("Light");
            log.InitializeLogFileCalls.Should().HaveCount(1);
        }
        finally
        {
            manager.Dispose();
        }
    }
}
