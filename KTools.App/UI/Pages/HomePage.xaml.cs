// -*- coding: utf-8 -*-
using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Extensions.DependencyInjection;
using KTools_App.Core;
using KTools_App.Services.Contracts;
using KTools_App.ViewModels;

namespace KTools_App.UI.Pages;

/// <summary>
/// Класс логики (Code-Behind) для домашней страницы HomePage.
/// Отображает плитки доступных скриптов и обрабатывает навигацию при клике.
/// </summary>
public sealed partial class HomePage : Page
{
    /// <summary>
    /// Предоставляет доступ к модели представления домашней страницы.
    /// </summary>
    public HomeViewModel ViewModel { get; }

    /// <summary>
    /// Инициализирует новый экземпляр HomePage, разрешая зависимости через DI.
    /// </summary>
    public HomePage()
    {
        ViewModel = App.Services.GetRequiredService<HomeViewModel>();
        InitializeComponent();
    }

    /// <summary>
    /// Обработчик наведения указателя мыши на карточку скрипта.
    /// Добавляет визуальный эффект Fluent-дизайна (изменение непрозрачности).
    /// </summary>
    private void Card_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            border.Opacity = 0.8;
        }
    }

    /// <summary>
    /// Обработчик ухода указателя мыши с карточки скрипта.
    /// Возвращает исходную непрозрачность карточки.
    /// </summary>
    private void Card_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border && 
            border.Tag is ScriptInfo scriptInfo)
        {
            border.Opacity = scriptInfo.CardOpacity;
        }
    }

    /// <summary>
    /// Обработчик клика по карточке скрипта.
    /// Выполняет переход к экрану выполнения скрипта через службу навигации.
    /// </summary>
    private void Card_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is Border border && border.Tag is ScriptInfo scriptInfo)
        {
            var registry = App.Services.GetRequiredService<ScriptRegistry>();
            var script = registry.GetScriptByName(scriptInfo.Name);

            if (script != null)
            {
                var navigationService = App.Services.GetRequiredService<INavigationService>();
                navigationService.NavigateTo(typeof(WorkPanel), script);
            }
        }
    }
}
