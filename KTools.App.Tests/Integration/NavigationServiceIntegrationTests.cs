// -*- coding: utf-8 -*-
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using KTools_App.Core;
using KTools_App.Services.Contracts;
using KTools_App.Services.Implementations;
using KTools_App.Tests.TestHelpers;

namespace KTools_App.Tests.Integration;

/// <summary>
/// Интеграционные тесты реальной службы навигации NavigationService.
///
/// ГОЛОВОЛОМКА HEADLESS-СРЕДЫ: NavigationService инкапсулирует WinUI 3 Frame
/// (Microsoft.UI.Xaml.Controls.Frame). Создание Frame в MSTest-процессе без
/// запущенного WinUI-хоста (Application.Start + DispatcherQueue) невозможно:
/// конструктор Frame выбрасывает COMException/TypeInitializationException, так как
/// XAML-движок не инициализирован. Поэтому:
/// - НЕ тестируем реальную навигацию через Frame (3 [Ignore]-теста-декларации ниже);
/// - Тестируем headless-доступную часть контракта: null-Frame → InvalidOperationException,
///   CanGoBack == false без Frame, GoBack не падает без Frame,
///   и сам факт того, что DI-регистрация типа возможна (реальный тип загружается в тестовой сборке).
/// </summary>
[TestClass]
public class NavigationServiceIntegrationTests
{
    [TestMethod]
    public void NavigateTo_FrameNotInitialized_ThrowsInvalidOperationException()
    {
        // Arrange — реальный NavigationService без установленного Frame
        var service = new NavigationService();

        // Act
        Action act = () => service.NavigateTo(typeof(object));

        // Assert — контракт головного класса: явное исключение вместо NRE
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Фрейм навигации не инициализирован*");
    }

    [TestMethod]
    public void CanGoBack_FrameIsNull_ReturnsFalse()
    {
        // Arrange
        var service = new NavigationService();

        // Act
        bool canGoBack = service.CanGoBack;

        // Assert
        canGoBack.Should().BeFalse("без Frame навигация назад невозможна");
    }

    [TestMethod]
    public void GoBack_FrameIsNull_DoesNotThrow()
    {
        // Arrange
        var service = new NavigationService();

        // Act — null-conditional внутри GoBack не должен падать
        Action act = () => service.GoBack();

        // Assert
        act.Should().NotThrow("GoBack безопасен при неинициализированном Frame");
    }

    [TestMethod]
    public void Frame_SetNullAfterSet_DoesNotThrowAndResetsState()
    {
        // Arrange
        var service = new NavigationService();

        // Act — установка null допустима (отписка от событий несуществующего Frame)
        service.Frame = null;

        // Assert
        service.Frame.Should().BeNull();
        service.CanGoBack.Should().BeFalse();
    }

    [TestMethod]
    public void Navigated_WithoutFrame_CanBeSubscribedAndUnsubscribed()
    {
        // Arrange — событие контракта INavigationService доступно headless
        var service = new NavigationService();
        int fired = 0;
        EventHandler<string> handler = (s, pageName) => fired++;
        service.Navigated += handler;

        // Act / Assert — подписка/отписка не требуют WinUI-хоста
        service.Navigated -= handler;
        fired.Should().Be(0);
    }

    /// <summary>
    /// [Ignore] Реальная навигация Frame.Navigate(Type, object) требует инициализированного
    /// WinUI 3 XAML-движка (Application.Start + STA DispatcherQueue) и package identity.
    /// В MSTest-процессе Frame выбрасывает COMException. Покрытие — ручной прогон
    /// или WinAppDriver (см. WindowManagementTests для полной оценки).
    /// </summary>
    [TestMethod]
    [Ignore("Требует STA WinUI-хоста с инициализированным XAML-движком; Frame.Navigate падает COMException в headless MSTest-процессе")]
    public void NavigateTo_RealFrame_NavigatesToPageType()
    {
        // Arrange
        var service = new NavigationService();
        // service.Frame = new Frame(); // невозможно headless

        // Act
        service.NavigateTo(typeof(object));

        // Assert
        service.Frame.Should().NotBeNull();
    }

    /// <summary>
    /// [Ignore] Проверка Navigated-события после реального Frame.Navigate требует WinUI-хоста.
    /// </summary>
    [TestMethod]
    [Ignore("Требует STA WinUI-хоста: событие Navigated транслируется только от реального WinUI Frame")]
    public void NavigateTo_RealFrame_RaisesNavigatedEventWithPageName()
    {
        Assert.Inconclusive("Декларация сценария для WinAppDriver-прогона");
    }

    /// <summary>
    /// [Ignore] GoBack с реальным стеком навигации Frame требует WinUI-хоста.
    /// </summary>
    [TestMethod]
    [Ignore("Требует STA WinUI-хоста: стек навигации Frame недоступен headless")]
    public void GoBack_WithBackStack_NavigatesBackAndUpdatesCanGoBack()
    {
        Assert.Inconclusive("Декларация сценария для WinAppDriver-прогона");
    }
}
