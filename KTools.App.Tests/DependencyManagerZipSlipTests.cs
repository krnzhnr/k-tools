// -*- coding: utf-8 -*-
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using KTools_App.Core;
using KTools_App.Services.Contracts;

namespace KTools_App.Tests;

/// <summary>
/// Проверки защиты от Zip Slip в распаковке зависимостей (issue #83).
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public sealed class DependencyManagerZipSlipTests
{
    private Mock<ILogService> _logServiceMock = null!;
    private Mock<IPathManager> _pathManagerMock = null!;
    private Mock<IHttpClientFactory> _httpClientFactoryMock = null!;
    private Mock<ISettingsManager> _settingsManagerMock = null!;
    private Mock<KTools_App.Encoders.IHardwareCapabilityCache> _hardwareCacheMock = null!;
    private string _tempRoot = null!;

    [TestInitialize]
    public void Setup()
    {
        _logServiceMock = new Mock<ILogService>();
        _pathManagerMock = new Mock<IPathManager>();
        _httpClientFactoryMock = new Mock<IHttpClientFactory>();
        _settingsManagerMock = new Mock<ISettingsManager>();
        _hardwareCacheMock = new Mock<KTools_App.Encoders.IHardwareCapabilityCache>();

        // Уникальная временная папка для каждого теста.
        _tempRoot = Path.Combine(Path.GetTempPath(), "KTools_ZipSlip83_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);

        _pathManagerMock.Setup(pm => pm.GetBinDirectory()).Returns(_tempRoot);
        _pathManagerMock.Setup(pm => pm.GetBaseDirectory()).Returns(_tempRoot);
        _pathManagerMock.Setup(pm => pm.GetBinaryPath(It.IsAny<string>()))
            .Returns<string>(binName => Path.Combine(_tempRoot, binName));

        // Мок фабрики HTTP-клиентов, чтобы конструктор менеджера не падал.
        var httpClient = new System.Net.Http.HttpClient(new FakeHttpMessageHandler());
        _httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempRoot))
        {
            try { Directory.Delete(_tempRoot, true); } catch { }
        }
    }

    /// <summary>
    /// Создаёт zip-архив с заданными записями (имена хранятся как есть, без нормализации).
    /// </summary>
    private static void CreateZip(string archivePath, params (string name, string content)[] entries)
    {
        using var stream = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);
        foreach (var (name, content) in entries)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
    }

    /// <summary>
    /// Вызывает приватный метод ExtractArchiveAsync через отражение.
    /// </summary>
    private static Task InvokeExtractAsync(DependencyManager manager, DependencyInfo dep, string archivePath, string destinationDir)
    {
        MethodInfo? method = typeof(DependencyManager).GetMethod(
            "ExtractArchiveAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.IsNotNull(method, "Приватный метод ExtractArchiveAsync не найден.");
        object? result = method.Invoke(manager, new object[] { dep, archivePath, destinationDir, CancellationToken.None });
        return (Task)result!;
    }

    [TestMethod]
    public async Task ExtractArchive_MaliciousEntries_DoesNotWriteOutsideDestination()
    {
        // Arrange — архив с выходом наверх и абсолютным путём плюс легитимный файл.
        string destinationDir = Path.Combine(_tempRoot, "dest");
        Directory.CreateDirectory(destinationDir);
        string archivePath = Path.Combine(_tempRoot, "evil.zip");
        const string evilName = "evil-83.exe";
        CreateZip(
            archivePath,
            ("../../" + evilName, "зловред"),
            ("/absolute-83-evil.exe", "зловред"),
            ("safe/ok.txt", "легитимное содержимое"));

        var manager = new DependencyManager(
            _logServiceMock.Object,
            _pathManagerMock.Object,
            _httpClientFactoryMock.Object,
            _settingsManagerMock.Object,
            _hardwareCacheMock.Object);
        var dep = new DependencyInfo { Key = "test83", StripTopLevelFolder = false };

        // Act
        await InvokeExtractAsync(manager, dep, archivePath, destinationDir);

        // Assert — легитимный файл распакован внутри назначения.
        File.Exists(Path.Combine(destinationDir, "safe", "ok.txt")).Should().BeTrue("легитимная запись обязана распаковываться");

        // Чужеродные записи пропущены: ничего не вышло за пределы назначения.
        string destinationFull = Path.GetFullPath(destinationDir);
        string[] allFiles = Directory.Exists(_tempRoot)
            ? Directory.GetFiles(_tempRoot, "*", SearchOption.AllDirectories)
            : Array.Empty<string>();
        foreach (string file in allFiles)
        {
            if (string.Equals(file, archivePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Path.GetFullPath(file).Should().StartWith(
                destinationFull + Path.DirectorySeparatorChar,
                "распаковка не должна писать за пределы папки назначения");
        }

        // Явная проверка: файл с выходом наверх не создан рядом с назначением.
        string outsideTraversal = Path.GetFullPath(Path.Combine(destinationDir, "../../" + evilName));
        File.Exists(outsideTraversal).Should().BeFalse("запись с ../ не должна создавать файл за пределами назначения");
        File.Exists(Path.Combine(destinationDir, evilName)).Should().BeFalse("зловред не должен оказаться и внутри назначения под обрезанным именем");
    }

    [TestMethod]
    public async Task ExtractArchive_LegitArchiveWithStripTopLevel_ExtractsInsideDestination()
    {
        // Arrange — легитимный архив с верхней папкой, срезка обязана сохраниться.
        string destinationDir = Path.Combine(_tempRoot, "dest-legit");
        Directory.CreateDirectory(destinationDir);
        string archivePath = Path.Combine(_tempRoot, "legit.zip");
        CreateZip(
            archivePath,
            ("topdir/inner-ok.txt", "легитимное содержимое"));

        var manager = new DependencyManager(
            _logServiceMock.Object,
            _pathManagerMock.Object,
            _httpClientFactoryMock.Object,
            _settingsManagerMock.Object,
            _hardwareCacheMock.Object);
        var dep = new DependencyInfo { Key = "test83", StripTopLevelFolder = true };

        // Act
        await InvokeExtractAsync(manager, dep, archivePath, destinationDir);

        // Assert — срезка верхнего уровня работает, файл лежит в корне назначения.
        File.Exists(Path.Combine(destinationDir, "inner-ok.txt")).Should().BeTrue("срезка верхней папки не должна ломаться для легитимных архивов");
    }

    /// <summary>
    /// Фейковый обработчик HTTP, чтобы конструктор менеджера не ходил в сеть.
    /// </summary>
    private sealed class FakeHttpMessageHandler : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
