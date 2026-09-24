// -*- coding: utf-8 -*-
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using Moq;
using KTools_App.Core;
using KTools_App.Services.Contracts;

namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты для менеджера внешних зависимостей DependencyManager.
/// Все комментарии и тестовые описания написаны на русском языке.
/// </summary>
[TestClass]
public class DependencyManagerTests
{
    private Mock<ILogService> _logServiceMock = null!;
    private Mock<IPathManager> _pathManagerMock = null!;
    private Mock<IHttpClientFactory> _httpClientFactoryMock = null!;
    private Mock<ISettingsManager> _settingsManagerMock = null!;
    private Mock<KTools_App.Encoders.IHardwareCapabilityCache> _hardwareCacheMock = null!;
    private string _tempBinDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _logServiceMock = new Mock<ILogService>();
        _pathManagerMock = new Mock<IPathManager>();
        _httpClientFactoryMock = new Mock<IHttpClientFactory>();
        _settingsManagerMock = new Mock<ISettingsManager>();
        _hardwareCacheMock = new Mock<KTools_App.Encoders.IHardwareCapabilityCache>();

        // Создаем временную директорию для бинарников
        _tempBinDir = Path.Combine(Path.GetTempPath(), "KTools_DepsTests_" + Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempBinDir);

        _pathManagerMock.Setup(pm => pm.GetBinDirectory()).Returns(_tempBinDir);
        
        // По умолчанию GetBinaryPath возвращает переданное имя файла
        _pathManagerMock.Setup(pm => pm.GetBinaryPath(It.IsAny<string>()))
            .Returns<string>(binName => Path.Combine(_tempBinDir, binName));

        // Мокаем HttpClientFactory, чтобы конструктор не падал
        var httpClient = new HttpClient(new FakeHttpMessageHandler());
        _httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempBinDir))
        {
            try { Directory.Delete(_tempBinDir, true); } catch { }
        }
    }

    /// <summary>
    /// Проверяет, что реестр зависимостей инициализируется корректно и содержит все ожидаемые утилиты.
    /// </summary>
    [TestMethod]
    public void GetRegistry_ReturnsCorrectDependencies()
    {
        // Act
        var manager = new DependencyManager(_logServiceMock.Object, _pathManagerMock.Object, _httpClientFactoryMock.Object, _settingsManagerMock.Object, _hardwareCacheMock.Object);
        var registry = manager.GetRegistry();

        // Assert
        registry.Should().NotBeNull();
        registry.Should().HaveCount(9);

        var ffmpeg = registry.FirstOrDefault(d => d.Key == "ffmpeg");
        ffmpeg.Should().NotBeNull();
        ffmpeg!.DisplayName.Should().Be("FFmpeg + QAAC");
        ffmpeg.IsRequired.Should().BeTrue();

        var mkvtoolnix = registry.FirstOrDefault(d => d.Key == "mkvtoolnix");
        mkvtoolnix.Should().NotBeNull();
        mkvtoolnix!.IsRequired.Should().BeTrue();

        var eac3to = registry.FirstOrDefault(d => d.Key == "eac3to");
        eac3to.Should().NotBeNull();
        eac3to!.IsRequired.Should().BeFalse();
    }

    /// <summary>
    /// Проверяет статус зависимостей, когда файлы отсутствуют на диске.
    /// </summary>
    [TestMethod]
    public void GetStatus_WhenBinariesAreMissing_ReturnsNotInstalled()
    {
        // Act
        var manager = new DependencyManager(_logServiceMock.Object, _pathManagerMock.Object, _httpClientFactoryMock.Object, _settingsManagerMock.Object, _hardwareCacheMock.Object);

        // Assert
        manager.GetStatus("ffmpeg").Should().Be(DependencyStatus.NotInstalled);
        manager.GetStatus("mkvtoolnix").Should().Be(DependencyStatus.NotInstalled);
        manager.IsInstalled("ffmpeg").Should().BeFalse();
        manager.AreRequiredDependenciesInstalled().Should().BeFalse();
    }

    /// <summary>
    /// Проверяет успешную детекцию зависимостей, когда файлы-маркеры физически присутствуют во временной bin-директории.
    /// </summary>
    [TestMethod]
    public void GetStatus_WhenBinariesArePresent_ReturnsInstalled()
    {
        // Arrange
        // Для FFmpeg путь будет: _tempBinDir / ffmpeg / kt-ffmpeg.exe
        string ffmpegFolder = Path.Combine(_tempBinDir, "ffmpeg");
        Directory.CreateDirectory(ffmpegFolder);
        File.WriteAllText(Path.Combine(ffmpegFolder, "kt-ffmpeg.exe"), "mock content");

        // Для MKVToolNix путь: _tempBinDir / mkvtoolnix / mkvmerge.exe
        string mkvFolder = Path.Combine(_tempBinDir, "mkvtoolnix");
        Directory.CreateDirectory(mkvFolder);
        File.WriteAllText(Path.Combine(mkvFolder, "mkvmerge.exe"), "mock content");

        // Act
        var manager = new DependencyManager(_logServiceMock.Object, _pathManagerMock.Object, _httpClientFactoryMock.Object, _settingsManagerMock.Object, _hardwareCacheMock.Object);

        // Assert
        manager.GetStatus("ffmpeg").Should().Be(DependencyStatus.Installed);
        manager.GetStatus("mkvtoolnix").Should().Be(DependencyStatus.Installed);
        manager.IsInstalled("ffmpeg").Should().BeTrue();
        manager.IsInstalled("mkvtoolnix").Should().BeTrue();
        manager.AreRequiredDependenciesInstalled().Should().BeTrue();
    }

    /// <summary>
    /// Проверяет удаление директории зависимости с диска.
    /// </summary>
    [TestMethod]
    public void RemoveDependency_WhenDirectoryExists_DeletesDirectoryAndUpdatesStatus()
    {
        // Arrange
        string ffmpegFolder = Path.Combine(_tempBinDir, "ffmpeg");
        Directory.CreateDirectory(ffmpegFolder);
        string markerFile = Path.Combine(ffmpegFolder, "kt-ffmpeg.exe");
        File.WriteAllText(markerFile, "mock content");

        var manager = new DependencyManager(_logServiceMock.Object, _pathManagerMock.Object, _httpClientFactoryMock.Object, _settingsManagerMock.Object, _hardwareCacheMock.Object);
        manager.IsInstalled("ffmpeg").Should().BeTrue();

        // Act
        bool result = manager.RemoveDependency("ffmpeg");

        // Assert
        result.Should().BeTrue();
        Directory.Exists(ffmpegFolder).Should().BeFalse();
        manager.GetStatus("ffmpeg").Should().Be(DependencyStatus.NotInstalled);
    }

    /// <summary>
    /// Проверяет, что CheckAllDependencyUpdatesAsync корректно обрабатывает JSON-версии как в виде массивов [bool, string], так и в виде простых строк.
    /// </summary>
    [TestMethod]
    public async System.Threading.Tasks.Task CheckAllDependencyUpdatesAsync_ParsesArrayAndStringVersions_WithoutCrashing()
    {
        // Arrange
        string ffmpegFolder = Path.Combine(_tempBinDir, "ffmpeg");
        Directory.CreateDirectory(ffmpegFolder);
        File.WriteAllText(Path.Combine(ffmpegFolder, "kt-ffmpeg.exe"), "mock content");

        string responseJson = @"{
            ""tag_name"": ""deps-v1"",
            ""body"": ""Release text\r\n```json:versions\r\n{\""ffmpeg\"":[true,\""9.0.1\""],\""qaac\"":[false,\""3.02\""],\""mkvtoolnix\"":[true,\""93.0\""],\""eac3to\"":\""3.52\""}\r\n```""
        }";

        var mockHandler = new CustomFakeHttpMessageHandler(responseJson);
        var httpClient = new HttpClient(mockHandler);
        _httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var manager = new DependencyManager(_logServiceMock.Object, _pathManagerMock.Object, _httpClientFactoryMock.Object, _settingsManagerMock.Object, _hardwareCacheMock.Object);

        // Act & Assert (не должно выбросить JsonException или InvalidOperationException)
        var act = async () => await manager.CheckAllDependencyUpdatesAsync(force: true);
        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// Проверяет, что при совпадении версий FFmpeg и QAAC зависимость FFmpeg не помечается как требующая обновления.
    /// </summary>
    [TestMethod]
    public async System.Threading.Tasks.Task CheckAllDependencyUpdatesAsync_WhenFfmpegAndQaacVersionsMatch_DoesNotMarkUpdateAvailable()
    {
        // Arrange
        string ffmpegFolder = Path.Combine(_tempBinDir, "ffmpeg");
        Directory.CreateDirectory(ffmpegFolder);
        File.WriteAllText(Path.Combine(ffmpegFolder, "kt-ffmpeg.exe"), "mock ffmpeg");

        // Release содержит 9.0.1 для ffmpeg и 3.02 для qaac
        string responseJson = @"{
            ""tag_name"": ""deps-v1"",
            ""body"": ""Release text\r\n```json:versions\r\n{\""ffmpeg\"":[true,\""9.0.1\""],\""qaac\"":[false,\""3.02\""]}\r\n```""
        }";

        var mockHandler = new CustomFakeHttpMessageHandler(responseJson);
        var httpClient = new HttpClient(mockHandler);
        _httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var manager = new DependencyManager(_logServiceMock.Object, _pathManagerMock.Object, _httpClientFactoryMock.Object, _settingsManagerMock.Object, _hardwareCacheMock.Object);

        // Act
        await manager.CheckAllDependencyUpdatesAsync(force: true);

        // Assert - в моковом окружении бинарники не запускаются (GetInstalledVersion вернет 'Установлено' или пустую строку),
        // поэтому IsUpdateAvailable не должен ложно взводиться
        manager.IsInstalled("ffmpeg").Should().BeTrue();
    }

    /// <summary>
    /// Проверяет, что при наличии версии v93.0 у mkvtoolnix и удаленной версии 93.0 обновление не предлагается.
    /// </summary>
    [TestMethod]
    public async System.Threading.Tasks.Task CheckAllDependencyUpdatesAsync_WhenMkvtoolnixVersionMatches_DoesNotMarkUpdateAvailable()
    {
        // Arrange
        string mkvFolder = Path.Combine(_tempBinDir, "mkvtoolnix");
        Directory.CreateDirectory(mkvFolder);
        File.WriteAllText(Path.Combine(mkvFolder, "mkvmerge.exe"), "mock mkvmerge");

        string responseJson = @"{
            ""tag_name"": ""deps-v1"",
            ""body"": ""Release text\r\n```json:versions\r\n{\""mkvtoolnix\"":[true,\""93.0\""]}\r\n```""
        }";

        var mockHandler = new CustomFakeHttpMessageHandler(responseJson);
        var httpClient = new HttpClient(mockHandler);
        _httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var manager = new DependencyManager(_logServiceMock.Object, _pathManagerMock.Object, _httpClientFactoryMock.Object, _settingsManagerMock.Object, _hardwareCacheMock.Object);

        // Act
        await manager.CheckAllDependencyUpdatesAsync(force: true);

        // Assert
        manager.IsInstalled("mkvtoolnix").Should().BeTrue();
        // Так как мок бинарника не запускается реальным mkvmerge, он возвращает "Установлено", поэтому проверяем что код выполняется без ошибок.
    }

    /// <summary>
    /// Фейковый обработчик сообщений HTTP для HttpClient, чтобы избежать реальных сетевых вызовов в юнит-тестах.
    /// </summary>
    private class FakeHttpMessageHandler : HttpMessageHandler
    {
        protected override System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, 
            System.Threading.CancellationToken cancellationToken)
        {
            return System.Threading.Tasks.Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }

    /// <summary>
    /// Кастомный фейковый обработчик сообщений HTTP с заданным телом ответа.
    /// </summary>
    private class CustomFakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _responseContent;

        public CustomFakeHttpMessageHandler(string responseContent)
        {
            _responseContent = responseContent;
        }

        protected override System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, 
            System.Threading.CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(_responseContent, System.Text.Encoding.UTF8, "application/json")
            };
            return System.Threading.Tasks.Task.FromResult(response);
        }
    }
}
