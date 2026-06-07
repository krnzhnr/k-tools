// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;
using KTools_App.Services.Contracts;
using KTools_App.Services.Implementations;
using KTools_App.ViewModels;
using KTools_App.UI.Pages;
using KTools_App.Core;

namespace KTools_App;

/// <summary>
/// Класс логики (Code-Behind) для главной страницы приложения MainPage.
/// Выполняет исключительно роль связующего звена между представлением и MainViewModel.
/// </summary>
public sealed partial class MainPage : Page
{
    private readonly INavigationService _navigationService;

    /// <summary>
    /// Предоставляет доступ к модели представления главной страницы.
    /// </summary>
    public MainViewModel ViewModel { get; }

    /// <summary>
    /// Инициализирует новый экземпляр MainPage, разрешая зависимости через DI.
    /// </summary>
    public MainPage()
    {
        InitializeComponent();

        ViewModel = App.Services.GetRequiredService<MainViewModel>();
        _navigationService = App.Services.GetRequiredService<INavigationService>();

        // Настраиваем фрейм для службы навигации
        if (_navigationService is NavigationService service)
        {
            service.Frame = ContentFrame;
        }

        // Подписываемся на событие навигации для синхронизации состояния бокового меню
        _navigationService.Navigated += OnNavigationServiceNavigated;
    }

    /// <summary>
    /// Вызывается при загрузке боковой панели навигации.
    /// Делегирует инициализацию (проверку зависимостей) во ViewModel.
    /// </summary>
    private void NavView_Loaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel.InitializeCommand.CanExecute(null))
        {
            ViewModel.InitializeCommand.Execute(null);
        }
    }

    /// <summary>
    /// Обработчик переключения элементов меню боковой панели.
    /// Транслирует UI-событие выбора в команду навигации во ViewModel.
    /// </summary>
    private void NavView_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            if (ViewModel.NavigateCommand.CanExecute("settings"))
            {
                ViewModel.NavigateCommand.Execute("settings");
            }
            return;
        }

        if (args.SelectedItemContainer is NavigationViewItem selectedItem)
        {
            string? tag = selectedItem.Tag?.ToString();
            if (tag != null && ViewModel.NavigateCommand.CanExecute(tag))
            {
                ViewModel.NavigateCommand.Execute(tag);
            }
        }
    }

    /// <summary>
    /// Синхронизирует выбранный пункт в NavigationView при навигации через INavigationService.
    /// Предотвращает рассинхронизацию меню при программных редиректах.
    /// </summary>
    private void OnNavigationServiceNavigated(object? sender, string pageTypeName)
    {
        string? targetTag = pageTypeName switch
        {
            nameof(HomePage) => "home",
            nameof(SettingsPage) => "settings",
            nameof(LogPage) => "logs",
            nameof(DependencySetupPage) => "dependencies",
            nameof(WorkPanel) => GetActiveScriptTag(),
            _ => null
        };

        if (targetTag != null)
        {
            // Находим пункт меню по тегу и делаем его выбранным
            if (targetTag == "settings")
            {
                NavView.SelectedItem = NavView.SettingsItem;
            }
            else
            {
                var item = FindNavItemByTag(NavView.MenuItems, targetTag)
                    ?? FindNavItemByTag(NavView.FooterMenuItems, targetTag);
                if (item != null)
                {
                    NavView.SelectedItem = item;
                }
            }
        }
    }

    /// <summary>
    /// Возвращает навигационный тег для активного в данный момент скрипта.
    /// </summary>
    private string? GetActiveScriptTag()
    {
        if (ContentFrame.Content is WorkPanel workPanel && workPanel.DataContext is WorkPanelViewModel workPanelVm)
        {
            var scriptName = workPanelVm.ActiveScript?.Name;
            if (scriptName != null)
            {
                return ViewModel.GetTagForScriptName(scriptName);
            }
        }
        return null;
    }

    /// <summary>
    /// Рекурсивно ищет элемент NavigationViewItem по строковому тегу.
    /// </summary>
    private NavigationViewItem? FindNavItemByTag(System.Collections.IEnumerable items, string tag)
    {
        foreach (var obj in items)
        {
            if (obj is NavigationViewItem item)
            {
                if (item.Tag?.ToString() == tag)
                {
                    return item;
                }

                var subItem = FindNavItemByTag(item.MenuItems, tag);
                if (subItem != null)
                {
                    return subItem;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Публичный метод для внешней навигации к домашней странице (для совместимости).
    /// </summary>
    public void NavigateToHomeExternally()
    {
        NavView.SelectedItem = NavItemHome;
    }

    /// <summary>
    /// Публичный метод для внешней навигации к странице зависимостей (для совместимости).
    /// </summary>
    public void NavigateToDependenciesExternally()
    {
        NavView.SelectedItem = NavItemDependencies;
    }
}
