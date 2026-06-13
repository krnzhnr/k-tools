// -*- coding: utf-8 -*-
using System;
using Microsoft.UI.Xaml.Controls;
using KTools_App.Services.Contracts;

namespace KTools_App.Services.Implementations;

/// <summary>
/// Реализация службы навигации на основе WinUI 3 Frame.
/// Управляет переходами между страницами и предоставляет событие Navigated
/// для синхронизации состояния NavigationView в MainPage.
/// </summary>
public sealed class NavigationService : INavigationService
{
    private Frame? _frame;

    /// <summary>
    /// Устанавливает или возвращает корневой фрейм навигации.
    /// При установке автоматически подписывается на событие Navigated фрейма.
    /// </summary>
    public Frame? Frame
    {
        get => _frame;
        set
        {
            if (_frame != null)
            {
                _frame.Navigated -= OnFrameNavigated;
            }

            _frame = value;

            if (_frame != null)
            {
                _frame.Navigated += OnFrameNavigated;
            }
        }
    }

    /// <inheritdoc />
    public bool CanGoBack => _frame?.CanGoBack ?? false;

    /// <inheritdoc />
    public event EventHandler<string>? Navigated;

    /// <inheritdoc />
    public void NavigateTo(Type pageType, object? parameter = null)
    {
        if (_frame == null)
        {
            throw new InvalidOperationException(
                "Фрейм навигации не инициализирован. "
                + "Установите свойство Frame перед вызовом NavigateTo.");
        }

        _frame.Navigate(pageType, parameter);
    }

    /// <inheritdoc />
    public void GoBack()
    {
        if (_frame?.CanGoBack == true)
        {
            _frame.GoBack();
        }
    }

    /// <summary>
    /// Внутренний обработчик события навигации фрейма.
    /// Транслирует событие WinUI Navigated в абстрактный интерфейсный контракт.
    /// </summary>
    private void OnFrameNavigated(
        object sender,
        Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        Navigated?.Invoke(
            this,
            e.SourcePageType.Name);
    }
}
