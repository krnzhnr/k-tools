// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Models;
using KTools_App.Services.Contracts;
using KTools_App.Tests.TestHelpers;

namespace KTools_App.Tests;

/// <summary>
/// Проверки ручного выбора путей к бинарникам и поиска в PATH (ишью 54).
/// По умолчанию источник — встроенные бинарники из bin/, поведение без
/// настройки один в один как раньше (изоляция bin/ сохраняется).
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class PathManagerBinarySourceIssue54Tests
{
    private readonly List<string> _tempDirs = new();
    private string? _savedPath;

    [TestInitialize]
    public void Setup()
    {
        _savedPath = Environment.GetEnvironmentVariable("PATH");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (_savedPath is not null)
        {
            Environment.SetEnvironmentVariable("PATH", _savedPath);
        }

        foreach (string dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
            }
        }

        _tempDirs.Clear();
    }

    /// <summary>
    /// Создаёт уникальную временную директорию, удаляемую в Cleanup.
    /// </summary>
    private string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "KToolsIssue54", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    /// <summary>
    /// Создаёт PathManager с изолированным каталогом установки.
    /// </summary>
    private static PathManager CreateManager(string baseDir)
    {
        var logMock = MockBuilders.CreateLogServiceMock();
        return new PathManager(logMock.Object, baseDir);
    }

    /// <summary>
    /// Добавляет каталог в начало системного PATH процесса.
    /// </summary>
    private static void PrependToPath(string dir)
    {
        string current = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        Environment.SetEnvironmentVariable("PATH", dir + Path.PathSeparator + current);
    }

    [TestMethod]
    public void GetBinaryPath_Default_IgnoresSystemPath()
    {
        // Arrange — бинарник есть только в системном PATH, переопределений нет.
        string baseDir = CreateTempDir();
        string pathDir = CreateTempDir();
        string uniqueName = "issue54-" + Guid.NewGuid().ToString("N") + ".exe";
        string pathFile = Path.Combine(pathDir, uniqueName);
        File.WriteAllText(pathFile, "stub");
        PrependToPath(pathDir);
        var manager = CreateManager(baseDir);

        // Act
        string resolved = manager.GetBinaryPath(uniqueName);

        // Assert — изоляция bin/: системный PATH по умолчанию не используется.
        resolved.Should().NotBe(pathFile);
        resolved.Should().Be(uniqueName);
    }

    [TestMethod]
    public void GetBinaryPath_UseSystemPathEnabled_ResolvesFromPath()
    {
        // Arrange
        string baseDir = CreateTempDir();
        string pathDir = CreateTempDir();
        string uniqueName = "issue54-" + Guid.NewGuid().ToString("N") + ".exe";
        string pathFile = Path.Combine(pathDir, uniqueName);
        File.WriteAllText(pathFile, "stub");
        PrependToPath(pathDir);
        var manager = CreateManager(baseDir);
        manager.SetUseSystemPath(uniqueName, true);

        // Act
        string resolved = manager.GetBinaryPath(uniqueName);

        // Assert
        resolved.Should().Be(pathFile);
    }

    [TestMethod]
    public void GetBinaryPath_UseSystemPathDisabledAfterEnable_FallsBack()
    {
        // Arrange
        string baseDir = CreateTempDir();
        string pathDir = CreateTempDir();
        string uniqueName = "issue54-" + Guid.NewGuid().ToString("N") + ".exe";
        string pathFile = Path.Combine(pathDir, uniqueName);
        File.WriteAllText(pathFile, "stub");
        PrependToPath(pathDir);
        var manager = CreateManager(baseDir);
        manager.SetUseSystemPath(uniqueName, true);
        manager.GetBinaryPath(uniqueName).Should().Be(pathFile);

        // Act
        manager.SetUseSystemPath(uniqueName, false);
        string resolved = manager.GetBinaryPath(uniqueName);

        // Assert
        resolved.Should().Be(uniqueName);
    }

    [TestMethod]
    public void GetBinaryPath_UseSystemPathMissingOnPath_FallsBackToBinChain()
    {
        // Arrange — флаг включён, но файла нигде нет.
        string baseDir = CreateTempDir();
        string uniqueName = "issue54-" + Guid.NewGuid().ToString("N") + ".exe";
        var manager = CreateManager(baseDir);
        manager.SetUseSystemPath(uniqueName, true);

        // Act
        string resolved = manager.GetBinaryPath(uniqueName);

        // Assert — честный фолбэк, как раньше.
        resolved.Should().Be(uniqueName);
    }

    [TestMethod]
    public void GetBinaryPath_CustomPath_TakesPriorityOverBin()
    {
        // Arrange — файл есть и в bin цепочке, и свой.
        string baseDir = CreateTempDir();
        string legacyBinDir = Path.Combine(baseDir, "bin");
        Directory.CreateDirectory(legacyBinDir);
        string uniqueName = "issue54-" + Guid.NewGuid().ToString("N") + ".exe";
        string binFile = Path.Combine(legacyBinDir, uniqueName);
        File.WriteAllText(binFile, "bin");
        string customDir = CreateTempDir();
        string customFile = Path.Combine(customDir, uniqueName);
        File.WriteAllText(customFile, "custom");
        var manager = CreateManager(baseDir);
        manager.GetBinaryPath(uniqueName).Should().Be(binFile);

        // Act
        manager.SetCustomBinaryPath(uniqueName, customFile);
        string resolved = manager.GetBinaryPath(uniqueName);

        // Assert — явный путь пользователя имеет высший приоритет.
        resolved.Should().Be(customFile);
    }

    [TestMethod]
    public void GetBinaryPath_CustomPathMissing_ReturnsCustomPath()
    {
        // Arrange — своего файла нет на диске: путь возвращается как есть,
        // статус Installed определяется проверкой наличия файла.
        string baseDir = CreateTempDir();
        string customDir = CreateTempDir();
        string uniqueName = "issue54-" + Guid.NewGuid().ToString("N") + ".exe";
        string missingFile = Path.Combine(customDir, uniqueName);
        var manager = CreateManager(baseDir);

        // Act
        manager.SetCustomBinaryPath(uniqueName, missingFile);
        string resolved = manager.GetBinaryPath(uniqueName);

        // Assert
        resolved.Should().Be(missingFile);
    }

    [TestMethod]
    public void SetCustomBinaryPath_Null_ClearsOverride()
    {
        // Arrange
        string baseDir = CreateTempDir();
        string legacyBinDir = Path.Combine(baseDir, "bin");
        Directory.CreateDirectory(legacyBinDir);
        string uniqueName = "issue54-" + Guid.NewGuid().ToString("N") + ".exe";
        string binFile = Path.Combine(legacyBinDir, uniqueName);
        File.WriteAllText(binFile, "bin");
        string customDir = CreateTempDir();
        string customFile = Path.Combine(customDir, uniqueName);
        File.WriteAllText(customFile, "custom");
        var manager = CreateManager(baseDir);
        manager.SetCustomBinaryPath(uniqueName, customFile);
        manager.GetBinaryPath(uniqueName).Should().Be(customFile);

        // Act
        manager.SetCustomBinaryPath(uniqueName, null);
        string resolved = manager.GetBinaryPath(uniqueName);

        // Assert — возврат к встроенной цепочке.
        resolved.Should().Be(binFile);
    }

    [TestMethod]
    public void GetBinaryPath_CustomPath_InvalidatesCachedBinEntry()
    {
        // Arrange — сначала резолв кэширует bin-файл.
        string baseDir = CreateTempDir();
        string legacyBinDir = Path.Combine(baseDir, "bin");
        Directory.CreateDirectory(legacyBinDir);
        string uniqueName = "issue54-" + Guid.NewGuid().ToString("N") + ".exe";
        string binFile = Path.Combine(legacyBinDir, uniqueName);
        File.WriteAllText(binFile, "bin");
        string customDir = CreateTempDir();
        string customFile = Path.Combine(customDir, uniqueName);
        File.WriteAllText(customFile, "custom");
        var manager = CreateManager(baseDir);
        manager.GetBinaryPath(uniqueName).Should().Be(binFile);

        // Act — смена настройки обязана инвалидировать кэш.
        manager.SetCustomBinaryPath(uniqueName, customFile);

        // Assert
        manager.GetBinaryPath(uniqueName).Should().Be(customFile);
    }

    [TestMethod]
    public void GetBinaryPath_SystemAlias_ResolvesFfmpegFromPath()
    {
        // Arrange — в PATH лежит системный ffmpeg.exe, встроенное имя kt-ffmpeg.exe.
        string baseDir = CreateTempDir();
        string pathDir = CreateTempDir();
        string pathFile = Path.Combine(pathDir, "ffmpeg.exe");
        File.WriteAllText(pathFile, "stub");
        PrependToPath(pathDir);
        var manager = CreateManager(baseDir);
        manager.SetUseSystemPath("kt-ffmpeg.exe", true);

        // Act
        string resolved = manager.GetBinaryPath("kt-ffmpeg.exe");

        // Assert
        resolved.Should().Be(pathFile);
    }

    [TestMethod]
    public void SetCustomBinaryPath_EmptyBinaryName_ThrowsArgumentException()
    {
        // Arrange
        var manager = CreateManager(CreateTempDir());

        // Act
        Action setCustom = () => manager.SetCustomBinaryPath(string.Empty, "C:\\tool.exe");
        Action setSystem = () => manager.SetUseSystemPath("   ", true);
        Action invalidate = () => manager.InvalidateBinaryPathCache("  ");

        // Assert
        setCustom.Should().Throw<ArgumentException>();
        setSystem.Should().Throw<ArgumentException>();
        invalidate.Should().Throw<ArgumentException>();
    }
}

/// <summary>
/// Проверки персистентности источника бинарников в DependencyManager (ишью 54).
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class DependencyManagerBinarySourceIssue54Tests
{
    private readonly List<string> _tempDirs = new();
    private readonly Dictionary<string, object?> _settingsStore = new();
    private string? _savedPath;
    private CountingHandler _httpHandler = null!;
    private Mock<IHttpClientFactory> _httpClientFactoryMock = null!;
    private Mock<ILogService> _logServiceMock = null!;
    private Mock<KTools_App.Encoders.IHardwareCapabilityCache> _hardwareCacheMock = null!;

    [TestInitialize]
    public void Setup()
    {
        _savedPath = Environment.GetEnvironmentVariable("PATH");
        _settingsStore.Clear();
        _httpHandler = new CountingHandler();
        var httpClient = new HttpClient(_httpHandler);
        _httpClientFactoryMock = new Mock<IHttpClientFactory>();
        _httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);
        _logServiceMock = MockBuilders.CreateLogServiceMock();
        _hardwareCacheMock = new Mock<KTools_App.Encoders.IHardwareCapabilityCache>();
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (_savedPath is not null)
        {
            Environment.SetEnvironmentVariable("PATH", _savedPath);
        }

        foreach (string dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
            }
        }

        _tempDirs.Clear();
    }

    /// <summary>
    /// Создаёт уникальную временную директорию, удаляемую в Cleanup.
    /// </summary>
    private string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "KToolsIssue54", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    /// <summary>
    /// Создаёт мок настроек с персистентным словарём вместо диска.
    /// </summary>
    private Mock<ISettingsManager> CreateBackedSettingsMock()
    {
        var mock = MockBuilders.CreateSettingsManagerMock();
        mock.Setup(m => m.GetSetting(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns<string, string, string>((group, key, defaultValue) =>
                _settingsStore.TryGetValue(group + "/" + key, out object? value) && value is string text
                    ? text
                    : defaultValue);
        mock.Setup(m => m.GetSetting(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .Returns<string, string, bool>((group, key, defaultValue) =>
                _settingsStore.TryGetValue(group + "/" + key, out object? value) && value is bool flag
                    ? flag
                    : defaultValue);
        mock.Setup(m => m.SetSetting(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns<string, string, string>((group, key, value) =>
            {
                _settingsStore[group + "/" + key] = value;
                return PersistenceResult.Succeeded(new[] { group + "/" + key });
            });
        mock.Setup(m => m.SetSetting(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .Returns<string, string, bool>((group, key, value) =>
            {
                _settingsStore[group + "/" + key] = value;
                return PersistenceResult.Succeeded(new[] { group + "/" + key });
            });
        return mock;
    }

    /// <summary>
    /// Создаёт менеджер с настоящим PathManager для резолва и изолированным
    /// BinDir во временной папке (тесты не зависят от файлов на машине).
    /// Вызовы переопределений пробрасываются в настоящий PathManager.
    /// </summary>
    private DependencyManager CreateManager()
    {
        string baseDir = CreateTempDir();
        string tempBinDir = Path.Combine(baseDir, "bin");
        Directory.CreateDirectory(tempBinDir);
        var realPaths = new PathManager(_logServiceMock.Object, baseDir);
        var pathMock = new Mock<IPathManager>();
        pathMock.Setup(m => m.GetBinDirectory()).Returns(tempBinDir);
        pathMock.Setup(m => m.GetBaseDirectory()).Returns(baseDir);
        pathMock.Setup(m => m.GetBinaryPath(It.IsAny<string>()))
            .Returns<string>(binaryName => realPaths.GetBinaryPath(binaryName));
        pathMock.Setup(m => m.SetCustomBinaryPath(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string?>((binaryName, fullPath) => realPaths.SetCustomBinaryPath(binaryName, fullPath));
        pathMock.Setup(m => m.SetUseSystemPath(It.IsAny<string>(), It.IsAny<bool>()))
            .Callback<string, bool>((binaryName, enabled) => realPaths.SetUseSystemPath(binaryName, enabled));
        pathMock.Setup(m => m.InvalidateBinaryPathCache(It.IsAny<string>()))
            .Callback<string?>(binaryName => realPaths.InvalidateBinaryPathCache(binaryName));
        return new DependencyManager(
            _logServiceMock.Object,
            pathMock.Object,
            _httpClientFactoryMock.Object,
            CreateBackedSettingsMock().Object,
            _hardwareCacheMock.Object);
    }

    /// <summary>
    /// Создаёт менеджер поверх общего словаря настроек (имитация перезапуска).
    /// </summary>
    private DependencyManager CreateManagerOnSharedStore()
    {
        return CreateManager();
    }

    [TestMethod]
    public void GetBinarySource_Default_IsBuiltin()
    {
        // Arrange
        var manager = CreateManager();

        // Act / Assert — дефолт не меняется: встроенные зависимости.
        foreach (var dep in manager.GetRegistry())
        {
            manager.GetBinarySource(dep.Key).Should().Be(BinarySource.Builtin, $"источник {dep.Key} по умолчанию встроенный");
        }
    }

    [TestMethod]
    public void SetCustomBinaryPath_ValidFile_PersistedAndResolved()
    {
        // Arrange
        var manager = CreateManager();
        string customDir = CreateTempDir();
        string customFile = Path.Combine(customDir, "kt-ffmpeg.exe");
        File.WriteAllText(customFile, "stub");

        // Act
        PersistenceResult result = manager.SetCustomBinaryPath("ffmpeg", customFile);

        // Assert
        result.IsSuccess.Should().BeTrue();
        manager.GetBinarySource("ffmpeg").Should().Be(BinarySource.Custom);
        manager.GetCustomBinaryPath("ffmpeg").Should().Be(customFile);
        manager.GetEffectiveBinaryPath("ffmpeg").Should().Be(customFile);
        manager.IsInstalled("ffmpeg").Should().BeTrue("свой файл на месте считается установленным");
        _settingsStore.Should().ContainKey(DependencyManager.BinarySourceSettingsGroup + "/ffmpeg.Path");
    }

    [TestMethod]
    public void SetCustomBinaryPath_UnknownKey_ReturnsFailure()
    {
        // Arrange
        var manager = CreateManager();

        // Act
        PersistenceResult result = manager.SetCustomBinaryPath("ghost-dep", "C:\\tools\\ghost.exe");

        // Assert
        result.IsFailure.Should().BeTrue();
    }

    [TestMethod]
    public void SetCustomBinaryPath_EmptyPath_ReturnsFailure()
    {
        // Arrange
        var manager = CreateManager();

        // Act
        PersistenceResult result = manager.SetCustomBinaryPath("ffmpeg", "   ");

        // Assert
        result.IsFailure.Should().BeTrue();
        manager.GetBinarySource("ffmpeg").Should().Be(BinarySource.Builtin);
    }

    [TestMethod]
    public void SetCustomBinaryPath_SystemComponent_ReturnsFailure()
    {
        // Arrange — у декодеров eac3to нет исполняемого файла, свой путь бессмыслен.
        var manager = CreateManager();
        string customDir = CreateTempDir();
        string customFile = Path.Combine(customDir, "decoders.exe");
        File.WriteAllText(customFile, "stub");

        // Act
        PersistenceResult result = manager.SetCustomBinaryPath("eac3to_decoders", customFile);

        // Assert
        result.IsFailure.Should().BeTrue();
    }

    [TestMethod]
    public void SetCustomBinaryPath_MissingFile_AcceptedButNotInstalled()
    {
        // Arrange — путь на отсутствующем носителе: хранится, но статуса Installed нет.
        var manager = CreateManager();
        string missingFile = Path.Combine(CreateTempDir(), "kt-ffmpeg.exe");

        // Act
        PersistenceResult result = manager.SetCustomBinaryPath("ffmpeg", missingFile);

        // Assert
        result.IsSuccess.Should().BeTrue();
        manager.GetBinarySource("ffmpeg").Should().Be(BinarySource.Custom);
        manager.IsInstalled("ffmpeg").Should().BeFalse("файла нет — зависимость не установлена");
    }

    [TestMethod]
    public void SetCustomBinaryPath_SurvivesManagerRestart()
    {
        // Arrange
        var first = CreateManagerOnSharedStore();
        string customDir = CreateTempDir();
        string customFile = Path.Combine(customDir, "mkvmerge.exe");
        File.WriteAllText(customFile, "stub");
        first.SetCustomBinaryPath("mkvtoolnix", customFile).IsSuccess.Should().BeTrue();

        // Act — новый менеджер поверх того же словаря настроек.
        var second = CreateManagerOnSharedStore();

        // Assert — пути переживают перезапуск, кэш PathManager инвалидирован загрузкой.
        second.GetBinarySource("mkvtoolnix").Should().Be(BinarySource.Custom);
        second.GetEffectiveBinaryPath("mkvtoolnix").Should().Be(customFile);
        second.IsInstalled("mkvtoolnix").Should().BeTrue();
    }

    [TestMethod]
    public void ClearBinarySource_AfterCustom_ResetsToBuiltin()
    {
        // Arrange
        var manager = CreateManager();
        string customDir = CreateTempDir();
        string customFile = Path.Combine(customDir, "kt-ffmpeg.exe");
        File.WriteAllText(customFile, "stub");
        manager.SetCustomBinaryPath("ffmpeg", customFile).IsSuccess.Should().BeTrue();

        // Act
        PersistenceResult result = manager.ClearBinarySource("ffmpeg");

        // Assert
        result.IsSuccess.Should().BeTrue();
        manager.GetBinarySource("ffmpeg").Should().Be(BinarySource.Builtin);
        manager.GetCustomBinaryPath("ffmpeg").Should().BeNull();
        manager.GetEffectiveBinaryPath("ffmpeg").Should().NotBe(customFile);
    }

    [TestMethod]
    public void SetBinaryUseSystemPath_Enabled_ResolvesFromPath()
    {
        // Arrange — системный mkvmerge.exe в PATH.
        var manager = CreateManager();
        string pathDir = CreateTempDir();
        string pathFile = Path.Combine(pathDir, "mkvmerge.exe");
        File.WriteAllText(pathFile, "stub");
        string current = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        Environment.SetEnvironmentVariable("PATH", pathDir + Path.PathSeparator + current);

        // Act
        PersistenceResult result = manager.SetBinaryUseSystemPath("mkvtoolnix", true);

        // Assert
        result.IsSuccess.Should().BeTrue();
        manager.GetBinarySource("mkvtoolnix").Should().Be(BinarySource.SystemPath);
        manager.GetEffectiveBinaryPath("mkvtoolnix").Should().Be(pathFile);
        manager.IsInstalled("mkvtoolnix").Should().BeTrue();
    }

    [TestMethod]
    public void SetBinaryUseSystemPath_UnknownKey_ReturnsFailure()
    {
        // Arrange
        var manager = CreateManager();

        // Act
        PersistenceResult result = manager.SetBinaryUseSystemPath("ghost-dep", true);

        // Assert
        result.IsFailure.Should().BeTrue();
    }

    [TestMethod]
    public void GetEffectiveBinaryPath_UnknownKey_ReturnsEmpty()
    {
        // Arrange
        var manager = CreateManager();

        // Act / Assert
        manager.GetEffectiveBinaryPath("ghost-dep").Should().BeEmpty();
        manager.GetCustomBinaryPath("ghost-dep").Should().BeNull();
        manager.GetBinarySource("ghost-dep").Should().Be(BinarySource.Builtin);
    }

    [TestMethod]
    public async Task RemoveDependencyAsync_CustomSource_KeepsUserFile()
    {
        // Arrange
        var manager = CreateManager();
        string customDir = CreateTempDir();
        string customFile = Path.Combine(customDir, "kt-ffmpeg.exe");
        File.WriteAllText(customFile, "stub");
        manager.SetCustomBinaryPath("ffmpeg", customFile).IsSuccess.Should().BeTrue();

        // Act — удаление не трогает файл пользователя.
        bool removed = await manager.RemoveDependencyAsync("ffmpeg");

        // Assert
        removed.Should().BeTrue();
        File.Exists(customFile).Should().BeTrue("свой файл удалять нельзя");
        manager.GetStatus("ffmpeg").Should().Be(DependencyStatus.Installed);
    }

    [TestMethod]
    public async Task InstallDependencyAsync_CustomSource_SkipsDownload()
    {
        // Arrange
        var manager = CreateManager();
        string customDir = CreateTempDir();
        string customFile = Path.Combine(customDir, "kt-ffmpeg.exe");
        File.WriteAllText(customFile, "stub");
        manager.SetCustomBinaryPath("ffmpeg", customFile).IsSuccess.Should().BeTrue();
        string? finishedMessage = null;
        bool finishedSuccess = true;
        manager.InstallFinished += (key, success, message) =>
        {
            finishedSuccess = success;
            finishedMessage = message;
        };

        // Act
        await manager.InstallDependencyAsync("ffmpeg");

        // Assert — сеть не используется, скачивание встроенной сборки не запускается.
        _httpHandler.CallCount.Should().Be(0);
        finishedSuccess.Should().BeFalse("установка при своём пути пропускается");
        finishedMessage.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// Считающий обработчик HTTP: фиксирует попытки сетевых запросов без реальной сети.
    /// </summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int CallCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref CallCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}

/// <summary>
/// Проверки карточки зависимости со своим путём (ишью 54).
/// </summary>
[TestClass]
public sealed class DependencyVMBinarySourceIssue54Tests
{
    private Mock<IDependencyManager> _dependencyManagerMock = null!;

    [TestInitialize]
    public void Setup()
    {
        _dependencyManagerMock = MockBuilders.CreateDependencyManagerMock(allInstalled: true);
        _dependencyManagerMock.Setup(d => d.GetStatus(It.IsAny<string>()))
            .Returns(DependencyStatus.NotInstalled);
    }

    /// <summary>
    /// Создаёт тестовую зависимость ffmpeg.
    /// </summary>
    private static DependencyInfo CreateFfmpeg()
    {
        return new DependencyInfo
        {
            Key = "ffmpeg",
            DisplayName = "FFmpeg + QAAC",
            Description = "Тестовая зависимость",
            IconName = "video",
            VerifyBinary = "kt-ffmpeg.exe",
            SizeMb = 100.0,
            ArchiveSizeMb = 50.0,
            IsRequired = true
        };
    }

    [TestMethod]
    public void Constructor_BuiltinSource_HidesCustomPanel()
    {
        // Arrange / Act
        var vm = new DependencyVM(CreateFfmpeg(), _dependencyManagerMock.Object);

        // Assert — по умолчанию только встроенные кнопки.
        vm.IsCustomSource.Should().BeFalse();
        vm.CurrentBinaryPath.Should().BeEmpty();
        vm.CustomSourceVisibility.Should().Be(Microsoft.UI.Xaml.Visibility.Collapsed);
        vm.InstallButtonVisibility.Should().Be(Microsoft.UI.Xaml.Visibility.Visible);
    }

    [TestMethod]
    public void RefreshBinarySource_CustomSource_ShowsPathAndHidesBuiltinButtons()
    {
        // Arrange
        _dependencyManagerMock.Setup(d => d.GetStatus(It.IsAny<string>()))
            .Returns(DependencyStatus.Installed);
        _dependencyManagerMock.Setup(d => d.GetInstalledVersionAsync(It.IsAny<string>()))
            .ReturnsAsync(string.Empty);
        string customPath = Path.Combine(Path.GetTempPath(), "kt-ffmpeg-custom.exe");
        _dependencyManagerMock.Setup(d => d.GetBinarySource("ffmpeg")).Returns(BinarySource.Custom);
        _dependencyManagerMock.Setup(d => d.GetCustomBinaryPath("ffmpeg")).Returns(customPath);
        _dependencyManagerMock.Setup(d => d.GetEffectiveBinaryPath("ffmpeg")).Returns(customPath);
        var vm = new DependencyVM(CreateFfmpeg(), _dependencyManagerMock.Object);

        // Act
        vm.RefreshBinarySource();

        // Assert — вместо кнопок «Скачать/Удалить» показывается текущий путь.
        vm.IsCustomSource.Should().BeTrue();
        vm.CurrentBinaryPath.Should().Be(customPath);
        vm.CustomSourceVisibility.Should().Be(Microsoft.UI.Xaml.Visibility.Visible);
        vm.InstallButtonVisibility.Should().Be(Microsoft.UI.Xaml.Visibility.Collapsed);
        vm.NormalDeleteButtonVisibility.Should().Be(Microsoft.UI.Xaml.Visibility.Collapsed);
    }

    [TestMethod]
    public async Task SelectCustomPathCommand_PickerReturnsNull_DoesNotCallManager()
    {
        // Arrange
        var vm = new DependencyVM(CreateFfmpeg(), _dependencyManagerMock.Object);
        vm.PickCustomBinaryPathAsync = () => Task.FromResult<string?>(null);

        // Act
        await vm.SelectCustomPathCommand.ExecuteAsync(null);

        // Assert
        _dependencyManagerMock.Verify(
            d => d.SetCustomBinaryPath(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [TestMethod]
    public async Task SelectCustomPathCommand_PickerReturnsPath_AppliesSource()
    {
        // Arrange
        string customPath = Path.Combine(Path.GetTempPath(), "kt-ffmpeg-picked.exe");
        _dependencyManagerMock.Setup(d => d.SetCustomBinaryPath("ffmpeg", customPath))
            .Returns(PersistenceResult.Succeeded(new[] { "BinarySources/ffmpeg.Path" }));
        _dependencyManagerMock.Setup(d => d.GetBinarySource("ffmpeg")).Returns(BinarySource.Custom);
        _dependencyManagerMock.Setup(d => d.GetCustomBinaryPath("ffmpeg")).Returns(customPath);
        _dependencyManagerMock.Setup(d => d.GetEffectiveBinaryPath("ffmpeg")).Returns(customPath);
        var vm = new DependencyVM(CreateFfmpeg(), _dependencyManagerMock.Object);
        vm.PickCustomBinaryPathAsync = () => Task.FromResult<string?>(customPath);

        // Act
        await vm.SelectCustomPathCommand.ExecuteAsync(null);

        // Assert
        _dependencyManagerMock.Verify(d => d.SetCustomBinaryPath("ffmpeg", customPath), Times.Once);
        vm.IsCustomSource.Should().BeTrue();
        vm.CurrentBinaryPath.Should().Be(customPath);
    }

    [TestMethod]
    public void ResetBinarySourceCommand_ClearsSource()
    {
        // Arrange
        _dependencyManagerMock.Setup(d => d.GetBinarySource("ffmpeg")).Returns(BinarySource.Custom);
        _dependencyManagerMock.Setup(d => d.ClearBinarySource("ffmpeg"))
            .Returns(PersistenceResult.Succeeded());
        _dependencyManagerMock.Setup(d => d.GetBinarySource(It.Is<string>(key => key != "ffmpeg")))
            .Returns(BinarySource.Builtin);
        var vm = new DependencyVM(CreateFfmpeg(), _dependencyManagerMock.Object);
        vm.IsCustomSource.Should().BeTrue("предусловие: источник свой");

        // Act
        vm.ResetBinarySourceCommand.Execute(null);

        // Assert
        _dependencyManagerMock.Verify(d => d.ClearBinarySource("ffmpeg"), Times.Once);
    }

    [TestMethod]
    public void UseSystemPathCommand_EnablesPathLookup()
    {
        // Arrange
        _dependencyManagerMock.Setup(d => d.SetBinaryUseSystemPath("ffmpeg", true))
            .Returns(PersistenceResult.Succeeded());
        var vm = new DependencyVM(CreateFfmpeg(), _dependencyManagerMock.Object);

        // Act
        vm.UseSystemPathCommand.Execute(null);

        // Assert
        _dependencyManagerMock.Verify(d => d.SetBinaryUseSystemPath("ffmpeg", true), Times.Once);
    }
}
