// -*- coding: utf-8 -*-
using System;

using KTools_App.Diagnostics;
using KTools_App.Services.Contracts;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace KTools_App.Core;

/// <summary>
/// Статический класс-помощник для управления фокусом ввода в интерфейсе приложения.
/// Обеспечивает универсальное снятие фокуса с текстовых полей ввода при кликах по пустой области или нажатии Enter.
/// </summary>
public static class FocusHelper
{
    /// <summary>
    /// Проверяет, является ли указанный элемент элементом ввода текста.
    /// </summary>
    /// <param name="element">Элемент интерфейса для проверки.</param>
    /// <returns>True, если элемент является текстовым полем ввода.</returns>
    public static bool IsTextInputElement(UIElement? element)
    {
        if (element == null) return false;

        if (element is TextBox textBox)
        {
            // Для многострочных текстовых полей Enter должен вставлять новую строку
            return !textBox.AcceptsReturn;
        }

        return element is NumberBox or AutoSuggestBox or PasswordBox;
    }

    /// <summary>
    /// Проверяет, находится ли элемент внутри текстового поля ввода, включая
    /// внутренние элементы его шаблона. Обход визуального дерева вверх обязателен:
    /// OriginalSource нажатия указателя — это глубокий внутренний элемент шаблона
    /// TextBox (граница, скроллер), а не сам TextBox, поэтому прямая проверка
    /// OriginalSource всегда давала false и фокус отбирался у поля, в которое кликнули.
    /// </summary>
    /// <param name="source">Элемент интерфейса для проверки (обычно OriginalSource события указателя).</param>
    /// <returns>True, если сам элемент или любой его предок — текстовое поле ввода.</returns>
    public static bool IsInsideTextInputElement(DependencyObject? source)
    {
        DependencyObject? current = source;
        while (current != null)
        {
            if (current is UIElement element && IsTextInputElement(element))
            {
                return true;
            }

            DependencyObject? parent;
            try
            {
                parent = VisualTreeHelper.GetParent(current);
            }
            catch (Exception)
            {
                return false;
            }

            current = parent;
        }

        return false;
    }

    /// <summary>
    /// Безопасно снимает фокус с текущего активного элемента ввода текста, перемещая его на нейтральный корневой контейнер.
    /// </summary>
    /// <param name="xamlRoot">Корневой элемент XAML для получения сфокусированного элемента.</param>
    /// <param name="targetRoot">Нейтральный элемент интерфейса (например, корневой Grid), на который будет передан фокус.</param>
    /// <param name="logService">Журнал приложения для диагностики сбоя снятия фокуса.</param>
    /// <returns>True, если фокус был успешно снят.</returns>
    public static bool ClearFocus(XamlRoot? xamlRoot, UIElement? targetRoot, ILogService? logService = null)
    {
        if (xamlRoot == null || targetRoot == null) return false;

        try
        {
            var focused = FocusManager.GetFocusedElement(xamlRoot) as UIElement;
            if (IsTextInputElement(focused))
            {
                bool isTabStop = targetRoot.IsTabStop;
                targetRoot.IsTabStop = true;
                bool focusResult = targetRoot.Focus(FocusState.Programmatic);
                targetRoot.IsTabStop = isTabStop;
                return focusResult;
            }
        }
        catch (Exception ex)
        {
            logService?.Write(
                "ui.focus.clear_failed",
                LogLevel.Warning,
                LogStatus.Failed,
                "Фокус с поля ввода не снят",
                ex,
                nameof(FocusHelper),
                properties: LogProps.Create("ErrorCode", "FOCUS_CLEAR_FAILED").With("Control", "RootGrid"));
        }

        return false;
    }
}
