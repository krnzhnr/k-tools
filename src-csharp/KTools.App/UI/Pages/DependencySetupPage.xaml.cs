// -*- coding: utf-8 -*-
using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Extensions.DependencyInjection;
using KTools_App.ViewModels;

namespace KTools_App.UI.Pages;

/// <summary>
/// Класс логики (Code-Behind) для страницы настройки компонентов DependencySetupPage.
/// Управляет привязками к DependencySetupViewModel и обрабатывает анимацию карточек при наведении.
/// </summary>
public sealed partial class DependencySetupPage : Page
{
    /// <summary>
    /// Предоставляет доступ к модели представления страницы настройки зависимостей.
    /// </summary>
    public DependencySetupViewModel ViewModel { get; }

    /// <summary>
    /// Инициализирует новый экземпляр DependencySetupPage, разрешая зависимости через DI.
    /// </summary>
    public DependencySetupPage()
    {
        ViewModel = App.Services.GetRequiredService<DependencySetupViewModel>();
        InitializeComponent();
        
        // Подписываемся на изменение состояния ViewModel для синхронизации иконок
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        
        Unloaded += OnPageUnloaded;
    }

    /// <summary>
    /// Вызывается при переходе на эту страницу.
    /// Настраивает начальный вид кнопки обновления в соответствии с состоянием.
    /// </summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        UpdateRefreshIcon(ViewModel.IsHomeState);
    }

    /// <summary>
    /// Синхронизирует системные иконки WinUI в зависимости от логического состояния.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DependencySetupViewModel.IsHomeState))
        {
            UpdateRefreshIcon(ViewModel.IsHomeState);
        }
    }

    /// <summary>
    /// Меняет символ на кнопке "Проверить" в зависимости от того, все ли обязательные компоненты установлены.
    /// </summary>
    private void UpdateRefreshIcon(bool isHomeState)
    {
        RefreshIcon.Symbol = isHomeState ? Symbol.Home : Symbol.Refresh;
    }

    /// <summary>
    /// Обработчик наведения указателя мыши на карточку зависимости.
    /// </summary>
    private void Card_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            border.Opacity = 0.85;
        }
    }

    /// <summary>
    /// Обработчик ухода указателя мыши с карточки зависимости.
    /// </summary>
    private void Card_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            border.Opacity = 1.0;
        }
    }

    /// <summary>
    /// Вызывается при выгрузке страницы из визуального дерева.
    /// Освобождает системные подписки во ViewModel.
    /// </summary>
    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.Cleanup();
    }
}
