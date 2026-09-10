// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using KTools_App;
using KTools_App.Core;
using KTools_App.Services.Contracts;
using KTools_App.Scripts;
using KTools_App.Tests.TestHelpers;
using KTools_App.UI.Pages;
using KTools_App.ViewModels;
using CommunityToolkit.Mvvm.Messaging;

namespace KTools_App.Tests.Integration;

/// <summary>
/// Интеграционные тесты сквозного цикла shell-активации: разбор командной строки
/// (SplitCommandLine/ParseCommandLineArray) и полная обработка ShellActivationMessage
/// реальным MainViewModel с реальными скриптами приложения.
///
/// ОГРАНИЧЕНИЯ HEADLESS-СРЕДЫ (задокументировано намеренно):
/// 1. Program.WriteArgsToFile — private static, пишет в реальный %LocalAppData%\KTools\PendingArgs.
///    Прямое тестирование мутировало бы глобальное состояние пользователя; в AppActivationTests
///    уже есть единственный тест с ручной очисткой — здесь не дублируется.
/// 2. App.ProcessPendingArgsFiles при UiDispatcherQueue == null выполняет ранний return
///    (App.xaml.cs:409-413) и логирует предупреждение — метод безопасен без UI-хоста,
///    но осмысленной работы не выполняет, поэтому тестируется только контракт раннего выхода.
/// 3. App.ParseActivationArgs недостижим без AppActivationArguments (WinRT-тип Windows App SDK),
///    который нельзя сконструировать в headless-тесте без package identity.
/// </summary>
[TestClass]
public class ShellActivationIntegrationTests : IsolatedMessengerTestBase
{
    private string _tempDir = null!;
    private string _tempFile = null!;
    private string _tempSubFile = null!;
    private string _tempDirectory = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "KTools_Integration_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _tempFile = Path.Combine(_tempDir, "movie.mkv");
        File.WriteAllText(_tempFile, "dummy");

        _tempSubFile = Path.Combine(_tempDir, "link.url");
        File.WriteAllText(_tempSubFile, "https://example.com");

        _tempDirectory = Path.Combine(_tempDir, "subfolder");
        Directory.CreateDirectory(_tempDirectory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    // ====================================================================
    // SplitCommandLine — расширенные сценарии разделения
    // ====================================================================

    [TestMethod]
    public void SplitCommandLine_QuotedPathWithInnerSpaces_PreservesWholeToken()
    {
        // Arrange
        string commandLine = "\"C:\\my file.mkv\"";

        // Act
        var result = App.SplitCommandLine(commandLine);

        // Assert
        result.Should().HaveCount(1);
        result[0].Should().Be("C:\\my file.mkv");
    }

    [TestMethod]
    public void SplitCommandLine_MultipleSpacesBetweenTokens_SkipsExtraSpaces()
    {
        // Arrange
        string commandLine = "  --script      video_encoding   ";

        // Act
        var result = App.SplitCommandLine(commandLine);

        // Assert
        result.Should().HaveCount(2);
        result[0].Should().Be("--script");
        result[1].Should().Be("video_encoding");
    }

    [TestMethod]
    public void SplitCommandLine_EmptyAndWhitespaceOnly_ReturnsEmptyArray()
    {
        // Arrange / Act
        var emptyResult = App.SplitCommandLine(string.Empty);
        var whitespaceResult = App.SplitCommandLine("   \t  ");

        // Assert
        emptyResult.Should().BeEmpty();
        whitespaceResult.Should().BeEmpty();
    }

    [TestMethod]
    public void SplitCommandLine_OnlyQuotes_ProducesNoTokens()
    {
        // Arrange
        string commandLine = "\"\"";

        // Act
        var result = App.SplitCommandLine(commandLine);

        // Assert — кавычки переключают режим, но токен пустой и не добавляется
        result.Should().BeEmpty();
    }

    [TestMethod]
    public void SplitCommandLine_TrailingSpaceAfterLastToken_DoesNotCreateEmptyToken()
    {
        // Arrange
        string commandLine = "value1 value2 ";

        // Act
        var result = App.SplitCommandLine(commandLine);

        // Assert
        result.Should().HaveCount(2);
        result[1].Should().Be("value2");
    }

    [TestMethod]
    public void SplitCommandLine_CyrillicUnicodePath_PreservedVerbatim()
    {
        // Arrange
        string cyrillicPath = "C:\\Медиа\\Фильмы\\мой фильм.mkv";
        string commandLine = $"--script metadata_cleanup \"{cyrillicPath}\"";

        // Act
        var result = App.SplitCommandLine(commandLine);

        // Assert
        result.Should().HaveCount(3);
        result[2].Should().Be(cyrillicPath, "юникод-путь с кириллицей не должен искажаться");
    }

    [TestMethod]
    public void SplitCommandLine_TabsDoNotSeparateTokens_KeptInsideToken()
    {
        // Arrange — SplitCommandLine разделяет ТОЛЬКО пробелы, табуляция считается частью токена
        string commandLine = "video\tencoding";

        // Act
        var result = App.SplitCommandLine(commandLine);

        // Assert — документируем фактическое поведение: табуляция НЕ является разделителем
        result.Should().HaveCount(1);
        result[0].Should().Be("video\tencoding");
    }

    [TestMethod]
    public void SplitCommandLine_TwoQuotedTokensWithSpaces_EachPreservedIndependently()
    {
        // Arrange — реальный сценарий Проводника: два выделенных файла с пробелами
        string commandLine = "\"C:\\a b.mkv\" \"D:\\c d.mp4\"";

        // Act
        var result = App.SplitCommandLine(commandLine);

        // Assert
        result.Should().HaveCount(2);
        result[0].Should().Be("C:\\a b.mkv");
        result[1].Should().Be("D:\\c d.mp4");
    }

    [TestMethod]
    public void SplitCommandLine_SpaceInsideQuotesIsKept_EscapedLikeBehavior()
    {
        // Arrange — пробел внутри кавычек не должен разрывать токен (аналог escaped-space)
        string commandLine = "\"--script name with spaces\"";

        // Act
        var result = App.SplitCommandLine(commandLine);

        // Assert
        result.Should().HaveCount(1);
        result[0].Should().Be("--script name with spaces");
    }

    // ====================================================================
    // ParseCommandLineArray — расширенные сценарии фильтрации
    // ====================================================================

    [TestMethod]
    public void ParseCommandLineArray_ExePathArgument_FilteredOut()
    {
        // Arrange — при запуске из Проводника args[0] часто содержит путь к самому exe
        string exePath = Environment.ProcessPath ?? "KTools.App.exe";
        string[] args = [exePath, _tempFile];

        // Act
        var (script, files) = App.ParseCommandLineArray(args);

        // Assert
        script.Should().BeNull();
        files.Should().ContainSingle().Which.Should().Be(_tempFile);
    }

    [TestMethod]
    public void ParseCommandLineArray_DllVariantOfExeName_FilteredOut()
    {
        // Arrange — фильтр сравнивает имя без расширения: .exe/.dll эквивалентны
        string baseName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "KTools.App");
        string dllPath = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath ?? ".") ?? ".", baseName + ".dll");
        string[] args = [dllPath, _tempFile];

        // Act
        var (script, files) = App.ParseCommandLineArray(args);

        // Assert — dll с тем же базовым именем отфильтрована, файл остаётся
        script.Should().BeNull();
        files.Should().ContainSingle().Which.Should().Be(_tempFile);
    }

    [TestMethod]
    public void ParseCommandLineArray_ExeNameCaseInsensitive_FilteredOut()
    {
        // Arrange — фильтр приводит к нижнему регистру (ToLowerInvariant)
        string exePath = Environment.ProcessPath ?? "KTools.App.exe";
        string upperPath = exePath.ToUpperInvariant();
        string[] args = [upperPath];

        // Act
        var (_, files) = App.ParseCommandLineArray(args);

        // Assert
        files.Should().BeEmpty("путь к exe в любом регистре должен отфильтровываться");
    }

    [TestMethod]
    public void ParseCommandLineArray_DirectoryPath_AcceptedAsFileEntry()
    {
        // Arrange
        string[] args = ["--script", "container_demux", _tempDirectory];

        // Act
        var (script, files) = App.ParseCommandLineArray(args);

        // Assert — Directory.Exists тоже принимается (папка перетаскивается в Проводнике)
        script.Should().Be("container_demux");
        files.Should().ContainSingle().Which.Should().Be(_tempDirectory);
    }

    [TestMethod]
    public void ParseCommandLineArray_QuotedPath_QuotesTrimmed()
    {
        // Arrange — кавычки обрезаются Trim('"') при парсинге массива
        string quoted = $"\"{_tempFile}\"";
        string[] args = [quoted];

        // Act
        var (_, files) = App.ParseCommandLineArray(args);

        // Assert
        files.Should().ContainSingle().Which.Should().Be(_tempFile);
    }

    [TestMethod]
    public void ParseCommandLineArray_UppercaseScriptFlag_ParsedOrdinalIgnoreCase()
    {
        // Arrange
        string[] args = ["--SCRIPT", "video_encoding", _tempFile];

        // Act
        var (script, files) = App.ParseCommandLineArray(args);

        // Assert
        script.Should().Be("video_encoding");
        files.Should().ContainSingle();
    }

    [TestMethod]
    public void ParseCommandLineArray_ScriptFlagLastArgument_ScriptIsNullButNoCrash()
    {
        // Arrange — "--script" последним элементом: значение взять неоткуда
        string[] args = [_tempFile, "--script"];

        // Act
        var (script, files) = App.ParseCommandLineArray(args);

        // Assert
        script.Should().BeNull();
        files.Should().ContainSingle().Which.Should().Be(_tempFile);
    }

    [TestMethod]
    public void ParseCommandLineArray_NonExistentPaths_FilteredOut()
    {
        // Arrange
        string[] args =
        [
            "C:\\definitely_missing_9x7_file.mkv",
            "Z:\\no_such_drive\\video.mp4",
            _tempFile
        ];

        // Act
        var (_, files) = App.ParseCommandLineArray(args);

        // Assert
        files.Should().ContainSingle().Which.Should().Be(_tempFile);
    }

    [TestMethod]
    public void ProcessPendingArgsFiles_NoUiDispatcher_ReturnsEarlyWithoutException()
    {
        // Arrange — в тестовом процессе UiDispatcherQueue == null (App.OnLaunched никогда не выполнялся),
        // поэтому метод должен сделать ранний return без обращения к файловой системе.
        // Если бы Queue была не null, метод читал бы глобальную папку %LocalAppData%\KTools\PendingArgs —
        // в тестах это глобальное состояние трогать нельзя (см. doc-комментарий класса).

        // Act — не должно упасть
        App.ProcessPendingArgsFiles();

        // Assert — контракт раннего выхода: исключений нет, файлы не тронуты
        // (дополнительная проверка: наш временный файл цел)
        File.Exists(_tempFile).Should().BeTrue();
    }

    // ====================================================================
    // Сквозной цикл: SplitCommandLine → ParseCommandLineArray → ShellActivationMessage → MainViewModel
    // ====================================================================

    /// <summary>
    /// Создаёт полный конвейер активации с реальным VideoEncodingScript
    /// и реальным MainViewModel, но с моками внешних сервисов.
    /// </summary>
    private (MainViewModel ViewModel, Mock<INavigationService> NavigationMock, VideoEncodingScript VideoScript, MediaDownloaderScript DownloaderScript)
        CreateActivationPipeline(
            Mock<INavigationService> navigationMock,
            out Mock<ILogService> logMock)
    {
        var logServiceMock = MockBuilders.CreateLogServiceMock();
        var settingsMock = MockBuilders.CreateSettingsManagerMock();
        settingsMock.SetupProperty(m => m.Theme, "System");
        settingsMock.SetupProperty(m => m.BackdropType, "Mica");
        settingsMock.SetupProperty(m => m.LogDir, string.Empty);
        settingsMock.SetupProperty(m => m.DebugSimulateOldVersion, false);
        settingsMock.SetupProperty(m => m.DebugDisableUpdateAction, false);
        settingsMock.SetupProperty(m => m.ClearListOnAdd, false);
        var pathMock = MockBuilders.CreatePathManagerMock();

        var hardwareCacheMock = new Mock<KTools_App.Encoders.IHardwareCapabilityCache>();
        var encoderRegistry = new KTools_App.Encoders.VideoEncoderRegistry(
            new List<KTools_App.Encoders.IVideoEncoder>(), hardwareCacheMock.Object);

        var videoScript = new VideoEncodingScript(
            logServiceMock.Object,
            settingsMock.Object,
            pathMock.Object,
            new Mock<IFFmpegRunner>().Object,
            MockBuilders.CreateMediaProbeMock().Object,
            encoderRegistry);

        var downloaderScript = new MediaDownloaderScript(
            logServiceMock.Object,
            settingsMock.Object,
            pathMock.Object);

        var registryMock = MockBuilders.CreateScriptRegistryMock(videoScript, downloaderScript);

        var settingsViewModel = new SettingsViewModel(
            settingsMock.Object,
            MockBuilders.CreateDialogMock().Object,
            MockBuilders.CreateUpdateMock().Object,
            logServiceMock.Object,
            pathMock.Object,
            registryMock.Object,
            MockBuilders.CreateDependencyManagerMock().Object);

        var vm = new MainViewModel(
            navigationMock.Object,
            registryMock.Object,
            MockBuilders.CreateDependencyManagerMock().Object,
            settingsMock.Object,
            logServiceMock.Object,
            settingsViewModel,
            MockBuilders.CreateDialogMock().Object,
            MockBuilders.CreateUpdateMock().Object,
            MockBuilders.CreateMediaProbeMock().Object);
        MessengerIsolation.Track(vm);

        logMock = logServiceMock;
        return (vm, navigationMock, videoScript, downloaderScript);
    }

    [TestMethod]
    public async Task FullPipeline_VideoEncodingWithRealFile_AddsFileToQueueAndNavigatesToWorkPanel()
    {
        // Arrange — полный цикл: командная строка → Split → Parse → Message → MainViewModel
        var navigationMock = MockBuilders.CreateNavigationMock();
        var (vm, _, videoScript, _) = CreateActivationPipeline(navigationMock, out _);
        string commandLine = $"--script video_encoding \"{_tempFile}\"";

        // Act
        var split = App.SplitCommandLine(commandLine);
        var (scriptTag, files) = App.ParseCommandLineArray(split);
        WeakReferenceMessenger.Default.Send(new ShellActivationMessage(scriptTag, files));

        // Assert — файл реальный и прошёл все фильтры: попал в очередь реального скрипта
        scriptTag.Should().Be("video_encoding");
        files.Should().ContainSingle().Which.Should().Be(_tempFile);

        bool added = await PollUntilAsync(() => videoScript.FilesQueue.Count == 1, TimeSpan.FromSeconds(3));
        added.Should().BeTrue("реальный .mkv файл должен быть добавлен в очередь VideoEncodingScript");

        videoScript.FilesQueue[0].FilePath.Should().Be(_tempFile);

        // Навигация на WorkPanel с передачей реального экземпляра скрипта
        navigationMock.Verify(
            n => n.NavigateTo(typeof(WorkPanel), videoScript),
            Times.Once,
            "сквозная активация должна завершиться навигацией на WorkPanel с реальным скриптом");

        // Заголовок синхронизирован с именем скрипта
        vm.HeaderTitle.Should().Be(AppConstants.ScriptMetadata.VideoProcessorName, "имя скрипта — 'Кодирование видео'");
    }

    [TestMethod]
    public async Task FullPipeline_UrlFileForMediaDownloader_RoutesToDownloaderScript()
    {
        // Arrange — .url файл должен направляться в "Загрузка медиа" по legacy-тегу media_downloader
        var navigationMock = MockBuilders.CreateNavigationMock();
        var (vm, _, videoScript, downloaderScript) = CreateActivationPipeline(navigationMock, out _);
        string commandLine = $"--script media_downloader \"{_tempSubFile}\"";

        // Act
        var split = App.SplitCommandLine(commandLine);
        var (scriptTag, files) = App.ParseCommandLineArray(split);
        WeakReferenceMessenger.Default.Send(new ShellActivationMessage(scriptTag, files));

        // Assert
        bool added = await PollUntilAsync(() => downloaderScript.FilesQueue.Count == 1, TimeSpan.FromSeconds(3));
        added.Should().BeTrue(".url файл должен попасть в очередь MediaDownloaderScript");

        videoScript.FilesQueue.Should().BeEmpty("файл .url не должен попадать в скрипт кодирования видео");
        downloaderScript.FilesQueue[0].FilePath.Should().Be(_tempSubFile);

        navigationMock.Verify(
            n => n.NavigateTo(typeof(WorkPanel), downloaderScript),
            Times.Once);
        vm.HeaderTitle.Should().Be("Загрузка медиа");
    }

    [TestMethod]
    public async Task FullPipeline_UnsupportedExtension_WarnedInLogAndFileFiltered()
    {
        // Arrange — файл с расширением вне списка VideoContainers должен отфильтроваться с Warn
        var navigationMock = MockBuilders.CreateNavigationMock();
        string unsupported = Path.Combine(_tempDir, "document.txt");
        File.WriteAllText(unsupported, "text");
        var (_, _, videoScript, _) = CreateActivationPipeline(navigationMock, out var logMock);
        var commandLine = $"--script video_encoding \"{unsupported}\" \"{_tempFile}\"";

        // Act
        var split = App.SplitCommandLine(commandLine);
        var (scriptTag, files) = App.ParseCommandLineArray(split);
        WeakReferenceMessenger.Default.Send(new ShellActivationMessage(scriptTag, files));

        // Assert — .txt отфильтрован, .mkv принят
        await Task.Delay(300); // фильтрация расширений синхронна внутри AddFilesToScript
        videoScript.FilesQueue.Should().ContainSingle("только .mkv должен остаться в очереди");
        videoScript.FilesQueue[0].FilePath.Should().Be(_tempFile);

        logMock.Verify(
            l => l.Warn(It.Is<string>(s => s.Contains("не поддерживается", StringComparison.Ordinal)), It.IsAny<string>()),
            Times.AtLeastOnce,
            "фильтрация неподдерживаемого расширения должна логироваться предупреждением");
    }

    [TestMethod]
    public async Task FullPipeline_UnknownScriptTagWithFiles_FallsBackToFirstScriptWithWarning()
    {
        // Arrange — неизвестный тег: файлы уходят в первый скрипт реестра (VideoEncodingScript)
        var navigationMock = MockBuilders.CreateNavigationMock();
        var (_, _, videoScript, _) = CreateActivationPipeline(navigationMock, out var logMock);

        // Act
        WeakReferenceMessenger.Default.Send(new ShellActivationMessage("no_such_script_tag", new List<string> { _tempFile }));

        // Assert
        bool added = await PollUntilAsync(() => videoScript.FilesQueue.Count == 1, TimeSpan.FromSeconds(3));
        added.Should().BeTrue("при нераспознанном теге файлы должны направляться в первый скрипт реестра");

        logMock.Verify(
            l => l.Warn(It.Is<string>(s => s.Contains("не распознан", StringComparison.Ordinal)), It.IsAny<string>()),
            Times.AtLeastOnce);
        navigationMock.Verify(n => n.NavigateTo(typeof(WorkPanel), It.IsAny<AbstractScript>()), Times.Once);
    }

    /// <summary>
    /// Вспомогательный метод поллинга до заданного таймаута.
    /// </summary>
    private static async Task<bool> PollUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }
            await Task.Delay(25);
        }
        return condition();
    }
}
