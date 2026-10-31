// -*- coding: utf-8 -*-
using System;
using System.IO;
using System.Net.Http;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using KTools_App.Diagnostics;
using KTools_App.Services.Contracts;
using KTools_App.Services.Implementations;

namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты чистки устаревших установщиков обновлений из временной папки.
/// Все комментарии и тестовые описания написаны на русском языке.
/// </summary>
[TestClass]
public class UpdateServiceCleanupTests
{
    private Mock<ILogService> _logServiceMock = null!;
    private Mock<ISettingsManager> _settingsManagerMock = null!;
    private Mock<IHttpClientFactory> _httpClientFactoryMock = null!;
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _logServiceMock = new Mock<ILogService>();
        _settingsManagerMock = new Mock<ISettingsManager>();
        _httpClientFactoryMock = new Mock<IHttpClientFactory>();
        _httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient());

        _tempDir = Path.Combine(Path.GetTempPath(), "KTools_CleanupTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    /// <summary>
    /// Проверяет, что старые файлы собственных установщиков удаляются, а свежие и чужие остаются.
    /// </summary>
    [TestMethod]
    public void CleanupStaleSetupFiles_OldSetupFilesPresent_DeletesOnlyStaleOwnFiles()
    {
        // Arrange
        var service = CreateService();
        string oldFirst = Touch("KTools_v2.1.0_Setup.exe", hoursOld: 2);
        string oldSecond = Touch("KTools_v2.1.1_Setup.exe", hoursOld: 26);
        string fresh = Touch("KTools_v2.2.0_Setup.exe", hoursOld: 0);
        string foreign = Touch("other_setup.exe", hoursOld: 48);

        // Act
        int removed = service.CleanupStaleSetupFiles(_tempDir);

        // Assert
        removed.Should().Be(2, "удаляются только старые файлы по собственному шаблону имён");
        File.Exists(oldFirst).Should().BeFalse("старый установщик обязан исчезнуть");
        File.Exists(oldSecond).Should().BeFalse("старый установщик обязан исчезнуть");
        File.Exists(fresh).Should().BeTrue("свежий файл может быть незавершённой загрузкой, его трогать нельзя");
        File.Exists(foreign).Should().BeTrue("чужие файлы служба чистить не вправе");
    }

    /// <summary>
    /// Проверяет, что пустая папка даёт ноль без ошибок.
    /// </summary>
    [TestMethod]
    public void CleanupStaleSetupFiles_EmptyDirectory_ReturnsZero()
    {
        // Arrange
        var service = CreateService();

        // Act
        int removed = service.CleanupStaleSetupFiles(_tempDir);

        // Assert
        removed.Should().Be(0, "чистить нечего");
    }

    /// <summary>
    /// Проверяет, что отсутствующая папка не роняет чистку.
    /// </summary>
    [TestMethod]
    public void CleanupStaleSetupFiles_MissingDirectory_ReturnsZeroWithoutThrowing()
    {
        // Arrange
        var service = CreateService();
        string missing = Path.Combine(_tempDir, "нет-такой-папки");

        // Act
        int removed = service.CleanupStaleSetupFiles(missing);

        // Assert
        removed.Should().Be(0, "несуществующая папка не ошибка для фоновой чистки");
    }

    private UpdateService CreateService()
    {
        return new UpdateService(
            _logServiceMock.Object,
            _settingsManagerMock.Object,
            _httpClientFactoryMock.Object);
    }

    private string Touch(string fileName, int hoursOld)
    {
        string path = Path.Combine(_tempDir, fileName);
        File.WriteAllText(path, "фиктивный установщик");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - TimeSpan.FromHours(hoursOld));
        return path;
    }
}
