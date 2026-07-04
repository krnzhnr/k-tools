// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
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
    private readonly ILogService _logService;
    private string? _pendingScriptTag;
    private bool _isSyncingNavigation;

    /// <summary>
    /// Предоставляет доступ к модели представления главной страницы.
    /// </summary>
    public MainViewModel ViewModel { get; }

    /// <summary>
    /// Инициализирует новый экземпляр MainPage, разрешая зависимости через DI.
    /// </summary>
    public MainPage()
    {
        ViewModel = App.Services.GetRequiredService<MainViewModel>();
        _navigationService = App.Services.GetRequiredService<INavigationService>();
        _logService = App.Services.GetRequiredService<ILogService>();

        InitializeComponent();

        // Настраиваем фрейм для службы навигации
        if (_navigationService is NavigationService service)
        {
            service.Frame = ContentFrame;
        }

        // Подписываемся на событие навигации для синхронизации состояния бокового меню
        _navigationService.Navigated += OnNavigationServiceNavigated;

        // Подписываемся на сообщение об изменении активного скрипта для синхронизации бокового меню
        var messenger = CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default;
        messenger.Register<MainPage, ActiveScriptChangedMessage>(
            this,
            (r, m) => r.OnActiveScriptChanged(m.Script));
    }

    /// <summary>
    /// Вызывается при загрузке боковой панели навигации.
    /// Переводит стандартную кнопку настроек и инициализирует ViewModel.
    /// </summary>
    private void NavView_Loaded(object sender, RoutedEventArgs e)
    {
        // Инициализируем XamlRoot в DialogService перед выполнением Initialize
        if (App.Services.GetService<IDialogService>() is DialogService dialogService)
        {
            dialogService.XamlRoot = this.XamlRoot;
            _logService.DebugLog("Инициализирован XamlRoot для IDialogService в MainPage.", "MainPage");
        }

        if (NavView.SettingsItem is NavigationViewItem settingsItem)
        {
            settingsItem.Content = "Настройки";
            var autoName = "Настройки";
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                settingsItem, autoName);
            ToolTipService.SetToolTip(settingsItem, "Настройки");
            _logService.Info(
                "Выполнена локализация кнопки настроек на русский язык",
                "MainPage");
        }

        if (ViewModel.InitializeCommand.CanExecute(null))
        {
            ViewModel.InitializeCommand.Execute(null);
        }

        // Устанавливаем начальное состояние видимости разделителя на основе состояния панели
        HomeSeparator.Visibility = NavView.IsPaneOpen ? Visibility.Collapsed : Visibility.Visible;
        _logService.Info(
            $"[MainPage] Инициализация видимости разделителя: {(NavView.IsPaneOpen ? "Скрыт" : "Показан")}",
            "MainPage");
    }

    /// <summary>
    /// Обработчик переключения элементов меню боковой панели.
    /// Транслирует UI-событие выбора в команду навигации во ViewModel.
    /// </summary>
    private void NavView_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (_isSyncingNavigation) return;

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
    private void OnNavigationServiceNavigated(
        object? sender, 
        string pageTypeName)
    {
        _logService.Info(
            $"[MainPage] Получено событие навигации на страницу: '{pageTypeName}'",
            "MainPage");

        string? targetTag = pageTypeName switch
        {
            nameof(HomePage) => "home",
            nameof(SettingsPage) => "settings",
            nameof(LogPage) => "logs",
            nameof(DependencySetupPage) => "dependencies",
            nameof(WorkPanel) => GetActiveScriptTag(),
            _ => null
        };

        _logService.Info(
            $"[MainPage] Вычисленный тег навигации для '{pageTypeName}': '{targetTag ?? "null"}'",
            "MainPage");

        if (targetTag != null)
        {
            SyncNavigationSelection(targetTag);
            UpdateHeader(targetTag);
        }
    }

    /// <summary>
    /// Возвращает навигационный тег для активного в данный момент скрипта.
    /// </summary>
    private string? GetActiveScriptTag()
    {
        if (ContentFrame.Content is WorkPanel workPanel)
        {
            var script = workPanel.ViewModel.ActiveScript;
            if (script != null)
            {
                string? tag = ViewModel.GetTagForScriptName(script.Name);
                _logService.Info(
                    $"[MainPage] Активный скрипт в WorkPanel: '{script.Name}', тег: '{tag ?? "null"}'",
                    "MainPage");
                return tag;
            }
            else
            {
                _logService.Warn(
                    "[MainPage] В WorkPanel отсутствует активный скрипт!",
                    "MainPage");
            }
        }
        else
        {
            _logService.Warn(
                $"[MainPage] Контент фрейма не является WorkPanel! Тип: '{ContentFrame.Content?.GetType().Name ?? "null"}'",
                "MainPage");
        }
        return null;
    }

    /// <summary>
    /// Обновляет текстовый заголовок и подзаголовок в верхней панели.
    /// </summary>
    private void UpdateHeader(string targetTag)
    {
        if (targetTag == "home")
        {
            ViewModel.HeaderTitle = "K-Tools";
            ViewModel.HeaderSubtitle = 
                "Ваш персональный набор инструментов для обработки медиа";
        }
        else if (targetTag == "settings")
        {
            ViewModel.HeaderTitle = "Настройки";
            ViewModel.HeaderSubtitle = 
                "Общие параметры и конфигурация приложения";
        }
        else if (targetTag == "logs")
        {
            ViewModel.HeaderTitle = "Логи";
            ViewModel.HeaderSubtitle = 
                "Просмотр журналов выполнения и сообщений в реальном времени";
        }
        else if (targetTag == "dependencies")
        {
            ViewModel.HeaderTitle = "Компоненты";
            ViewModel.HeaderSubtitle = 
                "Установка, обновление и удаление внешних бинарных утилит";
        }
        else if (targetTag.StartsWith("script:"))
        {
            if (ContentFrame.Content is WorkPanel workPanel)
            {
                var script = workPanel.ViewModel.ActiveScript;
                if (script != null)
                {
                    ViewModel.HeaderTitle = script.Name;
                    ViewModel.HeaderSubtitle = script.Description;
                }
            }
        }
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
                    if (NavView.IsPaneOpen)
                    {
                        item.IsExpanded = true; // Раскрываем только если панель открыта
                    }
                    return subItem;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Вызывается при получении сообщения об инициализации активного скрипта.
    /// Выполняет синхронизацию выделенного элемента в NavigationView.
    /// </summary>
    private void OnActiveScriptChanged(AbstractScript script)
    {
        string? targetTag = ViewModel.GetTagForScriptName(script.Name);
        if (targetTag != null)
        {
            SyncNavigationSelection(targetTag);
            _logService.Info(
                $"[MainPage] По сообщению синхронизировано выделение для " +
                $"тега '{targetTag}'",
                "MainPage");
        }
    }

    /// <summary>
    /// Синхронизирует выделенный элемент в NavigationView с указанным тегом.
    /// Учитывает состояние открытости панели для предотвращения
    /// нежелательных Flyout.
    /// </summary>
    private void SyncNavigationSelection(string? targetTag)
    {
        if (targetTag == null) return;

        // Если это тег скрипта, сохраняем его как ожидающий
        if (targetTag.StartsWith("script:"))
        {
            _pendingScriptTag = targetTag;
        }
        else
        {
            _pendingScriptTag = null;
        }

        _isSyncingNavigation = true;
        try
        {
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
                    var parentItem = FindParentItemForChildTag(
                        NavView.MenuItems, 
                        targetTag);

                    if (parentItem != null && NavView.IsPaneOpen)
                    {
                        parentItem.IsExpanded = true;
                    }

                    NavView.SelectedItem = item;
                }
            }
        }
        finally
        {
            _isSyncingNavigation = false;
        }
    }

    /// <summary>
    /// Рекурсивно находит родительский элемент NavigationViewItem 
    /// для дочернего тега.
    /// </summary>
    private NavigationViewItem? FindParentItemForChildTag(
        System.Collections.IEnumerable items, 
        string childTag)
    {
        foreach (var obj in items)
        {
            if (obj is NavigationViewItem item)
            {
                // Проверяем непосредственных детей
                foreach (var subObj in item.MenuItems)
                {
                    if (subObj is NavigationViewItem subItem && 
                        subItem.Tag?.ToString() == childTag)
                    {
                        return item;
                    }
                }

                // Рекурсивно проверяем более глубокие уровни
                var parent = FindParentItemForChildTag(
                    item.MenuItems, 
                    childTag);
                if (parent != null)
                {
                    return parent;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Вызывается перед началом открытия боковой панели.
    /// Синхронизирует выделение и раскрывает категорию, если был выбран скрипт.
    /// </summary>
    private void NavView_PaneOpened(NavigationView sender, object args)
    {
        if (_pendingScriptTag != null)
        {
            var parentItem = FindParentItemForChildTag(
                NavView.MenuItems, 
                _pendingScriptTag);

            if (parentItem != null)
            {
                _isSyncingNavigation = true;
                try
                {
                    parentItem.IsExpanded = true;
                }
                finally
                {
                    _isSyncingNavigation = false;
                }

                _logService.Info(
                    $"[MainPage] При открытии панели раскрыта родительская " +
                    $"категория: '{parentItem.Content}' для тега " +
                    $"'{_pendingScriptTag}'",
                    "MainPage");
            }
        }
    }

    /// <summary>
    /// Вызывается после полного закрытия боковой панели.
    /// </summary>
    private void NavView_PaneClosed(NavigationView sender, object args)
    {
        // В свернутом режиме не требуется программно менять SelectedItem, 
        // так как NavigationView автоматически проецирует выделение дочернего
        // элемента на родительскую категорию.
    }

    /// <summary>
    /// Вызывается в самом начале процесса открытия боковой панели (до начала анимации).
    /// Мгновенно скрывает разделитель под иконкой домашней страницы.
    /// </summary>
    private void NavView_PaneOpening(NavigationView sender, object args)
    {
        HomeSeparator.Visibility = Visibility.Collapsed;
        _logService.Info(
            "[MainPage] Панель начинает открываться. Разделитель скрыт мгновенно.",
            "MainPage");
    }

    /// <summary>
    /// Вызывается в самом начале процесса закрытия боковой панели (до начала анимации).
    /// Мгновенно показывает разделитель под иконкой домашней страницы.
    /// </summary>
    private void NavView_PaneClosing(NavigationView sender, object args)
    {
        HomeSeparator.Visibility = Visibility.Visible;
        _logService.Info(
            "[MainPage] Панель начинает закрываться. Разделитель показан мгновенно.",
            "MainPage");
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

    /// <summary>
    /// Обработчик события закрытия InfoBar об обновлении на главном экране.
    /// Перенаправляет вызов во ViewModel для обновления состояния.
    /// </summary>
    private void MainUpdateInfoBar_CloseButtonClick(InfoBar sender, object args)
    {
        if (ViewModel.CloseUpdateBannerCommand.CanExecute(null))
        {
            ViewModel.CloseUpdateBannerCommand.Execute(null);
        }
    }
}
