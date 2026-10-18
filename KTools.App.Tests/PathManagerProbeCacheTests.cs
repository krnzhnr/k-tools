// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Tests.TestHelpers;

namespace KTools_App.Tests;

/// <summary>
/// Проверки межзапускового кэша пробных записей PathManager (PERF-06):
/// второй запуск не выполняет пробу при валидном маркере,
/// инвалидация маркера приводит к повторной пробе.
/// </summary>
[TestClass]
public class PathManagerProbeCacheTests
{
    private readonly List<string> _tempDirs = new();
    private readonly List<string> _cacheFiles = new();

    [TestCleanup]
    public void Cleanup()
    {
        foreach (string cacheFile in _cacheFiles)
        {
            try
            {
                if (File.Exists(cacheFile))
                {
                    File.Delete(cacheFile);
                }
            }
            catch (IOException)
            {
                // Тихое игнорирование: временный маркер необязателен.
            }
            catch (UnauthorizedAccessException)
            {
                // Тихое игнорирование: временный маркер необязателен.
            }

            try
            {
                string tempFile = cacheFile + ".tmp";
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
            catch (IOException)
            {
                // Тихое игнорирование остатков атомарной записи.
            }
            catch (UnauthorizedAccessException)
            {
                // Тихое игнорирование остатков атомарной записи.
            }
        }

        _cacheFiles.Clear();

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
    public void GetBinDirectory_SecondCall_NoProbeWrite()
    {
        // Arrange — изолированный каталог установки с правами на запись (portable-режим).
        string baseDir = CreateTempBaseDir();
        var logMock = MockBuilders.CreateLogServiceMock();
        var firstManager = new PathManager(logMock.Object, baseDir);

        // Act — первый запуск выполняет единственную пробу и сохраняет маркер.
        string first = firstManager.GetBinDirectory();
        first.Should().Be(Path.Combine(baseDir, "bin"));

        string cacheFile = PathManager.GetProbeCacheFilePath(baseDir);
        _cacheFiles.Add(cacheFile);
        File.Exists(cacheFile).Should().BeTrue("первая проба обязана сохранить маркер решения");
        DateTime firstWrite = File.GetLastWriteTimeUtc(cacheFile);
        string firstContent = File.ReadAllText(cacheFile);

        // Второй запуск — новый экземпляр с тем же путём установки.
        var secondManager = new PathManager(logMock.Object, baseDir);
        string second = secondManager.GetBinDirectory();

        // Assert — проба не повторяется: путь тот же, маркер не перезаписан.
        second.Should().Be(first);
        File.GetLastWriteTimeUtc(cacheFile).Should().Be(firstWrite, "валидный маркер запрещает повторную пробу");
        File.ReadAllText(cacheFile).Should().Be(firstContent, "валидный маркер запрещает обновление кэша");
        Directory.GetFiles(baseDir, ".write_test*").Should().BeEmpty("пробные файлы не должны оставаться в каталоге установки");
    }

    [TestMethod]
    public void GetSettingsDirectory_CacheInvalidated_Reprobes()
    {
        // Arrange — изолированный каталог установки с правами на запись.
        string baseDir = CreateTempBaseDir();
        var logMock = MockBuilders.CreateLogServiceMock();
        var firstManager = new PathManager(logMock.Object, baseDir);

        string first = firstManager.GetSettingsDirectory();
        first.Should().Be(baseDir);

        string cacheFile = PathManager.GetProbeCacheFilePath(baseDir);
        _cacheFiles.Add(cacheFile);
        File.Exists(cacheFile).Should().BeTrue("первая проба обязана сохранить маркер решения");

        // Act — маркер удалён (отсутствие, смена пути или версии, протухание).
        File.Delete(cacheFile);
        File.Exists(cacheFile).Should().BeFalse();

        var secondManager = new PathManager(logMock.Object, baseDir);
        string second = secondManager.GetSettingsDirectory();

        // Assert — повторная проба дала тот же путь и восстановила маркер.
        second.Should().Be(baseDir);
        File.Exists(cacheFile).Should().BeTrue("инвалидация маркера обязана приводить к повторной пробе");
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

    [TestMethod]
    public void GetBinDirectory_UnwritableBaseDir_FallsBackToLocalAppData()
    {
        // Arrange — каталог установки недоступен для записи (случай Program Files
        // без прав: существующий файл вместо директории делает пробу неуспешной).
        string baseDir = CreateTempBaseDir();
        string fileAsBase = Path.Combine(baseDir, "not-a-directory");
        File.WriteAllText(fileAsBase, "stub");
        var logMock = MockBuilders.CreateLogServiceMock();

        string cacheFile = PathManager.GetProbeCacheFilePath(fileAsBase);
        _cacheFiles.Add(cacheFile);

        // Act
        var manager = new PathManager(logMock.Object, fileAsBase);
        string binDir = manager.GetBinDirectory();
        string settingsDir = manager.GetSettingsDirectory();

        // Assert — поведение fallback не изменилось: LocalAppData.
        string expectedBin = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KTools",
            "bin");
        string expectedSettings = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KTools");
        binDir.Should().Be(expectedBin);
        settingsDir.Should().Be(expectedSettings);
    }

    [TestMethod]
    public void GetSettingsDirectory_MsixBaseDir_UsesLocalAppDataWithoutProbe()
    {
        // Arrange — путь установки внутри защищённой папки MSIX.
        string tempRoot = CreateTempBaseDir();
        string baseDir = Path.Combine(tempRoot, "WindowsApps", "KTools_1.0");
        var logMock = MockBuilders.CreateLogServiceMock();

        string cacheFile = PathManager.GetProbeCacheFilePath(baseDir);
        _cacheFiles.Add(cacheFile);

        // Act
        var manager = new PathManager(logMock.Object, baseDir);
        string settingsDir = manager.GetSettingsDirectory();
        string binDir = manager.GetBinDirectory();

        // Assert — MSIX-логика без проб: всегда LocalAppData, маркер не создаётся.
        string expectedSettings = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KTools");
        string expectedBin = Path.Combine(expectedSettings, "bin");
        settingsDir.Should().Be(expectedSettings);
        binDir.Should().Be(expectedBin);
        File.Exists(cacheFile).Should().BeFalse("MSIX-установки маркер не используют и проб не выполняют");
        Directory.GetFiles(tempRoot, ".write_test*", SearchOption.AllDirectories).Should().BeEmpty();
    }
}
