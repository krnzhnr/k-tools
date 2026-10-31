// -*- coding: utf-8 -*-
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using KTools_App.Services.Implementations;

namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты сопоставления папки запуска и зарегистрированной установки.
/// Проверяется чистая функция <see cref="InstallLocationService.EvaluateStaleCopy"/>:
/// реестр Windows и файловые версии здесь не затрагиваются.
/// Все комментарии и тестовые описания написаны на русском языке.
/// </summary>
[TestClass]
public class InstallLocationServiceTests
{
    /// <summary>
    /// Проверяет, что запуск из зарегистрированной папки не считается неосновной копией.
    /// </summary>
    [TestMethod]
    public void EvaluateStaleCopy_SameDirectory_ReturnsNull()
    {
        // Arrange
        const string dir = @"C:\Program Files\KTools";

        // Act
        var info = InstallLocationService.EvaluateStaleCopy(
            dir, "2.2.0", dir, dir + @"\KTools.App.exe", "2.2.0");

        // Assert
        info.Should().BeNull("запуск из папки установки является основным");
    }

    /// <summary>
    /// Проверяет, что сравнение папок нечувствительно к регистру и завершающему разделителю.
    /// </summary>
    [TestMethod]
    public void EvaluateStaleCopy_SameDirectoryDifferentCaseAndTrailingSlash_ReturnsNull()
    {
        // Act
        var info = InstallLocationService.EvaluateStaleCopy(
            @"c:\program files\ktools\",
            "2.2.0",
            @"C:\Program Files\KTools",
            @"C:\Program Files\KTools\KTools.App.exe",
            "2.2.0");

        // Assert
        info.Should().BeNull("нормализация обязана гасить регистр и завершающий слеш");
    }

    /// <summary>
    /// Проверяет, что отсутствие данных об установке (портативный запуск) не даёт ложного срабатывания.
    /// </summary>
    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void EvaluateStaleCopy_NoRegisteredDirectory_ReturnsNull(string? registeredDir)
    {
        // Act
        var info = InstallLocationService.EvaluateStaleCopy(
            @"D:\Tools\KTools",
            "2.1.0",
            registeredDir,
            @"C:\Program Files\KTools\KTools.App.exe",
            "2.2.0");

        // Assert
        info.Should().BeNull("без папки установки нечего сравнивать");
    }

    /// <summary>
    /// Проверяет, что отсутствие пути к исполняемому файлу установки не даёт ложного срабатывания.
    /// </summary>
    [TestMethod]
    public void EvaluateStaleCopy_NoRegisteredExecutable_ReturnsNull()
    {
        // Act
        var info = InstallLocationService.EvaluateStaleCopy(
            @"D:\Tools\KTools",
            "2.1.0",
            @"C:\Program Files\KTools",
            null,
            "2.2.0");

        // Assert
        info.Should().BeNull("без исполняемого файла переход невозможен");
    }

    /// <summary>
    /// Проверяет главный сценарий: старая копия в другой папке, установка новее — предупреждение нужно.
    /// </summary>
    [TestMethod]
    public void EvaluateStaleCopy_RegisteredNewer_ReturnsInfoWithShouldWarn()
    {
        // Act
        var info = InstallLocationService.EvaluateStaleCopy(
            @"D:\Old\KTools",
            "2.1.0",
            @"C:\Program Files\KTools",
            @"C:\Program Files\KTools\KTools.App.exe",
            "2.2.0");

        // Assert
        info.Should().NotBeNull("папки различаются, установка существует");
        info!.ShouldWarn.Should().BeTrue("установка новее запущенной копии");
        info.RegisteredVersion.Should().Be("2.2.0");
        info.CurrentVersion.Should().Be("2.1.0");
    }

    /// <summary>
    /// Проверяет, что совпадающие версии при разных папках тоже дают предупреждение:
    /// обновление из такой копии всё равно уйдёт в чужую папку.
    /// </summary>
    [TestMethod]
    public void EvaluateStaleCopy_SameVersionDifferentDirectory_ReturnsInfoWithShouldWarn()
    {
        // Act
        var info = InstallLocationService.EvaluateStaleCopy(
            @"D:\Copy\KTools",
            "2.2.0",
            @"C:\Program Files\KTools",
            @"C:\Program Files\KTools\KTools.App.exe",
            "2.2.0.0");

        // Assert
        info.Should().NotBeNull();
        info!.ShouldWarn.Should().BeTrue("равенство версий не отменяет чужую папку обновления");
    }

    /// <summary>
    /// Проверяет, что строго более новая запущенная копия не предупреждает:
    /// пользователь осознанно тестирует новую сборку из другой папки.
    /// </summary>
    [TestMethod]
    public void EvaluateStaleCopy_RunningNewer_ReturnsInfoWithoutWarning()
    {
        // Act
        var info = InstallLocationService.EvaluateStaleCopy(
            @"D:\New\KTools",
            "2.3.0-preview.1",
            @"C:\Program Files\KTools",
            @"C:\Program Files\KTools\KTools.App.exe",
            "2.2.0");

        // Assert
        info.Should().NotBeNull("факт другой папки фиксируется");
        info!.ShouldWarn.Should().BeFalse("запущенная копия строго новее установки");
    }

    /// <summary>
    /// Проверяет, что неизвестная версия установки не подавляет предупреждение.
    /// </summary>
    [TestMethod]
    public void EvaluateStaleCopy_UnknownRegisteredVersion_ReturnsInfoWithShouldWarn()
    {
        // Act
        var info = InstallLocationService.EvaluateStaleCopy(
            @"D:\Old\KTools",
            "2.1.0",
            @"C:\Program Files\KTools",
            @"C:\Program Files\KTools\KTools.App.exe",
            null);

        // Assert
        info.Should().NotBeNull();
        info!.ShouldWarn.Should().BeTrue("новизну запущенной копии доказать невозможно");
    }

    /// <summary>
    /// Проверяет, что метаданные сборки и ведущий 'v' не ломают сравнение версий.
    /// </summary>
    [TestMethod]
    public void EvaluateStaleCopy_VersionMetadata_StrippedBeforeCompare()
    {
        // Act
        var info = InstallLocationService.EvaluateStaleCopy(
            @"D:\Old\KTools",
            "v2.1.0+build.5",
            @"C:\Program Files\KTools",
            @"C:\Program Files\KTools\KTools.App.exe",
            "2.2.0+build.9");

        // Assert
        info.Should().NotBeNull();
        info!.ShouldWarn.Should().BeTrue("2.2.0 новее 2.1.0 независимо от метаданных");
        info.CurrentVersion.Should().Be("2.1.0");
        info.RegisteredVersion.Should().Be("2.2.0");
    }
}
