// -*- coding: utf-8 -*-
using System;
using System.IO;
using System.Reflection;
using FluentAssertions;
using Microsoft.UI.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KTools_App.Tests.Integration;

/// <summary>
/// Юнит-тесты навигации боковой кнопкой мыши (XButton1 → «Назад», #40).
/// Чистая матрица решений ShouldNavigateBackOnPointerUpdate вызывается через
/// reflection без WinUI-хоста, глобальная подписка проверяется по исходнику.
/// Все комментарии выполнены на русском языке.
/// </summary>
[TestClass]
public class MainPagePointerTests
{
    private static bool InvokeDecision(PointerUpdateKind kind, bool canGoBack)
    {
        var method = typeof(MainPage).GetMethod(
            "ShouldNavigateBackOnPointerUpdate",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull(
            "чистое решение о навигации по кнопкам указателя обязано существовать в MainPage");
        return (bool)method!.Invoke(null, new object[] { kind, canGoBack })!;
    }

    [TestMethod]
    public void ShouldNavigateBackOnPointerUpdate_XButton1PressedAndCanGoBack_ReturnsTrue()
    {
        InvokeDecision(PointerUpdateKind.XButton1Pressed, true).Should().BeTrue(
            "нажатие боковой кнопки «назад» при непустой истории обязано запускать возврат");
    }

    [TestMethod]
    public void ShouldNavigateBackOnPointerUpdate_XButton1PressedAndCannotGoBack_ReturnsFalse()
    {
        InvokeDecision(PointerUpdateKind.XButton1Pressed, false).Should().BeFalse(
            "без истории навигации возвращаться некуда даже по боковой кнопке");
    }

    [TestMethod]
    public void ShouldNavigateBackOnPointerUpdate_XButton1Released_ReturnsFalse()
    {
        InvokeDecision(PointerUpdateKind.XButton1Released, true).Should().BeFalse(
            "отпускание боковой кнопки не должно дублировать возврат");
    }

    [TestMethod]
    public void ShouldNavigateBackOnPointerUpdate_LeftButtonPressed_ReturnsFalse()
    {
        InvokeDecision(PointerUpdateKind.LeftButtonPressed, true).Should().BeFalse(
            "обычный клик навигацию не запускает, он снимает фокус с полей ввода");
    }

    [TestMethod]
    public void ShouldNavigateBackOnPointerUpdate_XButton2Pressed_ReturnsFalse()
    {
        InvokeDecision(PointerUpdateKind.XButton2Pressed, true).Should().BeFalse(
            "кнопка «вперёд» не поддерживается: в INavigationService нет стека вперёд");
    }

    [TestMethod]
    public void RootGrid_PointerPressed_SubscribedViaAddHandlerWithHandledEventsToo()
    {
        // Arrange — кнопки и поля помечают PointerPressed как handled ради своего Click,
        // поэтому глобальный обработчик обязан подписываться с handledEventsToo,
        // иначе «назад» срабатывал бы только на пустых местах (регрессия из #40)
        string dir = AppContext.BaseDirectory;
        string? sourceFile = null;
        for (int i = 0; i < 8; i++)
        {
            string candidate = Path.Combine(dir, "KTools.App", "MainPage.xaml.cs");
            if (File.Exists(candidate))
            {
                sourceFile = candidate;
                break;
            }
            dir = Path.GetDirectoryName(dir)!;
        }
        sourceFile.Should().NotBeNull("корень проекта KTools.App обязан находиться");
        string source = File.ReadAllText(sourceFile!);

        // Act / Assert
        source.Should().Contain("PointerPressedEvent",
            "подписка обязана идти через AddHandler на событие PointerPressed");
        source.Should().Contain("handledEventsToo: true",
            "подписка обязана ловить нажатия поверх дочерних контролов");
    }
}
