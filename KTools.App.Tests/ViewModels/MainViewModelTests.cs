// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using KTools_App;
using KTools_App.Core;
using KTools_App.Services.Contracts;
using KTools_App.Tests.TestHelpers;
using KTools_App.UI.Pages;
using KTools_App.ViewModels;
using CommunityToolkit.Mvvm.Messaging;

namespace KTools_App.Tests.ViewModels;

/// <summary>
/// Юнит-тесты MainViewModel — модели представления главной страницы.
/// Проверяют маршрутизацию навигации по тегам, синхронизацию HeaderTitle/HeaderSubtitle,
/// видимость вкладки логов, InitializeCommand (проверка зависимостей, автообновления),
/// обработку ShellActivationMessage (активация из командной строки/Проводника)
/// и GetTagForScriptName.
/// Все тесты наследуют IsolatedMessengerTestBase: конструктор MainViewModel
/// подписывается на 3 сообщения глобального WeakReferenceMessenger.
/// Все комментарии выполнены на русском языке.
/// </summary>
[TestClass]
public class MainViewModelTests : IsolatedMessengerTestBase
{
    private Mock<INavigationService> _navigationMock = null!;
    private Mock<IScriptRegistry> _scriptRegistryMock = null!;
    private Mock<IDependencyManager> _dependencyManagerMock = null!;
    private Mock<ISettingsManager> _settingsManagerMock = null!;
    private Mock<ILogService> _logServiceMock = null!;
    private SettingsViewModel _settingsViewModel = null!;
    private Mock<IDialogService> _dialogServiceMock = null!;
    private Mock<IUpdateService> _updateServiceMock = null!;
    private Mock<IMediaProbeService> _mediaProbeMock = null!;
    private StubScript _script = null!;

    /// <summary>
    /// Создаёт MainViewModel со всеми зависимостями и трекает его подписки в messenger.
    /// </summary>
    private MainViewModel CreateViewModel()
    {
        var vm = new MainViewModel(
            _navigationMock.Object,
            _scriptRegistryMock.Object,
            _dependencyManagerMock.Object,
            _settingsManagerMock.Object,
            _logServiceMock.Object,
            _settingsViewModel,
            _dialogServiceMock.Object,
            _updateServiceMock.Object,
            _mediaProbeMock.Object);
        MessengerIsolation.Track(vm);
        return vm;
    }

    [TestInitialize]
    public void Setup()
    {
        _navigationMock = MockBuilders.CreateNavigationMock();
        _scriptRegistryMock = MockBuilders.CreateScriptRegistryMock();
        _dependencyManagerMock = MockBuilders.CreateDependencyManagerMock(allInstalled: true);
        _settingsManagerMock = MockBuilders.CreateSettingsManagerMock();
        _logServiceMock = MockBuilders.CreateLogServiceMock();
        _dialogServiceMock = MockBuilders.CreateDialogMock();
        _updateServiceMock = MockBuilders.CreateUpdateMock();
        _mediaProbeMock = MockBuilders.CreateMediaProbeMock();

        // Заглушка Theme/BackdropType — обязательные свойства интерфейса ISettingsManager для SettingsViewModel
        _settingsManagerMock.SetupProperty(m => m.Theme, "System");
        _settingsManagerMock.SetupProperty(m => m.BackdropType, "Mica");
        _settingsManagerMock.SetupProperty(m => m.LogDir, string.Empty);
        _settingsManagerMock.SetupProperty(m => m.DebugSimulateOldVersion, false);
        _settingsManagerMock.SetupProperty(m => m.DebugDisableUpdateAction, false);
        _settingsManagerMock.SetupProperty(m => m.ClearListOnAdd, false);

        _settingsViewModel = new SettingsViewModel(
            _settingsManagerMock.Object,
            _dialogServiceMock.Object,
            _updateServiceMock.Object,
            _logServiceMock.Object,
            MockBuilders.CreatePathManagerMock().Object,
            _scriptRegistryMock.Object,
            _dependencyManagerMock.Object);

        _script = new StubScript(
            _logServiceMock.Object,
            _settingsManagerMock.Object,
            MockBuilders.CreatePathManagerMock().Object);
        _scriptRegistryMock = MockBuilders.CreateScriptRegistryMock(_script);
    }

    /// <summary>
    /// Проверяет, что NavigateCommand с null/пустым тегом не выполняет навигацию.
    /// </summary>
    [TestMethod]
    public void NavigateCommand_NullOrEmptyTag_DoesNotNavigate()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        vm.NavigateCommand.Execute(null);
        vm.NavigateCommand.Execute(string.Empty);

        // Assert
        _navigationMock.Verify(n => n.NavigateTo(It.IsAny<Type>(), It.IsAny<object?>()), Times.Never);
        vm.HeaderTitle.Should().Be("K-Tools", "заголовок не должен меняться при пустом теге");
    }

    /// <summary>
    /// Проверяет переход на страницу настроек и синхронное обновление заголовков.
    /// </summary>
    [TestMethod]
    public void NavigateCommand_SettingsTag_NavigatesToSettingsPageAndUpdatesHeaders()
    {
        // Arrange
        var vm = CreateViewModel();
        var recorder = new PropertyChangedRecorder(vm);

        // Act — один вызов
        vm.NavigateCommand.Execute("settings");

        // Assert
        _navigationMock.Verify(n => n.NavigateTo(typeof(SettingsPage), It.IsAny<object?>()), Times.Once);
        vm.HeaderTitle.Should().Be("Настройки");
        vm.HeaderSubtitle.Should().Be("Общие параметры и конфигурация приложения");
        recorder.GetEventsFor(nameof(MainViewModel.HeaderTitle)).Should().HaveCount(1);
        recorder.GetEventsFor(nameof(MainViewModel.HeaderSubtitle)).Should().HaveCount(1);
        recorder.Detach();
    }

    /// <summary>
    /// Проверяет переход на домашнюю страницу с восстановлением заголовков.
    /// </summary>
    [TestMethod]
    public void NavigateCommand_HomeTag_NavigatesToHomePageAndUpdatesHeaders()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        vm.NavigateCommand.Execute("home");

        // Assert
        _navigationMock.Verify(n => n.NavigateTo(typeof(HomePage), It.IsAny<object?>()), Times.Once);
        vm.HeaderTitle.Should().Be("K-Tools");
        vm.HeaderSubtitle.Should().Be("Ваш персональный набор инструментов для обработки медиа");
    }

    /// <summary>
    /// Проверяет переход на страницу логов.
    /// </summary>
    [TestMethod]
    public void NavigateCommand_LogsTag_NavigatesToLogPageAndUpdatesHeaders()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        vm.NavigateCommand.Execute("logs");

        // Assert
        _navigationMock.Verify(n => n.NavigateTo(typeof(LogPage), It.IsAny<object?>()), Times.Once);
        vm.HeaderTitle.Should().Be("Логи");
        vm.HeaderSubtitle.Should()
            .Be("Просмотр журналов выполнения и системных сообщений в реальном времени");
    }

    /// <summary>
    /// Проверяет переход к калькулятору таймингов через тег tool:timing_calculator.
    /// </summary>
    [TestMethod]
    public void NavigateCommand_TimingCalculatorTag_NavigatesToTimingCalculatorPage()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        vm.NavigateCommand.Execute("tool:timing_calculator");

        // Assert
        _navigationMock.Verify(n => n.NavigateTo(typeof(TimingCalculatorPage), It.IsAny<object?>()), Times.Once);
        vm.HeaderTitle.Should().Be("Калькулятор сдвига");
    }

    /// <summary>
    /// Проверяет переход на страницу зависимостей.
    /// </summary>
    [TestMethod]
    public void NavigateCommand_DependenciesTag_NavigatesToDependencySetupPage()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        vm.NavigateCommand.Execute("dependencies");

        // Assert
        _navigationMock.Verify(n => n.NavigateTo(typeof(DependencySetupPage), It.IsAny<object?>()), Times.Once);
        vm.HeaderTitle.Should().Be("Компоненты");
    }

    /// <summary>
    /// Проверяет переход к скрипту по тегу script:<Имя> с передачей скрипта как параметра.
    /// </summary>
    [TestMethod]
    public void NavigateCommand_ScriptTagByName_NavigatesToWorkPanelWithScriptParameter()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        vm.NavigateCommand.Execute($"script:{StubScript.DefaultName}");

        // Assert
        _navigationMock.Verify(n => n.NavigateTo(typeof(WorkPanel), _script), Times.Once);
        vm.HeaderTitle.Should().Be(StubScript.DefaultName);
        vm.HeaderSubtitle.Should().Be("Тестовый скрипт для юнит-тестов");
    }

    /// <summary>
    /// Проверяет, что неизвестный script-тег не приводит к навигации.
    /// </summary>
    [TestMethod]
    public void NavigateCommand_UnknownScriptTag_DoesNotNavigate()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        vm.NavigateCommand.Execute("script:несуществующий_скрипт");

        // Assert
        _navigationMock.Verify(n => n.NavigateTo(It.IsAny<Type>(), It.IsAny<object?>()), Times.Never);
        vm.HeaderTitle.Should().Be("K-Tools", "заголовок не должен меняться для неизвестного тега");
    }

    /// <summary>
    /// Проверяет, что IsLogsTabVisible синхронизирован с настройкой ShowLogsTab при создании.
    /// </summary>
    [TestMethod]
    public void Constructor_ShowLogsTabTrue_IsLogsTabVisibleSynced()
    {
        // Arrange
        _settingsManagerMock.Object.ShowLogsTab = true;

        // Act
        var vm = CreateViewModel();

        // Assert
        vm.IsLogsTabVisible.Should().BeTrue();
    }

    /// <summary>
    /// Проверяет, что UpdateLogsTabVisibility реагирует на изменение мока и уведомляет INPC.
    /// </summary>
    [TestMethod]
    public void UpdateLogsTabVisibility_SettingsChanged_UpdatesPropertyAndNotifies()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.IsLogsTabVisible.Should().BeFalse();
        var recorder = new PropertyChangedRecorder(vm);

        // Act
        _settingsManagerMock.Object.ShowLogsTab = true;
        vm.UpdateLogsTabVisibility();

        // Assert
        vm.IsLogsTabVisible.Should().BeTrue();
        var events = recorder.GetEventsFor(nameof(MainViewModel.IsLogsTabVisible));
        events.Should().HaveCount(1);
        events[0].Value.Should().Be(true);
        recorder.Detach();
    }

    /// <summary>
    /// Проверяет, что при отсутствии зависимостей InitializeCommand перенаправляет
    /// на страницу зависимостей и НЕ на домашнюю.
    /// </summary>
    [TestMethod]
    public void InitializeCommand_DependenciesNotInstalled_NavigatesToDependencySetupOnly()
    {
        // Arrange
        _dependencyManagerMock = MockBuilders.CreateDependencyManagerMock(allInstalled: false);
        var vm = CreateViewModel();

        // Act
        vm.InitializeCommand.Execute(null);

        // Assert
        _navigationMock.Verify(n => n.NavigateTo(typeof(DependencySetupPage), It.IsAny<object?>()), Times.Once);
        _navigationMock.Verify(n => n.NavigateTo(typeof(HomePage), It.IsAny<object?>()), Times.Never);
    }

    /// <summary>
    /// Проверяет, что при установленных зависимостях InitializeCommand идёт на домашнюю.
    /// </summary>
    [TestMethod]
    public void InitializeCommand_DependenciesInstalled_NavigatesToHome()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        vm.InitializeCommand.Execute(null);

        // Assert
        _navigationMock.Verify(n => n.NavigateTo(typeof(HomePage), It.IsAny<object?>()), Times.Once);
        _navigationMock.Verify(n => n.NavigateTo(typeof(DependencySetupPage), It.IsAny<object?>()), Times.Never);
    }

    /// <summary>
    /// Проверяет, что при AutoCheckUpdates=true вызывается CheckForUpdatesAsync.
    /// </summary>
    [TestMethod]
    public async Task InitializeCommand_AutoCheckUpdatesEnabled_TriggersUpdateCheck()
    {
        // Arrange
        _settingsManagerMock.Object.AutoCheckUpdates = true;
        var update = new UpdateInfo("9.9.9", "Релиз", "Изменения", "http://download", "setup.exe", 100, false);
        _updateServiceMock = MockBuilders.CreateUpdateMock(update);
        var vm = CreateViewModel();

        // Act
        vm.InitializeCommand.Execute(null);

        // Assert — ждём завершения fire-and-forget CheckUpdatesSilentlyAsync (поллинг до 2 сек)
        bool bannerShown = await PollUntilAsync(() => vm.IsUpdateBannerVisible, TimeSpan.FromSeconds(2));

        bannerShown.Should().BeTrue("баннер обновлений должен появиться после фоновой проверки");
        vm.UpdateStatusText.Should().Contain("9.9.9", "текст статуса должен содержать версию");
        vm.NewUpdateInfo.Should().NotBeNull();
        vm.NewUpdateInfo!.Version.Should().Be("9.9.9");
        _updateServiceMock.Verify(u => u.CheckForUpdatesAsync(It.IsAny<bool>(), It.IsAny<System.Threading.CancellationToken>()), Times.AtLeastOnce);
    }

    /// <summary>
    /// Проверяет, что CloseUpdateBannerCommand скрывает баннер обновлений.
    /// </summary>
    [TestMethod]
    public async Task CloseUpdateBannerCommand_BannerVisible_HidesBanner()
    {
        // Arrange
        _settingsManagerMock.Object.AutoCheckUpdates = true;
        var update = new UpdateInfo("9.9.9", "Релиз", "Изменения", "http://download", "setup.exe", 100, false);
        _updateServiceMock = MockBuilders.CreateUpdateMock(update);
        var vm = CreateViewModel();
        vm.InitializeCommand.Execute(null);
        await PollUntilAsync(() => vm.IsUpdateBannerVisible, TimeSpan.FromSeconds(2));
        vm.IsUpdateBannerVisible.Should().BeTrue();

        // Act
        vm.CloseUpdateBannerCommand.Execute(null);

        // Assert
        vm.IsUpdateBannerVisible.Should().BeFalse();
        _logServiceMock.Verify(l => l.Info(It.Is<string>(s => s.Contains("закрыл баннер")), It.IsAny<string>()), Times.Once);
    }

    /// <summary>
    /// Проверяет обработку ShellActivationMessage с legacy-тегом video_encoding.
    /// Используется реальный VideoEncodingScript из реестра приложения (имя "Кодирование видео").
    /// </summary>
    [TestMethod]
    public async Task HandleShellActivation_LegacyVideoEncodingTag_NavigatesToVideoEncodingScript()
    {
        // Arrange — используем реальный скрипт, зарегистрированный под legacy-тегом
        var logMock = MockBuilders.CreateLogServiceMock();
        var settingsMock = MockBuilders.CreateSettingsManagerMock();
        settingsMock.SetupProperty(m => m.Theme, "System");
        settingsMock.SetupProperty(m => m.BackdropType, "Mica");
        var pathMock = MockBuilders.CreatePathManagerMock();
        var hardwareCacheMock = new Mock<KTools_App.Encoders.IHardwareCapabilityCache>();
        var encoderRegistry = new KTools_App.Encoders.VideoEncoderRegistry(
            new List<KTools_App.Encoders.IVideoEncoder>(), hardwareCacheMock.Object);
        var videoScript = new KTools_App.Scripts.VideoEncodingScript(
            logMock.Object,
            settingsMock.Object,
            pathMock.Object,
            new Mock<IFFmpegRunner>().Object,
            MockBuilders.CreateMediaProbeMock().Object,
            encoderRegistry);
        _scriptRegistryMock = MockBuilders.CreateScriptRegistryMock(videoScript);
        var vm = CreateViewModel();
        var files = new List<string> { "C:\\media\\film.mkv" };

        // Act
        CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(new ShellActivationMessage("video_encoding", files));

        // Assert
        _navigationMock.Verify(n => n.NavigateTo(typeof(WorkPanel), videoScript), Times.Once);
        vm.HeaderTitle.Should().Be("Кодирование видео");

        // Файл добавлен в очередь скрипта (после Task.Run-анализа — поллинг)
        bool added = await PollUntilAsync(() => videoScript.FilesQueue.Count == 1, TimeSpan.FromSeconds(2));
        added.Should().BeTrue("файл должен быть добавлен в очередь скрипта");
    }

    /// <summary>
    /// Проверяет, что файлы с неподдерживаемым расширением отфильтровываются.
    /// </summary>
    [TestMethod]
    public async Task HandleShellActivation_UnsupportedExtension_FileFilteredOut()
    {
        // Arrange
        var vm = CreateViewModel();
        var files = new List<string> { "C:\\media\\document.txt", "C:\\media\\film.mkv", "C:\\media\\song.mp3" };

        // Act
        CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(new ShellActivationMessage(null, files));

        // Assert — скрипт поддерживает только .mkv/.mp4
        bool settled = await PollUntilAsync(
            () => _script.FilesQueue.Count == 1 && _script.FilesQueue.All(f => f.FilePath.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase)),
            TimeSpan.FromSeconds(2));
        settled.Should().BeTrue("должен быть добавлен только поддерживаемый .mkv-файл");
        _logServiceMock.Verify(l => l.Warn(It.Is<string>(s => s.Contains("не поддерживается")), It.IsAny<string>()), Times.AtLeast(2));
    }

    /// <summary>
    /// Проверяет, что дубликаты файлов не добавляются в очередь дважды.
    /// </summary>
    [TestMethod]
    public async Task HandleShellActivation_DuplicateFiles_NotAddedTwice()
    {
        // Arrange
        var vm = CreateViewModel();
        var files = new List<string> { "C:\\media\\film.mkv", "C:\\MEDIA\\film.mkv" };

        // Act
        CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(new ShellActivationMessage(null, files));

        // Assert — сравнение по OrdinalIgnoreCase: дубликат не должен добавиться
        bool settled = await PollUntilAsync(() => _script.FilesQueue.Count >= 1, TimeSpan.FromSeconds(2));
        await Task.Delay(200); // даём время возможному добавлению дубля
        _script.FilesQueue.Count.Should().Be(1, "дубликаты (ignore case) не должны добавляться дважды");
    }

    /// <summary>
    /// Проверяет, что при ScriptTag=null файлы направляются в первый скрипт реестра.
    /// </summary>
    [TestMethod]
    public async Task HandleShellActivation_NullTagWithFiles_AddsToFirstScript()
    {
        // Arrange
        var firstScript = new StubScript(_logServiceMock.Object, _settingsManagerMock.Object, MockBuilders.CreatePathManagerMock().Object);
        var secondScript = new StubScript(_logServiceMock.Object, _settingsManagerMock.Object, MockBuilders.CreatePathManagerMock().Object);
        // StubScript всегда возвращает одно имя — переопределяем реестр с двумя различными скриптами через подкласс
        var registryMock = new Mock<IScriptRegistry>();
        registryMock.Setup(r => r.Scripts).Returns(new List<AbstractScript> { firstScript, secondScript });
        registryMock.Setup(r => r.GetScriptByName(It.IsAny<string>()))
            .Returns<string>(name => name == firstScript.Name ? firstScript : secondScript);
        _scriptRegistryMock = registryMock;

        var vm = CreateViewModel();
        var files = new List<string> { "C:\\media\\film.mkv" };

        // Act
        CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(new ShellActivationMessage(null, files));

        // Assert
        bool settled = await PollUntilAsync(() => firstScript.FilesQueue.Count == 1, TimeSpan.FromSeconds(2));
        settled.Should().BeTrue("файл должен попасть в первый скрипт реестра");
        secondScript.FilesQueue.Should().BeEmpty();
    }

    /// <summary>
    /// Проверяет распознавание тега по имени с подчеркиваниями (normalizedTag).
    /// </summary>
    [TestMethod]
    public async Task HandleShellActivation_NameWithUnderscores_RecognizedByName()
    {
        // Arrange — имя скрипта "Тестовый скрипт", тег с подчеркиваниями "тестовый_скрипт"
        var vm = CreateViewModel();
        var files = new List<string> { "C:\\media\\film.mkv" };

        // Act
        CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(new ShellActivationMessage("тестовый_скрипт", files));

        // Assert — normalizedTag "тестовый скрипт" должен совпасть с именем скрипта
        bool settled = await PollUntilAsync(() => _script.FilesQueue.Count == 1, TimeSpan.FromSeconds(2));
        settled.Should().BeTrue("тег с подчеркиваниями должен распознаваться по имени скрипта");
        _navigationMock.Verify(n => n.NavigateTo(typeof(WorkPanel), It.IsAny<AbstractScript>()), Times.Once);
    }

    /// <summary>
    /// Проверяет GetTagForScriptName: точное имя возвращает корректный тег.
    /// </summary>
    [TestMethod]
    public void GetTagForScriptName_ExactName_ReturnsScriptTag()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        string? tag = vm.GetTagForScriptName(StubScript.DefaultName);

        // Assert
        tag.Should().Be($"script:{StubScript.DefaultName}");
    }

    /// <summary>
    /// Проверяет GetTagForScriptName: регистронезависимость.
    /// </summary>
    [TestMethod]
    public void GetTagForScriptName_CaseInsensitive_MatchesTag()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        string? tag = vm.GetTagForScriptName(StubScript.DefaultName.ToUpperInvariant());

        // Assert
        tag.Should().Be($"script:{StubScript.DefaultName}", "поиск должен быть регистронезависимым");
    }

    /// <summary>
    /// Проверяет GetTagForScriptName: несуществующее имя возвращает null.
    /// </summary>
    [TestMethod]
    public void GetTagForScriptName_UnknownName_ReturnsNull()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        string? tag = vm.GetTagForScriptName("Нет такого скрипта");

        // Assert
        tag.Should().BeNull();
    }

    /// <summary>
    /// Вспомогательный метод поллинга до заданного таймаута (без Thread.Sleep).
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
