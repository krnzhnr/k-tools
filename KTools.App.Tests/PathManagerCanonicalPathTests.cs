// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Tests.TestHelpers;

namespace KTools_App.Tests;

/// <summary>
/// Проверки канонической модели хранения данных (ишью 46):
/// всё лежит в %LocalAppData%\KTools, рядом с exe — только portable-режим
/// с маркером portable.txt. Никаких проб записи и маркеров write-probe-*.json.
/// </summary>
[TestClass]
public class PathManagerCanonicalPathTests
{
    private readonly List<string> _tempDirs = new();
    private readonly List<string> _extraCleanupFiles = new();

    [TestCleanup]
    public void Cleanup()
    {
        foreach (string file in _extraCleanupFiles)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        _extraCleanupFiles.Clear();

        foreach (string dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch (IOException)
            {
                // Тихое игнорирование транзитных блокировок временной папки.
            }
            catch (UnauthorizedAccessException)
            {
                // Тихое игнорирование транзитных блокировок временной папки.
            }
        }

        _tempDirs.Clear();
    }

    [TestMethod]
    public void GetSettingsDirectory_DefaultMode_ReturnsLocalAppData()
    {
        // Arrange — каталог установки без маркера portable.
        string baseDir = CreateTempBaseDir();
        var logMock = MockBuilders.CreateLogServiceMock();
        var manager = new PathManager(logMock.Object, baseDir);
        string[] probesBefore = Directory.GetFiles(
            LocalDataRoot(), "write-probe-*.json", SearchOption.TopDirectoryOnly);

        // Act
        string settingsDir = manager.GetSettingsDirectory();

        // Assert — канонический путь, без проб в каталоге установки.
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KTools");
        settingsDir.Should().Be(expected);
        Directory.GetFiles(baseDir, ".write_test*", SearchOption.AllDirectories).Should().BeEmpty();
        Directory.GetFiles(baseDir, "write-probe-*", SearchOption.AllDirectories).Should().BeEmpty();
        Directory.GetFiles(LocalDataRoot(), "write-probe-*.json", SearchOption.TopDirectoryOnly)
            .Except(probesBefore).Should().BeEmpty("пробные маркеры больше не создаются");
    }

    [TestMethod]
    public void GetBinDirectory_DefaultMode_ReturnsLocalAppDataBin()
    {
        // Arrange
        string baseDir = CreateTempBaseDir();
        var logMock = MockBuilders.CreateLogServiceMock();
        var manager = new PathManager(logMock.Object, baseDir);

        // Act
        string binDir = manager.GetBinDirectory();

        // Assert
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KTools",
            "bin");
        binDir.Should().Be(expected);
        Directory.GetFiles(baseDir, ".write_test*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [TestMethod]
    public void GetSettingsDirectory_PortableMarker_ReturnsBaseDir()
    {
        // Arrange — маркер portable.txt рядом с exe.
        string baseDir = CreateTempBaseDir();
        File.WriteAllText(Path.Combine(baseDir, "portable.txt"), string.Empty);
        var logMock = MockBuilders.CreateLogServiceMock();
        var manager = new PathManager(logMock.Object, baseDir);

        // Act
        string settingsDir = manager.GetSettingsDirectory();
        string binDir = manager.GetBinDirectory();

        // Assert — всё рядом с exe, как раньше.
        settingsDir.Should().Be(baseDir);
        binDir.Should().Be(Path.Combine(baseDir, "bin"));
        manager.IsPortableMode().Should().BeTrue();
    }

    [TestMethod]
    public void IsPortableMode_MarkerAbsent_ReturnsFalse()
    {
        // Arrange
        string baseDir = CreateTempBaseDir();
        var logMock = MockBuilders.CreateLogServiceMock();
        var manager = new PathManager(logMock.Object, baseDir);

        // Act / Assert
        manager.IsPortableMode().Should().BeFalse();
    }

    [TestMethod]
    public void GetSettingsDirectory_WindowsAppsPath_ReturnsLocalAppDataWithoutProbes()
    {
        // Arrange — путь внутри защищённой папки (бывший MSIX-случай — теперь общее правило).
        string tempRoot = CreateTempBaseDir();
        string baseDir = Path.Combine(tempRoot, "WindowsApps", "KTools_1.0");
        Directory.CreateDirectory(baseDir);
        var logMock = MockBuilders.CreateLogServiceMock();

        // Act
        var manager = new PathManager(logMock.Object, baseDir);
        string settingsDir = manager.GetSettingsDirectory();
        string binDir = manager.GetBinDirectory();

        // Assert — всегда LocalAppData, проб нет.
        string expectedSettings = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KTools");
        settingsDir.Should().Be(expectedSettings);
        binDir.Should().Be(Path.Combine(expectedSettings, "bin"));
        Directory.GetFiles(tempRoot, ".write_test*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [TestMethod]
    public void GetSettingsDirectory_FallbackMode_ReturnsBaseDir()
    {
        // Arrange — режим отката после сбоя миграции.
        string baseDir = CreateTempBaseDir();
        var logMock = MockBuilders.CreateLogServiceMock();
        var manager = new PathManager(logMock.Object, baseDir);

        // Act
        manager.EnterLegacyFallbackMode();
        string settingsDir = manager.GetSettingsDirectory();
        string binDir = manager.GetBinDirectory();

        // Assert — старт «по-старому», из каталога установки.
        settingsDir.Should().Be(baseDir);
        binDir.Should().Be(Path.Combine(baseDir, "bin"));
    }

    [TestMethod]
    public void GetBinaryPath_LegacyBinFallback_ResolvesExistingFile()
    {
        // Arrange — файл есть только в старом месте (переходный период миграции).
        string baseDir = CreateTempBaseDir();
        string legacyFile = Path.Combine(baseDir, "bin", "legacytool", "legacytool.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyFile)!);
        File.WriteAllText(legacyFile, "stub");
        var logMock = MockBuilders.CreateLogServiceMock();
        var manager = new PathManager(logMock.Object, baseDir);

        // Act
        string resolved = manager.GetBinaryPath("legacytool.exe");

        // Assert — старый файл находится, приложение работает до конца чистки.
        resolved.Should().Be(legacyFile);
    }

    [TestMethod]
    public void GetBinaryPath_CanonicalBinPriority_ReturnsNewLocation()
    {
        // Arrange — один и тот же файл в обоих местах: побеждает новый путь.
        string baseDir = CreateTempBaseDir();
        string uniqueName = "prioritytool-" + Guid.NewGuid().ToString("N") + ".exe";
        string legacyFile = Path.Combine(baseDir, "bin", uniqueName);
        Directory.CreateDirectory(Path.GetDirectoryName(legacyFile)!);
        File.WriteAllText(legacyFile, "old");
        var logMock = MockBuilders.CreateLogServiceMock();
        var manager = new PathManager(logMock.Object, baseDir);
        string canonicalFile = Path.Combine(manager.GetBinDirectory(), uniqueName);
        File.WriteAllText(canonicalFile, "new");
        _extraCleanupFiles.Add(canonicalFile);

        // Act
        string resolved = manager.GetBinaryPath(uniqueName);

        // Assert
        resolved.Should().Be(canonicalFile);
    }

    private static string LocalDataRoot()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string root = Path.Combine(localAppData, "KTools");
        if (!Directory.Exists(root))
        {
            Directory.CreateDirectory(root);
        }

        return root;
    }

    /// <summary>
    /// Создаёт уникальную временную директорию для имитации каталога установки.
    /// </summary>
    /// <returns>Путь к созданной директории.</returns>
    private string CreateTempBaseDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "KToolsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }
}
