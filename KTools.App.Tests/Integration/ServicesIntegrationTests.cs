// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using Moq.Protected;
using KTools_App.Core;
using KTools_App.Services.Contracts;
using KTools_App.Services.Implementations;
using KTools_App.Tests.TestHelpers;

namespace KTools_App.Tests.Integration;

/// <summary>
/// Интеграционные тесты реальных сервисов приложения с мок-зависимостями:
/// UpdateService (парсинг GitHub-релизов через замоканный HttpMessageHandler),
/// LogService (файловая запись в изолированную временную директорию)
/// и PathManager (формирование путей и 8.3-сокращение).
///
/// Примечание по LogService: конструктор без параметров вызывает InitializeLogFile(),
/// который пишет тестовый файл в AppContext.BaseDirectory — тестовая директория bin
/// доступна для записи, поэтому реальный LogService безопасен headless.
/// Все тесты перенаправляют логи в TempDirectoryScope через InitializeLogFile(customLogDir).
/// </summary>
[TestClass]
public class ServicesIntegrationTests
{
    // ====================================================================
    // UpdateService — реальный сервис, замоканный HTTP-транспорт
    // ====================================================================

    /// <summary>
    /// Строит реальный UpdateService с замоканным HttpMessageHandler,
    /// который отвечает заданным JSON для любого запроса.
    /// </summary>
    private static (UpdateService Service, Mock<HttpMessageHandler> HandlerMock) CreateUpdateService(
        string responseContent,
        HttpStatusCode statusCode,
        out Mock<ILogService> logMock,
        Mock<ISettingsManager>? settingsMock = null)
    {
        logMock = MockBuilders.CreateLogServiceMock();
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(responseContent, Encoding.UTF8, "application/json")
            });

        settingsMock ??= MockBuilders.CreateSettingsManagerMock();
        var httpClient = new HttpClient(handlerMock.Object);
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var service = new UpdateService(
            logMock.Object,
            settingsMock.Object,
            factoryMock.Object);
        return (service, handlerMock);
    }

    private static string ReleaseJson(
        string tag,
        string name,
        bool prerelease,
        string assetName = "KTools_Setup.exe",
        long size = 50_000_000) => $$"""
        [
            {
                "tag_name": "{{tag}}",
                "name": "{{name}}",
                "body": "Список изменений релиза",
                "prerelease": {{prerelease.ToString().ToLowerInvariant()}},
                "assets": [
                    { "name": "{{assetName}}", "size": {{size}}, "browser_download_url": "https://example.com/{{assetName}}" }
                ]
            }
        ]
        """;

    [TestMethod]
    public async Task CheckForUpdatesAsync_DebugSimulateOldVersion_ReturnsUpdateInfo()
    {
        // Arrange — симуляция старой версии (текущая = 1.0.0), удалённый релиз 9.9.9
        var settingsMock = MockBuilders.CreateSettingsManagerMock();
        settingsMock.SetupProperty(m => m.DebugSimulateOldVersion, true);
        string json = ReleaseJson("v9.9.9", "K-Tools C# Edition v9.9.9", prerelease: false);
        var (service, _) = CreateUpdateService(json, HttpStatusCode.OK, out _, settingsMock);

        // Act
        var update = await service.CheckForUpdatesAsync(includePreReleases: false);

        // Assert
        update.Should().NotBeNull("текущая версия симулирована как 1.0.0, удалённый релиз 9.9.9 новее");
        update!.Version.Should().Be("9.9.9");
        update.Title.Should().Be("K-Tools C# Edition v9.9.9");
        update.Changelog.Should().Be("Список изменений релиза");
        update.DownloadUrl.Should().Be("https://example.com/KTools_Setup.exe");
        update.FileName.Should().Be("KTools_Setup.exe");
        update.Size.Should().Be(50_000_000);
        update.IsPrerelease.Should().BeFalse();
    }

    [TestMethod]
    public async Task CheckForUpdatesAsync_PreReleaseFilteredWhenDisabled_DoesNotReturnOlderPre()
    {
        // Arrange — единственный релиз пререлизный, includePreReleases = false
        string json = ReleaseJson("v3.0.0", "K-Tools C# Edition v3.0.0", prerelease: true);
        var settingsMock = MockBuilders.CreateSettingsManagerMock();
        settingsMock.SetupProperty(m => m.DebugSimulateOldVersion, true);
        var (service, _) = CreateUpdateService(json, HttpStatusCode.OK, out _, settingsMock);

        // Act
        var update = await service.CheckForUpdatesAsync(includePreReleases: false);

        // Assert — пререлиз отфильтрован, обновлений нет
        update.Should().BeNull("пререлизы исключены настройкой includePreReleases=false");
    }

    [TestMethod]
    public async Task CheckForUpdatesAsync_PreReleaseIncludedWhenEnabled_ReturnsPreReleaseUpdate()
    {
        // Arrange
        string json = ReleaseJson("v3.0.0", "K-Tools C# Edition v3.0.0", prerelease: true);
        var settingsMock = MockBuilders.CreateSettingsManagerMock();
        settingsMock.SetupProperty(m => m.DebugSimulateOldVersion, true);
        var (service, _) = CreateUpdateService(json, HttpStatusCode.OK, out _, settingsMock);

        // Act
        var update = await service.CheckForUpdatesAsync(includePreReleases: true);

        // Assert
        update.Should().NotBeNull("пререлиз включён в поиск");
        update!.IsPrerelease.Should().BeTrue();
    }

    [TestMethod]
    public async Task CheckForUpdatesAsync_EmptyReleasesList_ReturnsNull()
    {
        // Arrange
        var (service, _) = CreateUpdateService("[]", HttpStatusCode.OK, out _);

        // Act
        var update = await service.CheckForUpdatesAsync(includePreReleases: false);

        // Assert
        update.Should().BeNull();
    }

    [TestMethod]
    public async Task CheckForUpdatesAsync_InvalidJson_ThrowsLoggedException()
    {
        // Arrange — невалидный JSON: EnsureSuccessStatusCode проходит, десериализация падает
        var (service, _) = CreateUpdateService("this is not json at all", HttpStatusCode.OK, out var logMock);

        // Act
        Func<Task> act = () => service.CheckForUpdatesAsync(includePreReleases: false);

        // Assert — сервис логирует исключение и пробрасывает его (документированное поведение)
        await ServicesIntegrationTestsExtensions.ThrowAsyncAsync(act);
        logMock.Verify(
            l => l.Exception(It.IsAny<Exception>(), It.Is<string>(s => s.Contains("GitHub", StringComparison.Ordinal)), It.IsAny<string>()),
            Times.Once);
    }

    [TestMethod]
    public async Task CheckForUpdatesAsync_NetworkError_LogsExceptionAndThrows()
    {
        // Arrange — сетевой сбой: handler выбрасывает HttpRequestException
        var logMock = MockBuilders.CreateLogServiceMock();
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Сетевая ока"));
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient(handlerMock.Object));
        var service = new UpdateService(
            logMock.Object,
            MockBuilders.CreateSettingsManagerMock().Object,
            factoryMock.Object);

        // Act
        Func<Task> act = () => service.CheckForUpdatesAsync(includePreReleases: false);

        // Assert — сервис не глотает исключение молча: логирует и пробрасывает
        await ServicesIntegrationTestsExtensions.ThrowAsyncAsync(act);
        logMock.Verify(
            l => l.Exception(It.IsAny<Exception>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Once);
    }

    [TestMethod]
    public async Task CheckForUpdatesAsync_ReleaseWithoutExeAsset_SkipsRelease()
    {
        // Arrange — релиз без .exe-ассета должен быть пропущен
        string json = """
        [
            {
                "tag_name": "v5.0.0",
                "name": "K-Tools C# Edition v5.0.0",
                "body": "changelog",
                "prerelease": false,
                "assets": [
                    { "name": "sources.zip", "size": 1000, "browser_download_url": "https://example.com/sources.zip" }
                ]
            }
        ]
        """;
        var settingsMock = MockBuilders.CreateSettingsManagerMock();
        settingsMock.SetupProperty(m => m.DebugSimulateOldVersion, true);
        var (service, _) = CreateUpdateService(json, HttpStatusCode.OK, out _, settingsMock);

        // Act
        var update = await service.CheckForUpdatesAsync(includePreReleases: false);

        // Assert
        update.Should().BeNull("релиз без установщика .exe не может быть предложен");
    }

    [TestMethod]
    public async Task CheckForUpdatesAsync_MultipleReleases_PicksNewestVersion()
    {
        // Arrange — несколько стабильных релизов: должен быть выбран максимальный
        string json = $$"""
        [
            {
                "tag_name": "v1.5.0", "name": "v1.5.0", "body": "old", "prerelease": false,
                "assets": [ { "name": "setup.exe", "size": 1, "browser_download_url": "https://example.com/a.exe" } ]
            },
            {
                "tag_name": "v2.10.0", "name": "v2.10.0", "body": "newest", "prerelease": false,
                "assets": [ { "name": "KTools_Setup.exe", "size": 2, "browser_download_url": "https://example.com/b.exe" } ]
            },
            {
                "tag_name": "v2.9.0", "name": "v2.9.0", "body": "middle", "prerelease": false,
                "assets": [ { "name": "setup.exe", "size": 3, "browser_download_url": "https://example.com/c.exe" } ]
            }
        ]
        """;
        var settingsMock = MockBuilders.CreateSettingsManagerMock();
        settingsMock.SetupProperty(m => m.DebugSimulateOldVersion, true);
        var (service, _) = CreateUpdateService(json, HttpStatusCode.OK, out _, settingsMock);

        // Act
        var update = await service.CheckForUpdatesAsync(includePreReleases: false);

        // Assert — v2.10.0 > v2.9.0 > v1.5.0 (SemVer, а не строковое сравнение)
        update.Should().NotBeNull();
        update!.Version.Should().Be("2.10.0");
        update.DownloadUrl.Should().Be("https://example.com/b.exe");
    }

    [TestMethod]
    public async Task CheckForUpdatesAsync_FixedPreReleaseTag_ExtractsVersionFromReleaseName()
    {
        // Arrange — фиксированный тег "csharp-pre-release": версия извлекается из названия
        string json = """
        [
            {
                "tag_name": "csharp-pre-release",
                "name": "K-Tools C# Edition v3.4.5-preview.12",
                "body": "prerelease changelog",
                "prerelease": true,
                "assets": [ { "name": "KTools_Setup.exe", "size": 42, "browser_download_url": "https://example.com/s.exe" } ]
            }
        ]
        """;
        var settingsMock = MockBuilders.CreateSettingsManagerMock();
        settingsMock.SetupProperty(m => m.DebugSimulateOldVersion, true);
        var (service, _) = CreateUpdateService(json, HttpStatusCode.OK, out _, settingsMock);

        // Act
        var update = await service.CheckForUpdatesAsync(includePreReleases: true);

        // Assert — реальная версия 3.4.5-preview.12 извлечена из Name через regex
        update.Should().NotBeNull();
        update!.Version.Should().Be("3.4.5-preview.12");
    }

    [TestMethod]
    public void CompareVersions_SemVerSemantics_StableNewerThanPreRelease()
    {
        // Arrange / Act / Assert — компаратор, используемый UpdateService
        UpdateService.CompareVersions("2.0.0", "2.0.0-preview.5").Should().BePositive("стабильная новее пререлиза");
        UpdateService.CompareVersions("2.0.0-preview.10", "2.0.0-preview.9").Should().BePositive("10 > 9 в суффиксе");
        UpdateService.CompareVersions("v2.0.0", "2.0.0").Should().Be(0, "префикс v игнорируется");
        UpdateService.CompareVersions("2.10.0", "2.9.0").Should().BePositive("числовое, а не строковое сравнение");
        UpdateService.CompareVersions("1.0.0", "1.0.0").Should().Be(0);
    }

    // ====================================================================
    // LogService — реальный сервис с изолированной директорией
    // ====================================================================

    [TestMethod]
    public void LogService_AllLevels_WrittenToFileWithLevelMarkers()
    {
        // Arrange
        using var scope = new TempDirectoryScope();
        var logService = new LogService();
        logService.InitializeLogFile(Path.Combine(scope.RootPath, "logs"));

        // Act
        logService.Info("информационное сообщение", "TestSource");
        logService.Warn("предупреждение", "TestSource");
        logService.Error("ошибка", "TestSource");
        logService.Fatal("фатальная ошибка", "TestSource");
        logService.DebugLog("отладка", "TestSource");

        // Assert
        string content = logService.ReadCurrentLog();
        content.Should().NotBeEmpty();
        content.Should().Contain("информационное сообщение");
        content.Should().Contain("предупреждение");
        content.Should().Contain("ошибка");
        content.Should().Contain("фатальная ошибка");
        content.Should().Contain("отладка");

        string logDir = Path.Combine(scope.RootPath, "logs");
        Directory.GetFiles(logDir, "ktools_*.log").Should().ContainSingle();
    }

    [TestMethod]
    public void LogService_ExceptionMethod_WritesMessageAndStackTrace()
    {
        // Arrange
        using var scope = new TempDirectoryScope();
        var logService = new LogService();
        logService.InitializeLogFile(Path.Combine(scope.RootPath, "logs"));
        Exception ex;
        try
        {
            throw new InvalidOperationException("тестовая ошибка интеграции");
        }
        catch (Exception caught)
        {
            ex = caught;
        }

        // Act
        logService.Exception(ex, "Контекст сбоя", "Integration");

        // Assert
        string content = logService.ReadCurrentLog();
        content.Should().Contain("Контекст сбоя");
        content.Should().Contain("тестовая ошибка интеграции");
        content.Should().Contain("Стек вызовов");
        content.Should().Contain("LogService_ExceptionMethod_WritesMessageAndStackTrace",
            "стек вызовов исключения должен попадать в лог");
        content.Should().Contain("ERROR", "уровень записи — ERROR");
    }

    [TestMethod]
    public void LogService_ClearCurrentLog_EmptiesFileContent()
    {
        // Arrange
        using var scope = new TempDirectoryScope();
        var logService = new LogService();
        logService.InitializeLogFile(Path.Combine(scope.RootPath, "logs"));
        logService.Info("запись до очистки", "Test");

        // Act
        logService.ClearCurrentLog();

        // Assert
        logService.ReadCurrentLog().Should().BeEmpty("файл должен быть очищен");
        logService.Info("запись после очистки", "Test");
        logService.ReadCurrentLog().Should().Contain("запись после очистки", "файл остаётся рабочим после очистки");
    }

    [TestMethod]
    public void LogService_InitializeLogFileCreatesDirectoryStructure_LogFileCreatedInCustomDir()
    {
        // Arrange
        using var scope = new TempDirectoryScope();
        string customDir = Path.Combine(scope.RootPath, "custom_logs");
        var logService = new LogService();

        // Act
        logService.InitializeLogFile(customDir);
        logService.Info("запись в кастомную директорию", "Test");

        // Assert
        Directory.Exists(customDir).Should().BeTrue();
        string[] logFiles = Directory.GetFiles(customDir, "ktools_*.log");
        logFiles.Should().ContainSingle();
        File.ReadAllText(logFiles[0]).Should().Contain("запись в кастомную директорию");
    }

    [TestMethod]
    public async Task LogService_ParallelWritesFromTenThreads_NoLinesLost()
    {
        // Arrange
        using var scope = new TempDirectoryScope();
        var logService = new LogService();
        logService.InitializeLogFile(Path.Combine(scope.RootPath, "logs"));
        const int threads = 10;
        const int writesPerThread = 50;

        // Act — 10 потоков по 50 строк
        var tasks = Enumerable.Range(0, threads)
            .Select(t => Task.Run(() =>
            {
                for (int i = 0; i < writesPerThread; i++)
                {
                    logService.Info($"поток-{t} запись-{i}", "Parallel");
                }
            }))
            .ToArray();
        await Task.WhenAll(tasks);

        // Assert — допускается interleaved-порядок, но потеря строк недопустима
        string content = logService.ReadCurrentLog();
        string[] lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Length.Should().Be(threads * writesPerThread,
            $"все {threads * writesPerThread} строк должны быть в файле (потеря недопустима)");

        // Каждая уникальная строка ровно одна
        var distinct = lines.Select(l => l.TrimEnd('\r')).Distinct().Count();
        distinct.Should().Be(threads * writesPerThread, "все строки должны быть уникальны");
    }

    [TestMethod]
    public void LogService_CleanOldLogs_RemovesFilesOlderThanTenDays()
    {
        // Arrange
        using var scope = new TempDirectoryScope();
        string logDir = Path.Combine(scope.RootPath, "logs");
        Directory.CreateDirectory(logDir);
        string oldFile = Path.Combine(logDir, "ktools_20200101_000000.log");
        string recentFile = Path.Combine(logDir, $"ktools_{DateTime.Now:yyyyMMdd_HHmmss}.log");
        File.WriteAllText(oldFile, "old");
        File.WriteAllText(recentFile, "recent");
        File.SetLastWriteTime(oldFile, DateTime.Now.AddDays(-15));

        var logService = new LogService();

        // Act — InitializeLogFile вызывает ротацию CleanOldLogs для директории
        logService.InitializeLogFile(logDir);

        // Assert — старый удалён, свежий остался
        File.Exists(oldFile).Should().BeFalse("лог старше 10 дней должен удаляться ротацией");
        File.Exists(recentFile).Should().BeTrue("свежий лог не должен удаляться");
    }

    // ====================================================================
    // PathManager — реальные пути приложения
    // ====================================================================

    [TestMethod]
    public void PathManager_GetBaseDirectory_ReturnsTestAssemblyDirectory()
    {
        // Arrange
        var pathManager = new PathManager(MockBuilders.CreateLogServiceMock().Object);

        // Act
        string baseDir = pathManager.GetBaseDirectory();

        // Assert — в тестовом процессе базовая директория = каталог тестовой сборки
        baseDir.Should().Be(AppContext.BaseDirectory);
        Directory.Exists(baseDir).Should().BeTrue();
    }

    [TestMethod]
    public void PathManager_GetBinDirectory_ReturnsWritableDirectory()
    {
        // Arrange
        var pathManager = new PathManager(MockBuilders.CreateLogServiceMock().Object);

        // Act
        string binDir = pathManager.GetBinDirectory();

        // Assert — тестовая директория доступна для записи → bin рядом с базовой
        binDir.Should().Be(Path.Combine(AppContext.BaseDirectory, "bin"));
    }

    [TestMethod]
    public void PathManager_GetSettingsDirectory_ReturnsBaseDirectoryInPortableMode()
    {
        // Arrange — тестовый запуск эквивалентен portable-режиму (есть права записи)
        var pathManager = new PathManager(MockBuilders.CreateLogServiceMock().Object);

        // Act
        string settingsDir = pathManager.GetSettingsDirectory();

        // Assert
        settingsDir.Should().Be(AppContext.BaseDirectory);
    }

    [TestMethod]
    public void PathManager_GetBinaryPath_RenamesFfmpegToKtFfmpegWithExeSuffix()
    {
        // Arrange
        var pathManager = new PathManager(MockBuilders.CreateLogServiceMock().Object);

        // Act — договор об именовании K-Tools: ffmpeg → kt-ffmpeg, подпапка ffmpeg
        string ffmpegPath = pathManager.GetBinaryPath("ffmpeg");

        // Assert — файла нет в bin → возвращено короткое имя для поиска в PATH
        ffmpegPath.Should().Be("kt-ffmpeg.exe", "несуществующая утилита возвращается именем для PATH");
    }

    [TestMethod]
    public void PathManager_GetBinaryPath_MkvmergeMappedToMkvtoolnixSubfolder()
    {
        // Arrange
        using var scope = new TempDirectoryScope();
        var pathManager = new PathManager(MockBuilders.CreateLogServiceMock().Object, scope.RootPath);
        string mkvDir = Path.Combine(scope.RootPath, "bin", "mkvtoolnix");
        string mkvExe = Path.Combine(mkvDir, "mkvmerge.exe");
        Directory.CreateDirectory(mkvDir);
        File.WriteAllText(mkvExe, "stub");

        // Act
        string path = pathManager.GetBinaryPath("mkvmerge");

        // Assert — маппинг подпапок: mkvmerge лежит в bin/mkvtoolnix/
        path.Should().Be(mkvExe);
    }

    [TestMethod]
    public void PathManager_GetBinaryPath_FindsFileInBinRoot()
    {
        // Arrange
        using var scope = new TempDirectoryScope();
        var pathManager = new PathManager(MockBuilders.CreateLogServiceMock().Object, scope.RootPath);
        string binDir = Path.Combine(scope.RootPath, "bin");
        Directory.CreateDirectory(binDir);
        string eacPath = Path.Combine(binDir, "eac3to.exe");
        File.WriteAllText(eacPath, "stub");

        // Act
        string path = pathManager.GetBinaryPath("eac3to");

        // Assert
        path.Should().Be(eacPath);
    }

    [TestMethod]
    public void PathManager_GetShortPath_ShortPath_ReturnsPathUnchanged()
    {
        // Arrange
        var pathManager = new PathManager(MockBuilders.CreateLogServiceMock().Object);
        string shortPath = @"C:\a\b\c.mkv";

        // Act
        string result = pathManager.GetShortPath(shortPath);

        // Assert — пути без пробелов/кириллицы уже соответствуют 8.3-совместимой форме
        result.Should().Be(shortPath);
    }

    [TestMethod]
    public void PathManager_GetShortPath_EmptyAndNullPaths_ReturnedAsIs()
    {
        // Arrange
        var pathManager = new PathManager(MockBuilders.CreateLogServiceMock().Object);

        // Act / Assert — контракт защитных проверок
        pathManager.GetShortPath(string.Empty).Should().BeEmpty();
        pathManager.GetShortPath(null!).Should().BeNull();
    }

    [TestMethod]
    public void PathManager_GetShortPath_LongPathOver260Chars_ReturnsValidResultWithoutException()
    {
        // Arrange — эмпирическая проверка: длинный путь > 260 символов не должен приводить
        // к исключению; результат зависит от настройки 8.3 на томе (может вернуть исходный путь)
        var pathManager = new PathManager(MockBuilders.CreateLogServiceMock().Object);
        string segment = new string('d', 40);
        string longPath = @"C:\" + string.Join("\\", Enumerable.Repeat(segment, 8)) + "\\file.mkv";
        longPath.Length.Should().BeGreaterThan(260);

        // Act
        string result = pathManager.GetShortPath(longPath);

        // Assert — файл не существует: Windows может вернуть 0 → исходный путь; исключений быть не должно
        result.Should().NotBeNull();
        result.Should().NotBeEmpty();
    }

    [TestMethod]
    public void PathManager_GetShortPath_RealFileWithSpaces_ReturnsExistingPath()
    {
        // Arrange — реальный существующий файл с пробелами в имени
        using var scope = new TempDirectoryScope();
        string filePath = scope.CreateFile("folder with spaces\\file name with spaces.txt", "content");
        var pathManager = new PathManager(MockBuilders.CreateLogServiceMock().Object);

        // Act
        string result = pathManager.GetShortPath(filePath);

        // Assert — для существующего файла GetShortPathName обязана вернуть валидный путь
        result.Should().NotBeNullOrWhiteSpace();
        if (!result.Equals(filePath, StringComparison.OrdinalIgnoreCase))
        {
            // Если 8.3-сокращение применилось — результирующий путь должен реально существовать
            File.Exists(result).Should().BeTrue("8.3-путь существующего файла должен существовать");
        }
    }
}

/// <summary>
/// Вспомогательный метод проверки того, что делегат бросает исключение,
/// без указания конкретного типа (JsonException / HttpRequestException зависят от пути ошибки).
/// </summary>
file static class ServicesIntegrationTestsExtensions
{
    public static async System.Threading.Tasks.Task ThrowAsyncAsync(Func<Task> action)
    {
        bool threw = false;
        try
        {
            await action();
        }
        catch
        {
            threw = true;
        }

        if (!threw)
        {
            throw new Microsoft.VisualStudio.TestTools.UnitTesting.AssertFailedException(
                "Ожидалось исключение, но действие завершилось без ошибок.");
        }
    }
}
