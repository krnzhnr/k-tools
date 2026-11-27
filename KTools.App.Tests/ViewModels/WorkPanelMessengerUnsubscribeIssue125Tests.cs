// -*- coding: utf-8 -*-
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using KTools_App.Services.Contracts;
using KTools_App.Tests.TestHelpers;
using KTools_App.ViewModels;
using KTools_App.ViewModels.Messages;
using Moq;

namespace KTools_App.Tests.ViewModels;

/// <summary>
/// Проверки детерминированной отписки WorkPanelViewModel от WeakReferenceMessenger (ишью #125).
/// Transient-VM оставались получателями сообщений до сборки мусора;
/// Dispose обязан снимать все подписки мессенджера.
/// </summary>
[TestClass]
public class WorkPanelMessengerUnsubscribeIssue125Tests : IsolatedMessengerTestBase
{
    private Mock<INavigationService> _navigationMock = null!;
    private Mock<IDialogService> _dialogMock = null!;
    private Mock<ISettingsManager> _settingsMock = null!;
    private Mock<ILogService> _logMock = null!;
    private Mock<IDependencyManager> _dependencyMock = null!;
    private Mock<IMediaProbeService> _probeMock = null!;

    [TestInitialize]
    public void Setup()
    {
        _navigationMock = MockBuilders.CreateNavigationMock();
        _dialogMock = MockBuilders.CreateDialogMock();
        _settingsMock = MockBuilders.CreateSettingsManagerMock();
        _logMock = MockBuilders.CreateLogServiceMock();
        _dependencyMock = MockBuilders.CreateDependencyManagerMock(allInstalled: true);
        _probeMock = MockBuilders.CreateMediaProbeMock();
    }

    /// <summary>
    /// Создаёт WorkPanelViewModel с моками и трекает подписки мессенджера.
    /// </summary>
    private WorkPanelViewModel CreateViewModel()
    {
        var vm = new WorkPanelViewModel(
            _navigationMock.Object,
            _dialogMock.Object,
            _settingsMock.Object,
            _logMock.Object,
            _dependencyMock.Object,
            _probeMock.Object);
        MessengerIsolation.Track(vm);
        return vm;
    }

    /// <summary>
    /// Проверяет, что Dispose снимает обе подписки мессенджера,
    /// зарегистрированные в конструкторе ViewModel.
    /// </summary>
    [TestMethod]
    public void Dispose_RegisteredViewModel_UnregistersAllMessages()
    {
        // Arrange
        var vm = CreateViewModel();
        WeakReferenceMessenger.Default.IsRegistered<TrackSelectedMessage>(vm)
            .Should().BeTrue("конструктор подписывается на выбор дорожек");
        WeakReferenceMessenger.Default.IsRegistered<OutputPathRememberingChangedMessage>(vm)
            .Should().BeTrue("конструктор подписывается на переключение запоминания путей");

        // Act
        vm.Dispose();

        // Assert
        WeakReferenceMessenger.Default.IsRegistered<TrackSelectedMessage>(vm)
            .Should().BeFalse("отписанная ViewModel не должна получать выбор дорожек");
        WeakReferenceMessenger.Default.IsRegistered<OutputPathRememberingChangedMessage>(vm)
            .Should().BeFalse("отписанная ViewModel не должна получать переключение запоминания путей");
    }

    /// <summary>
    /// Проверяет идемпотентность детерминированной отписки:
    /// повторный вызов Dispose не бросает исключений.
    /// </summary>
    [TestMethod]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        Action act = () =>
        {
            vm.Dispose();
            vm.Dispose();
        };

        // Assert
        act.Should().NotThrow("повторная отписка обязана быть безопасной");
        WeakReferenceMessenger.Default.IsRegistered<TrackSelectedMessage>(vm)
            .Should().BeFalse("подписки сняты первым вызовом Dispose");
    }
}
