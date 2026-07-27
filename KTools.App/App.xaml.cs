using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Windows.AppLifecycle;
using CommunityToolkit.Mvvm.Messaging;
using Polly;
using Polly.Extensions.Http;
using KTools_App.Core;
using KTools_App.Infrastructure;
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
    internal static IServiceProvider Services { get; private set; }
        = null!;

    private static ILogService? _logService;

    /// <summary>
    /// Глобальная ссылка на главное окно приложения.
    /// Необходима для инициализации системных диалогов
    /// (FolderPicker, FilePicker) через COM Interop.
    /// </summary>
    public static Window? CurrentMainWindow { get; private set; }

    /// <summary>
    /// Глобальная ссылка на DispatcherQueue UI-потока.
    /// </summary>
    public static Microsoft.UI.Dispatching.DispatcherQueue? UiDispatcherQueue { get; private set; }

    /// <summary>
    /// Инициализирует singleton-объект приложения
    /// и настраивает DI-контейнер.
    /// </summary>
    public App()
    {
        // === Глобальные перехватчики исключений для диагностики крашей ===
        
        // 1. WinUI 3 UnhandledException — ловит исключения на UI-потоке XAML
        this.UnhandledException += (sender, e) =>
        {
            string report = FormatCrashReport("WinUI3 UnhandledException", e.Exception);
            WriteCrashReport(report);
            try
            {
                _logService?.Fatal(report, "App.UnhandledException");
            }
            catch { }
            e.Handled = true; // Попытка не дать процессу упасть
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
                _logService?.Fatal(report, "AppDomain.UnhandledException");
            }
            catch { }
        };

        // 3. TaskScheduler — ловит исключения из fire-and-forget async Task
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (sender, e) =>
        {
            string report = FormatCrashReport("TaskScheduler.UnobservedTaskException", e.Exception);
            WriteCrashReport(report);
            try
            {
                _logService?.Error(report, "TaskScheduler.UnobservedException");
            }
            catch { }
            e.SetObserved();
        };

        // Регистрируем провайдер кодировок для поддержки чтения файлов
        // в локальных кодировках (например, Windows-1251 / CP1251).
        System.Text.Encoding.RegisterProvider(
            System.Text.CodePagesEncodingProvider.Instance);

        InitializeComponent();
        Services = ConfigureServices();
        _logService = Services.GetRequiredService<ILogService>();
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
        // 1. Попытка записи в LocalAppData (всегда доступно для записи)
        try
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string ktoolsFolder = System.IO.Path.Combine(localAppData, "KTools");
            System.IO.Directory.CreateDirectory(ktoolsFolder);
            string localCrashFile = System.IO.Path.Combine(ktoolsFolder, "crash_report.txt");
            System.IO.File.AppendAllText(
                localCrashFile,
                report + Environment.NewLine,
                System.Text.Encoding.UTF8);
        }
        catch { }

        // 2. Попытка записи рядом с exe
        try
        {
            string exeDir = AppContext.BaseDirectory;
            string crashFile = System.IO.Path.Combine(exeDir, "crash_report.txt");
            System.IO.File.AppendAllText(
                crashFile,
                report + Environment.NewLine,
                System.Text.Encoding.UTF8);
        }
        catch { }
    }

    /// <summary>
    /// Конфигурирует контейнер внедрения зависимостей,
    /// регистрируя все сервисы, синглтоны ядра и ViewModels.
    /// </summary>
    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // 0. Регистрация HttpClient с политиками Polly
        services.AddHttpClient("DefaultClient")
            .AddPolicyHandler(HttpPolicyExtensions
                .HandleTransientHttpError()
                .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt))));

        // 1. Регистрация служб ядра через чистый DI
        services.AddSingleton<ILogService, LogService>();
        services.AddSingleton<IPathManager, PathManager>();
        services.AddSingleton<ISettingsManager, SettingsManager>();
        services.AddSingleton<IDependencyManager, DependencyManager>();
        services.AddSingleton<IScriptRegistry, ScriptRegistry>();

        // Infrastructure-сервисы (Runner'ы)
        services.AddSingleton<IFFmpegRunner, FFmpegRunner>();
        services.AddSingleton<IEac3toRunner, Eac3toRunner>();
        services.AddSingleton<IMediaProbeService, MediaProbeService>();
        services.AddSingleton<IMkvmergeRunner, MkvmergeRunner>();
        services.AddSingleton<DeeRunner>();
        services.AddSingleton<QaacRunner>();
        services.AddSingleton<IAudioWaveformService, AudioWaveformService>();
        services.AddSingleton<IAssParser, AssParser>();

        // Регистрация скриптов обработки медиа
        services.AddTransient<Scripts.MetadataCleanupScript>();
        services.AddTransient<Scripts.VideoEncodingScript>();
        services.AddTransient<Scripts.ContainerConversionScript>();
        services.AddTransient<Scripts.AudioEncodingScript>();
        services.AddTransient<Scripts.AudioDownmixScript>();
        services.AddTransient<Scripts.AudioSpeedScript>();
        services.AddTransient<Scripts.AudioChannelsScript>();
        services.AddTransient<Scripts.AudioTransplantScript>();
        services.AddTransient<Scripts.MkvAssemblyScript>();
        services.AddTransient<Scripts.StreamManagementScript>();
        services.AddTransient<Scripts.StreamReplacementScript>();
        services.AddTransient<Scripts.TrackExtractorScript>();
        services.AddTransient<Scripts.SubtitlesConvertScript>();
        services.AddTransient<Scripts.AudioShiftScript>();
        services.AddTransient<Scripts.SubtitleShiftScript>();
        services.AddTransient<Scripts.MediaDownloaderScript>();

        // 2. Регистрация служб приложения
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IWindowHandleProvider, WindowHandleProvider>();
        services.AddSingleton<IUpdateService, UpdateService>();
        services.AddSingleton<MainWindow>();

        // 3. Регистрация ViewModels
        services.AddSingleton<MainViewModel>();
        services.AddTransient<HomeViewModel>();
        services.AddTransient<WorkPanelViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddTransient<LogViewModel>();
        services.AddTransient<DependencySetupViewModel>();
        services.AddTransient<TrackSelectionViewModel>();
        services.AddTransient<ScriptSettingsViewModel>();

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
                _logService?.Exception(
                    ex,
                    "Не удалось определить, запущено ли приложение с правами администратора",
                    "App");
            }

            _logService?.Info(
                $"=== Запуск приложения K-Tools C# Edition (Права администратора: {(isAdmin ? "Да" : "Нет")}) ===",
                "App");

            string settingsDir = Services.GetRequiredService<IPathManager>().GetSettingsDirectory();
            _logService?.Info(
                $"Конфигурация приложения успешно "
                + $"инициализирована. Папка: {settingsDir}",
                "SettingsManager");

            // При первом запуске автоматически инициализируем
            // все настройки по умолчанию
            _logService?.DebugLog(
                "Выполняется автоматическая инициализация "
                + "настроек по умолчанию...",
                "App");
            _ = Services.GetRequiredService<IScriptRegistry>().Scripts;

            // Автоматически обновляем ключи контекстного меню в реестре, если интеграция включена
            try
            {
                var settingsManager = Services.GetRequiredService<ISettingsManager>();
                if (settingsManager.GetSetting("Shell", "IsContextMenuEnabled", false))
                {
                    var exePath = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(exePath))
                    {
                        var scriptRegistry = Services.GetRequiredService<IScriptRegistry>();
                        var scripts = scriptRegistry.Scripts.Select(s => s.Name).ToList();
                        if (ShellIntegration.NeedsUpdate(exePath, scripts))
                        {
                            ShellIntegration.Register(exePath, scripts);
                            _logService?.Info("Реестр контекстного меню успешно обновлен при запуске", "App");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logService?.Exception(ex, "Не удалось обновить контекстное меню при запуске", "App");
            }

            // Создаём и активируем главное окно
            var window = Services.GetRequiredService<MainWindow>();
            CurrentMainWindow = window;

            // Инициализируем провайдер дескриптора окна
            var handleProvider = Services
                .GetRequiredService<IWindowHandleProvider>();
            if (handleProvider is WindowHandleProvider provider)
            {
                provider.SetMainWindow(window);
            }

            UiDispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

            window.Activate();

            // Обрабатываем собственные параметры запуска
            var ownArgs = Environment.GetCommandLineArgs();
            var (script, filesList) = ParseCommandLineArray(ownArgs);
            if (!string.IsNullOrEmpty(script) || filesList.Count > 0)
            {
                _logService?.Info($"Обработка собственных аргументов запуска. Скрипт: {script ?? "нет"}, файлов: {filesList.Count}", "App");
                WeakReferenceMessenger.Default.Send(new ShellActivationMessage(script, filesList));
            }

            // Обрабатываем накопленные аргументы от других экземпляров из папки PendingArgs
            ProcessPendingArgsFiles();

            // Запускаем отслеживание новых аргументов через FileSystemWatcher
            StartArgsWatcher();
        }
        catch (Exception ex)
        {
            _logService?.Exception(
                ex,
                "Критическая ошибка при инициализации приложения.",
                "App");
            throw;
        }
    }

    /// <summary>
    /// Сканирует директорию PendingArgs и обрабатывает все перенаправленные аргументы.
    /// </summary>
    public static void ProcessPendingArgsFiles()
    {
        if (UiDispatcherQueue == null)
        {
            _logService?.Warn("Пропуск обработки отложенных аргументов: UiDispatcherQueue еще не инициализирован.", "App");
            return;
        }

        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string dir = Path.Combine(appData, "KTools", "PendingArgs");
            if (!Directory.Exists(dir)) return;

            string[] files = Directory.GetFiles(dir, "*.txt");
            foreach (string file in files)
            {
                try
                {
                    if (!File.Exists(file)) continue;

                    string[] args = File.ReadAllLines(file);
                    File.Delete(file);

                    var (script, filesList) = ParseCommandLineArray(args);
                    if (UiDispatcherQueue is Microsoft.UI.Dispatching.DispatcherQueue dispatcherQueue)
                    {
                        dispatcherQueue.TryEnqueue(() =>
                        {
                            _logService?.Info($"Обработка перенаправленных файлов из файла аргументов. Скрипт: {script ?? "нет"}, файлов: {filesList.Count}", "App");
                            BringMainWindowToFront();
                            if (!string.IsNullOrEmpty(script) || filesList.Count > 0)
                            {
                                WeakReferenceMessenger.Default.Send(new ShellActivationMessage(script, filesList));
                            }
                        });
                    }
                }
                catch (IOException)
                {
                    // Файл занят другим процессом, обработаем при следующем вызове
                }
                catch (Exception ex)
                {
                    _logService?.Exception(ex, "Ошибка при обработке файла отложенных аргументов", "App");
                }
            }
        }
        catch (Exception ex)
        {
            _logService?.Exception(ex, "Ошибка при доступе к папке отложенных аргументов", "App");
        }
    }

    private static FileSystemWatcher? _argsWatcher;

    /// <summary>
    /// Запускает FileSystemWatcher для отслеживания новых файлов аргументов в директории PendingArgs.
    /// </summary>
    private static void StartArgsWatcher()
    {
        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string dir = Path.Combine(appData, "KTools", "PendingArgs");
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            _argsWatcher = new FileSystemWatcher(dir, "*.txt")
            {
                EnableRaisingEvents = true
            };

            _argsWatcher.Created += (s, e) =>
            {
                // Небольшая задержка, чтобы дать другому процессу завершить запись в файл
                System.Threading.Thread.Sleep(50);
                ProcessPendingArgsFiles();
            };

            _argsWatcher.Changed += (s, e) =>
            {
                ProcessPendingArgsFiles();
            };
        }
        catch (Exception ex)
        {
            _logService?.Exception(ex, "Ошибка при инициализации FileSystemWatcher для аргументов запуска", "App");
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;

    /// <summary>
    /// Выводит главное окно приложения на передний план, восстанавливая его из свернутого состояния при необходимости.
    /// </summary>
    public static void BringMainWindowToFront()
    {
        if (CurrentMainWindow == null) return;

        try
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(CurrentMainWindow);
            if (hwnd != IntPtr.Zero)
            {
                if (IsIconic(hwnd))
                {
                    ShowWindow(hwnd, SW_RESTORE);
                }
                else
                {
                    ShowWindow(hwnd, SW_SHOW);
                }
                SetForegroundWindow(hwnd);
            }

            CurrentMainWindow.Activate();
        }
        catch (Exception ex)
        {
            _logService?.Error($"Не удалось вывести главное окно на передний план: {ex.Message}", "App");
        }
    }

    /// <summary>
    /// Обрабатывает аргументы запуска приложения (активации) и перенаправляет их через Messenger.
    /// </summary>
    public static void HandleActivation(AppActivationArguments args)
    {
        ProcessPendingArgsFiles();
    }

    private static (string? Script, List<string> Files) ParseActivationArgs(AppActivationArguments args)
    {
        string? script = null;
        var files = new List<string>();

        if (args.Kind == ExtendedActivationKind.Launch)
        {
            if (args.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launchArgs)
            {
                var rawArgs = SplitCommandLine(launchArgs.Arguments);
                (script, files) = ParseCommandLineArray(rawArgs);
            }
        }
        return (script, files);
    }

    public static (string? Script, List<string> Files) ParseCommandLineArray(string[] args)
    {
        string? script = null;
        var files = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--script", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                script = args[i + 1];
                i++;
            }
            else
            {
                string path = args[i];
                path = path.Trim('\"');

                // Игнорируем сам исполняемый файл или сборку приложения (сравниваем имя без расширения, чтобы отфильтровать и .exe, и .dll)
                string fileNameWithoutExt = System.IO.Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                string currentExeNameWithoutExt = System.IO.Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "ktools.app").ToLowerInvariant();
                if (fileNameWithoutExt == currentExeNameWithoutExt || fileNameWithoutExt == "ktools.app" || fileNameWithoutExt == "ktools_app")
                {
                    continue;
                }

                if (System.IO.File.Exists(path) || System.IO.Directory.Exists(path))
                {
                    files.Add(path);
                }
            }
        }
        return (script, files);
    }

    public static string[] SplitCommandLine(string commandLine)
    {
        var args = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine)) return args.ToArray();

        var inQuotes = false;
        var current = new StringBuilder();
        for (int i = 0; i < commandLine.Length; i++)
        {
            char c = commandLine[i];
            if (c == '\"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ' ' && !inQuotes)
            {
                if (current.Length > 0)
                {
                    args.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }
        if (current.Length > 0)
        {
            args.Add(current.ToString());
        }
        return args.ToArray();
    }
}

/// <summary>
/// Сообщение активации через командную строку/Проводник.
/// </summary>
public sealed class ShellActivationMessage
{
    public string? ScriptTag { get; }
    public List<string> Files { get; }

    public ShellActivationMessage(string? scriptTag, List<string> files)
    {
        ScriptTag = scriptTag;
        Files = files;
    }
}
