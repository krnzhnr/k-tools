// -*- coding: utf-8 -*-
using System;
using Microsoft.UI.Xaml;
using Microsoft.Extensions.DependencyInjection;
using KTools_App.Core;
using KTools_App.Services.Contracts;
using KTools_App.Services.Implementations;
using KTools_App.ViewModels;

namespace KTools_App;

/// <summary>
/// Точка входа приложения.
/// Конфигурирует DI-контейнер для внедрения зависимостей
/// и инициализирует главное окно приложения.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Глобальный провайдер служб (DI-контейнер).
    /// Используется для получения зависимостей во Views.
    /// </summary>
    public static IServiceProvider Services { get; private set; }
        = null!;

    /// <summary>
    /// Глобальная ссылка на главное окно приложения.
    /// Необходима для инициализации системных диалогов
    /// (FolderPicker, FilePicker) через COM Interop.
    /// </summary>
    public static Window? CurrentMainWindow { get; private set; }

    /// <summary>
    /// Инициализирует singleton-объект приложения
    /// и настраивает DI-контейнер.
    /// </summary>
    public App()
    {
        InitializeComponent();
        Services = ConfigureServices();
    }

    /// <summary>
    /// Конфигурирует контейнер внедрения зависимостей,
    /// регистрируя все сервисы, синглтоны ядра и ViewModels.
    /// </summary>
    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // 1. Регистрация синглтонов ядра (существующие Lazy<T>)
        services.AddSingleton(LogService.Instance);
        services.AddSingleton(DependencyManager.Instance);
        services.AddSingleton(SettingsManager.Instance);
        services.AddSingleton(ScriptRegistry.Instance);

        // 2. Регистрация служб приложения
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IWindowHandleProvider, WindowHandleProvider>();

        // 3. Регистрация ViewModels
        services.AddSingleton<MainViewModel>();
        services.AddTransient<HomeViewModel>();
        services.AddTransient<WorkPanelViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<LogViewModel>();
        services.AddTransient<DependencySetupViewModel>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Вызывается при запуске приложения.
    /// Инициализирует логирование, настройки и главное окно.
    /// </summary>
    protected override void OnLaunched(
        LaunchActivatedEventArgs args)
    {
        // Инициализируем логирование при старте приложения
        LogService.Instance.Info(
            "=== Запуск приложения K-Tools C# Edition ===",
            "App");

        string settingsDir = PathManager.GetSettingsDirectory();
        LogService.Instance.Info(
            $"Конфигурация приложения успешно "
            + $"инициализирована. Папка: {settingsDir}",
            "SettingsManager");

        // При первом запуске автоматически инициализируем
        // все настройки по умолчанию
        LogService.Instance.DebugLog(
            "Выполняется автоматическая инициализация "
            + "настроек по умолчанию...",
            "App");
        _ = ScriptRegistry.Instance.Scripts;

        // Создаём и активируем главное окно
        var window = new MainWindow();
        CurrentMainWindow = window;

        // Инициализируем провайдер дескриптора окна
        var handleProvider = Services
            .GetRequiredService<IWindowHandleProvider>();
        if (handleProvider is WindowHandleProvider provider)
        {
            provider.SetMainWindow(window);
        }

        window.Activate();
    }
}
