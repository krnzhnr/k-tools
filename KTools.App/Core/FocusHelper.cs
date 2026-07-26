// -*- coding: utf-8 -*-
using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

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
    /// Безопасно снимает фокус с текущего активного элемента ввода текста, перемещая его на нейтральный корневой контейнер.
    /// </summary>
    /// <param name="xamlRoot">Корневой элемент XAML для получения сфокусированного элемента.</param>
    /// <param name="targetRoot">Нейтральный элемент интерфейса (например, корневой Grid), на который будет передан фокус.</param>
    /// <returns>True, если фокус был успешно снят.</returns>
    public static bool ClearFocus(XamlRoot? xamlRoot, UIElement? targetRoot)
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
            System.Diagnostics.Debug.WriteLine($"[FocusHelper] Ошибка при снятии фокуса: {ex.Message}");
        }

        return false;
    }
}
