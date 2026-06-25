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
        // Регистрируем провайдер кодировок для поддержки чтения файлов
        // в локальных кодировках (например, Windows-1251 / CP1251).
        System.Text.Encoding.RegisterProvider(
            System.Text.CodePagesEncodingProvider.Instance);

        InitializeComponent();
        Services = ConfigureServices();

        // === Глобальные перехватчики исключений для диагностики крашей в publish-сборке ===

        // 1. WinUI 3 UnhandledException — ловит исключения на UI-потоке XAML
        this.UnhandledException += (sender, e) =>
        {
            string report = FormatCrashReport("WinUI3 UnhandledException", e.Exception);
            WriteCrashReport(report);
            LogService.Instance.Fatal(report, "App.UnhandledException");
            e.Handled = true; // Попытка не дать процессу упасть до записи
        };

        // 2. .NET AppDomain — ловит необработанные managed-исключения
        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            string report = FormatCrashReport(
                $"AppDomain.UnhandledException (IsTerminating={e.IsTerminating})",
                ex);
            WriteCrashReport(report);
            try
            {
                LogService.Instance.Fatal(report, "AppDomain.UnhandledException");
            }
            catch
            {
                // LogService может быть недоступен при фатальном сбое
            }
        };

        // 3. TaskScheduler — ловит исключения из fire-and-forget async Task
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (sender, e) =>
        {
            string report = FormatCrashReport("TaskScheduler.UnobservedTaskException", e.Exception);
            WriteCrashReport(report);
            LogService.Instance.Error(report, "TaskScheduler.UnobservedException");
            e.SetObserved();
        };
    }

    /// <summary>
    /// Формирует полный отчёт о крахе с информацией об исключении,
    /// внутренних исключениях и стеке вызовов для диагностики.
    /// </summary>
    private static string FormatCrashReport(string source, Exception? ex)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"=== АВАРИЙНЫЙ ОТЧЁТ: {source} ===");
        sb.AppendLine($"Время: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
        sb.AppendLine($"Версия: {typeof(App).Assembly.GetName().Version}");

        if (ex == null)
        {
            sb.AppendLine("Исключение: (null — объект исключения отсутствует)");
        }
        else
        {
            var current = ex;
            int depth = 0;
            while (current != null)
            {
                string prefix = depth == 0 ? "Исключение" : $"Внутреннее исключение [{depth}]";
                sb.AppendLine($"{prefix}: {current.GetType().FullName}");
                sb.AppendLine($"  Сообщение: {current.Message}");
                sb.AppendLine($"  Источник: {current.Source}");
                sb.AppendLine($"  Стек вызовов:");
                sb.AppendLine(current.StackTrace ?? "  (стек отсутствует)");
                current = current.InnerException;
                depth++;
            }

            // AggregateException — развернуть все внутренние
            if (ex is AggregateException aggEx)
            {
                sb.AppendLine("--- Развёрнутые внутренние исключения AggregateException ---");
                foreach (var inner in aggEx.Flatten().InnerExceptions)
                {
                    sb.AppendLine($"  Тип: {inner.GetType().FullName}");
                    sb.AppendLine($"  Сообщение: {inner.Message}");
                    sb.AppendLine($"  Стек: {inner.StackTrace}");
                }
            }
        }

        sb.AppendLine("=== КОНЕЦ АВАРИЙНОГО ОТЧЁТА ===");
        return sb.ToString();
    }

    /// <summary>
    /// Записывает аварийный отчёт в файл crash_report.txt рядом с exe приложения.
    /// Использует прямой File.AppendAllText без зависимости от LogService,
    /// чтобы отчёт был доступен даже при сбое логгера.
    /// </summary>
    private static void WriteCrashReport(string report)
    {
        try
        {
            string exeDir = AppContext.BaseDirectory;
            string crashFile = System.IO.Path.Combine(exeDir, "crash_report.txt");
            System.IO.File.AppendAllText(
                crashFile,
                report + Environment.NewLine,
                System.Text.Encoding.UTF8);
        }
        catch
        {
            // Если запись невозможна (например, нет прав на папку), просто проглатываем
        }
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
        services.AddSingleton<IUpdateService, UpdateService>();

        // 3. Регистрация ViewModels
        services.AddSingleton<MainViewModel>();
        services.AddTransient<HomeViewModel>();
        services.AddTransient<WorkPanelViewModel>();
        services.AddSingleton<SettingsViewModel>();
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
        try
        {
            // Инициализируем логирование при старте приложения
            bool isAdmin = false;
            try
            {
                using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    var principal = new System.Security.Principal.WindowsPrincipal(identity);
                    isAdmin = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
            }
            catch (Exception ex)
            {
                LogService.Instance.Exception(
                    ex,
                    "Не удалось определить, запущено ли приложение с правами администратора",
                    "App");
            }

            LogService.Instance.Info(
                $"=== Запуск приложения K-Tools C# Edition (Права администратора: {(isAdmin ? "Да" : "Нет")}) ===",
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
        catch (Exception ex)
        {
            LogService.Instance.Exception(
                ex,
                "Критическая ошибка при инициализации приложения.",
                "App");
            throw;
        }
    }
}
