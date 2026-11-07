// -*- coding: utf-8 -*-
using System;

using KTools_App.Core;
using KTools_App.Services.Contracts;
using KTools_App.ViewModels;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
    /// Обработчик клика по карточке скрипта.
    /// Выполняет переход к экрану выполнения скрипта через службу навигации.
    /// </summary>
    private void Card_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is ScriptInfo scriptInfo)
        {
            ViewModel.NavigateToScriptCommand.Execute(scriptInfo);
        }
    }
}
