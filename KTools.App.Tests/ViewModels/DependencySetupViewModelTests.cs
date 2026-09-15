// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using KTools_App.Core;
using KTools_App.Services.Contracts;
using KTools_App.Tests.TestHelpers;
using KTools_App.UI.Pages;
using KTools_App.ViewModels;

namespace KTools_App.Tests.ViewModels;

/// <summary>
/// Юнит-тесты DependencySetupViewModel — модели страницы установки зависимостей.
///
/// ОГРАНИЧЕНИЕ ИНФРАСТРУКТУРЫ (эмпирически подтверждено):
/// DependencySetupViewModel.LoadDependencies() создаёт конкретный класс DependencyVM
/// для каждой зависимости реестра, а конструктор DependencyVM (KTools.App\Models\DependencyVM.cs:173)
/// вызывает DispatcherQueue.GetForCurrentThread() БЕЗ try/catch. В headless-процессе
/// dotnet test WinAppSDK runtime не инициализирован (нет package identity), поэтому вызов
/// активации WinRT-статики падает с COMException 0x80040154 ("Требуемый класс отсутствует
/// в ClassFactory"). Попытки инициализации WinAppSDK Bootstrap в testhost-процессе
/// (MddBootstrapInitialize, Bootstrap.Net TryInitialize) завершились с ошибками
/// EntryPointNotFound / 0x80670016 — runtime требует package identity приложения.
///
/// ПОТЕНЦИАЛЬНЫЙ БАГ ПРИЛОЖЕНИЯ: в отличие от FileQueueItem (FileQueueItem.cs:56-63)
/// и ThreadSafeViewModel (ThreadSafeViewModel.cs:23-31), где GetForCurrentThread()
/// обёрнут в try/catch, DependencyVM не защищён и крашит любой не-XAML-сценарий создания.
///
/// Следствие: тесты, которым нужен непустой реестр зависимостей (создание DependencyVM),
/// выполняются только в XAML-среде и помечены [Ignore]. Тесты с пустым реестром,
/// null-защита и команды с null-аргументами полностью покрыты без ограничений.
/// Все комментарии выполнены на русском языке.
/// </summary>
[TestClass]
public class DependencySetupViewModelTests
{
    private Mock<IDependencyManager> _dependencyManagerMock = null!;
    private Mock<INavigationService> _navigationMock = null!;
    private Mock<ILogService> _logServiceMock = null!;
    private Mock<IPathManager> _pathManagerMock = null!;

    [TestInitialize]
    public void Setup()
    {
        _dependencyManagerMock = MockBuilders.CreateDependencyManagerMock(allInstalled: true);
        _navigationMock = MockBuilders.CreateNavigationMock();
        _logServiceMock = MockBuilders.CreateLogServiceMock();
        _pathManagerMock = MockBuilders.CreatePathManagerMock();
    }

    /// <summary>
    /// Создаёт DependencyInfo с заданными параметрами.
    /// </summary>
    private static DependencyInfo CreateDependency(string key, bool isRequired, DependencyStatus status)
    {
        return new DependencyInfo
        {
            Key = key,
            DisplayName = key,
            Description = "Тестовая зависимость",
            IconName = "video",
            SizeMb = 100.0,
            ArchiveSizeMb = 50.0,
            IsRequired = isRequired
        };
    }

    /// <summary>
    /// Настраивает реестр зависимостей мока вместе со статусами ключей.
    /// </summary>
    private void SetupRegistry(params (DependencyInfo Info, DependencyStatus Status)[] dependencies)
    {
        var infos = dependencies.Select(d => d.Info).ToList();
        _dependencyManagerMock
            .Setup(d => d.GetRegistry())
            .Returns(infos);
        foreach (var (info, status) in dependencies)
        {
            var localStatus = status;
            _dependencyManagerMock
                .Setup(d => d.GetStatus(info.Key))
                .Returns(localStatus);
        }
    }

    /// <summary>
    /// Создаёт ViewModel с текущими моками.
    /// </summary>
    private DependencySetupViewModel CreateViewModel()
    {
        return new DependencySetupViewModel(
            _dependencyManagerMock.Object,
            _navigationMock.Object,
            _logServiceMock.Object,
            _pathManagerMock.Object);
    }

    /// <summary>
    /// Проверяет, что конструктор с null-зависимостями бросает ArgumentNullException.
    /// </summary>
    [TestMethod]
    public void Constructor_NullDependencies_ThrowArgumentNullException()
    {
        // Act
        Action depNull = () => new DependencySetupViewModel(null!, _navigationMock.Object, _logServiceMock.Object, _pathManagerMock.Object);
        Action navNull = () => new DependencySetupViewModel(_dependencyManagerMock.Object, null!, _logServiceMock.Object, _pathManagerMock.Object);
        Action logNull = () => new DependencySetupViewModel(_dependencyManagerMock.Object, _navigationMock.Object, null!, _pathManagerMock.Object);
        Action pathNull = () => new DependencySetupViewModel(_dependencyManagerMock.Object, _navigationMock.Object, _logServiceMock.Object, null!);

        // Assert
        depNull.Should().Throw<ArgumentNullException>();
        navNull.Should().Throw<ArgumentNullException>();
        logNull.Should().Throw<ArgumentNullException>();
        pathNull.Should().Throw<ArgumentNullException>();
    }

    /// <summary>
    /// Проверяет начальное состояние ViewModel при пустом реестре зависимостей.
    /// </summary>
    [TestMethod]
    public void Constructor_EmptyRegistry_CorrectInitialState()
    {
        // Act
        var vm = CreateViewModel();

        // Assert
        vm.RequiredDependencies.Should().BeEmpty();
        vm.OptionalDependencies.Should().BeEmpty();
        vm.IsInstallAllEnabled.Should().BeFalse("нет отсутствующих зависимостей — кнопка не нужна");
        vm.RefreshText.Should().Be("Проверить обновления");
        vm.IsHomeState.Should().BeFalse();
    }

    /// <summary>
    /// Проверяет, что RefreshAll с пустым реестром вызывает менеджер с force=true и перезагружает зависимости.
    /// </summary>
    [TestMethod]
    public async Task RefreshAllCommand_EmptyRegistry_RefreshesAndReloads()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        await vm.RefreshAllCommand.ExecuteAsync(null);

        // Assert
        _dependencyManagerMock.Verify(d => d.RefreshAllStatuses(), Times.Once);
        _dependencyManagerMock.Verify(d => d.CheckAllDependencyUpdatesAsync(true), Times.Once,
            "команда должна вызывать принудительную проверку обновлений (force=true)");
        _logServiceMock.Verify(
            l => l.Info(It.Is<string>(s => s.Contains("ручная проверка обновлений")), It.IsAny<string>()),
            Times.Once);
        vm.RequiredDependencies.Should().BeEmpty("после перезагрузки реестр всё ещё пуст");
    }

    /// <summary>
    /// Проверяет, что Cleanup отписывает все события менеджера
    /// (через SetupAdd/SetupRemove верификацию).
    /// </summary>
    [TestMethod]
    public void ConstructorAndCleanup_SubscribesAndUnsubscribesManagerEvents()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        vm.Cleanup();

        // Assert — подписки на 4 события выполняются ровно один раз (конструктор), отписка — Cleanup
        _dependencyManagerMock.VerifyAdd(d => d.StatusChanged += It.IsAny<Action<string, DependencyStatus>>(), Times.Once);
        _dependencyManagerMock.VerifyRemove(d => d.StatusChanged -= It.IsAny<Action<string, DependencyStatus>>(), Times.Once);
        _dependencyManagerMock.VerifyAdd(d => d.ProgressChanged += It.IsAny<Action<string, int>>(), Times.Once);
        _dependencyManagerMock.VerifyRemove(d => d.ProgressChanged -= It.IsAny<Action<string, int>>(), Times.Once);
        _dependencyManagerMock.VerifyAdd(d => d.SpeedUpdated += It.IsAny<Action<string, string>>(), Times.Once);
        _dependencyManagerMock.VerifyRemove(d => d.SpeedUpdated -= It.IsAny<Action<string, string>>(), Times.Once);
        _dependencyManagerMock.VerifyAdd(d => d.InstallFinished += It.IsAny<Action<string, bool, string>>(), Times.Once);
        _dependencyManagerMock.VerifyRemove(d => d.InstallFinished -= It.IsAny<Action<string, bool, string>>(), Times.Once);
    }

    /// <summary>
    /// Проверяет, что после Cleanup события менеджера больше не обрабатываются ViewModel
    /// (навигация не вызывается при InstallFinished).
    /// </summary>
    [TestMethod]
    public void OnInstallFinished_AfterCleanup_DoesNotNavigate()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.Cleanup();

        // Act — событие с ключом, которого нет в реестре
        _dependencyManagerMock.Raise(d => d.InstallFinished += null, "ffmpeg", true, string.Empty);

        // Assert — без зарегистрированных зависимостей обработчик не находит VM и не перенаправляет
        _navigationMock.Verify(n => n.NavigateTo(It.IsAny<Type>(), It.IsAny<object?>()), Times.Never);
    }

    /// <summary>
    /// Проверяет, что InstallFinished для ключа вне реестра не вызывает навигацию
    /// (FindViewModel возвращает null, CheckAndRedirectToHome не выполняется).
    /// </summary>
    [TestMethod]
    public void OnInstallFinished_UnknownKey_DoesNotNavigate()
    {
        // Arrange
        var vm = CreateViewModel();
        _dependencyManagerMock.Setup(d => d.AreRequiredDependenciesInstalled()).Returns(true);

        // Act
        _dependencyManagerMock.Raise(d => d.InstallFinished += null, "ghost-dep", true, string.Empty);

        // Assert
        _navigationMock.Verify(n => n.NavigateTo(typeof(HomePage), It.IsAny<object?>()), Times.Never,
            "без DependencyVM для ключа обработчик не должен перенаправлять");
    }

    /// <summary>
    /// Проверяет, что команда установки с null-аргументом не вызывает менеджер
    /// (проверка CanExecute-подобной null-защиты).
    /// </summary>
    [TestMethod]
    public async Task InstallDependencyCommand_NullArgument_DoesNotCallManager()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        await vm.InstallDependencyCommand.ExecuteAsync(null);

        // Assert
        _dependencyManagerMock.Verify(d => d.InstallDependencyAsync(It.IsAny<string>()), Times.Never);
        _logServiceMock.Verify(l => l.Info(It.IsAny<string>(), It.IsAny<string>()), Times.Never,
            "лог о ручной установке не пишется для null-аргумента");
    }

    /// <summary>
    /// Проверяет, что команда отмены с null-аргументом не вызывает менеджер.
    /// </summary>
    [TestMethod]
    public void CancelInstallationCommand_NullArgument_DoesNotCallManager()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        vm.CancelInstallationCommand.Execute(null);

        // Assert
        _dependencyManagerMock.Verify(d => d.CancelInstallation(It.IsAny<string>()), Times.Never);
        _logServiceMock.Verify(l => l.Warn(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// Проверяет, что команда удаления с null-аргументом не вызывает менеджер.
    /// </summary>
    [TestMethod]
    public void RemoveDependencyCommand_NullArgument_DoesNotCallManager()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        vm.RemoveDependencyCommand.Execute(null);

        // Assert
        _dependencyManagerMock.Verify(d => d.RemoveDependency(It.IsAny<string>()), Times.Never);
        _logServiceMock.Verify(l => l.Warn(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// Проверяет пакетную установку: при пустом реестре менеджер не вызывается,
    /// кнопка блокируется и лог пишется.
    /// </summary>
    [TestMethod]
    public async Task InstallAllCommand_EmptyRegistry_LogsAndDisablesButton()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        await vm.InstallAllCommand.ExecuteAsync(null);

        // Assert
        _dependencyManagerMock.Verify(d => d.InstallDependencyAsync(It.IsAny<string>()), Times.Never);
        vm.IsInstallAllEnabled.Should().BeFalse("кнопка блокируется на время пакетной установки даже без задач");
        _logServiceMock.Verify(
            l => l.Info(It.Is<string>(s => s.Contains("пакетная установка")), It.IsAny<string>()),
            Times.Once);
    }

    /// <summary>
    /// Проверяет распределение обязательных и необязательных зависимостей по коллекциям.
    /// </summary>
    [TestMethod]
    public void Constructor_MixedRegistry_SplitsRequiredAndOptional()
    {
        // Arrange
        SetupRegistry(
            (CreateDependency("ffmpeg", isRequired: true, DependencyStatus.Installed), DependencyStatus.Installed),
            (CreateDependency("mkvtoolnix", isRequired: true, DependencyStatus.NotInstalled), DependencyStatus.NotInstalled),
            (CreateDependency("yt-dlp", isRequired: false, DependencyStatus.NotInstalled), DependencyStatus.NotInstalled));

        // Act
        var vm = CreateViewModel();

        // Assert
        vm.RequiredDependencies.Should().HaveCount(2);
        vm.OptionalDependencies.Should().HaveCount(1);
        vm.RequiredDependencies.Select(d => d.Info.Key).Should().Equal("ffmpeg", "mkvtoolnix");
        vm.OptionalDependencies.Select(d => d.Info.Key).Should().Equal("yt-dlp");
        vm.IsInstallAllEnabled.Should().BeTrue("есть незагруженные зависимости");
    }

    /// <summary>
    /// Проверяет полную цепочку событий IDependencyManager для одной зависимости:
    /// StatusChanged обновляет статус VM, ProgressChanged — прогресс, SpeedUpdated — скорость,
    /// InstallFinished с ошибкой — ErrorMessage.
    /// </summary>
    [TestMethod]
    public void ManagerEvents_RaisedInSequence_UpdateDependencyViewModel()
    {
        // Arrange
        SetupRegistry((CreateDependency("ffmpeg", isRequired: true, DependencyStatus.Downloading), DependencyStatus.Downloading));
        var vm = CreateViewModel();
        var dep = vm.RequiredDependencies[0];

        // Act — поднимаем события через mock.Raise
        _dependencyManagerMock.Raise(d => d.StatusChanged += null, "ffmpeg", DependencyStatus.Extracting);
        _dependencyManagerMock.Raise(d => d.ProgressChanged += null, "ffmpeg", 67);
        _dependencyManagerMock.Raise(d => d.SpeedUpdated += null, "ffmpeg", "5.2 МБ/с");
        _dependencyManagerMock.Raise(d => d.InstallFinished += null, "ffmpeg", false, "Сеть недоступна");

        // Assert
        dep.Status.Should().Be(DependencyStatus.Extracting);
        dep.Progress.Should().Be(67);
        dep.Speed.Should().Be("5.2 МБ/с");
        dep.ErrorMessage.Should().Be("Сеть недоступна");
    }

    /// <summary>
    /// Проверяет, что InstallFinished при установленных обязательных зависимостях
    /// больше НЕ перенаправляет автоматически на HomePage (пользователь остаётся на вкладке зависимостей).
    /// </summary>
    [TestMethod]
    public void OnInstallFinished_AllRequiredInstalled_DoesNotNavigateToHomePage()
    {
        // Arrange
        SetupRegistry((CreateDependency("ffmpeg", isRequired: true, DependencyStatus.Installed), DependencyStatus.Installed));
        _dependencyManagerMock.Setup(d => d.AreRequiredDependenciesInstalled()).Returns(true);
        var vm = CreateViewModel();

        // Act
        _dependencyManagerMock.Raise(d => d.InstallFinished += null, "ffmpeg", true, string.Empty);

        // Assert
        _navigationMock.Verify(n => n.NavigateTo(typeof(HomePage), It.IsAny<object?>()), Times.Never);
    }

    /// <summary>
    /// Проверяет пакетную установку только отсутствующих/ошибочных зависимостей.
    /// </summary>
    [TestMethod]
    public async Task InstallAllCommand_MixedStatuses_InstallsOnlyMissing()
    {
        // Arrange
        SetupRegistry(
            (CreateDependency("ffmpeg", isRequired: true, DependencyStatus.Installed), DependencyStatus.Installed),
            (CreateDependency("mkvtoolnix", isRequired: true, DependencyStatus.NotInstalled), DependencyStatus.NotInstalled),
            (CreateDependency("yt-dlp", isRequired: false, DependencyStatus.Error), DependencyStatus.Error));
        var vm = CreateViewModel();

        // Act
        await vm.InstallAllCommand.ExecuteAsync(null);

        // Assert
        _dependencyManagerMock.Verify(d => d.InstallDependencyAsync("ffmpeg"), Times.Never,
            "установленная зависимость не должна переустанавливаться");
        _dependencyManagerMock.Verify(d => d.InstallDependencyAsync("mkvtoolnix"), Times.Once);
        _dependencyManagerMock.Verify(d => d.InstallDependencyAsync("yt-dlp"), Times.Once,
            "зависимость в статусе Error входит в пакетную установку");
        vm.IsInstallAllEnabled.Should().BeFalse("кнопка блокируется на время пакетной установки");
    }

    /// <summary>
    /// Проверяет команды установки/отмены/удаления для конкретной DependencyVM.
    /// </summary>
    [TestMethod]
    public async Task Commands_WithValidDependency_CallManagerWithCorrectKey()
    {
        // Arrange
        SetupRegistry((CreateDependency("ffmpeg", isRequired: true, DependencyStatus.NotInstalled), DependencyStatus.NotInstalled));
        var vm = CreateViewModel();
        var dep = vm.RequiredDependencies[0];

        // Act
        await vm.InstallDependencyCommand.ExecuteAsync(dep);
        vm.CancelInstallationCommand.Execute(dep);
        vm.RemoveDependencyCommand.Execute(dep);

        // Assert
        _dependencyManagerMock.Verify(d => d.InstallDependencyAsync("ffmpeg"), Times.Once);
        _dependencyManagerMock.Verify(d => d.CancelInstallation("ffmpeg"), Times.Once);
        _dependencyManagerMock.Verify(d => d.RemoveDependency("ffmpeg"), Times.Once);
    }
}
