// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KTools_App.Core;
using KTools_App.Services.Contracts;
using KTools_App.UI.Pages;
using CommunityToolkit.Mvvm.Messaging;

namespace KTools_App.ViewModels;

/// <summary>
/// Модель представления главной страницы приложения (MainPage).
/// Управляет навигацией между экранами, заголовками и видимостью вкладки логов.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly INavigationService _navigationService;
    private readonly ScriptRegistry _scriptRegistry;
    private readonly DependencyManager _dependencyManager;
    private readonly SettingsManager _settingsManager;
    private readonly LogService _logService;
    private readonly SettingsViewModel _settingsViewModel;
    private readonly IDialogService _dialogService;
    private readonly IUpdateService _updateService;

    /// <summary>
    /// Словарь для быстрого поиска скрипта по тегу навигации.
    /// </summary>
    private readonly Dictionary<string, ScriptInfo> _scriptsByTag = new();

    /// <summary>
    /// Заголовок в верхней панели приложения.
    /// </summary>
    [ObservableProperty]
    public partial string HeaderTitle { get; set; } = "K-Tools";

    /// <summary>
    /// Подзаголовок в верхней панели приложения.
    /// </summary>
    [ObservableProperty]
    public partial string HeaderSubtitle { get; set; } = "Ваш персональный набор инструментов для обработки медиа";

    /// <summary>
    /// Флаг видимости вкладки системных журналов в интерфейсе.
    /// </summary>
    [ObservableProperty]
    public partial bool IsLogsTabVisible { get; set; }

    /// <summary>
    /// Инициализирует ViewModel главной страницы с внедрением зависимостей.
    /// </summary>
    public MainViewModel(
        INavigationService navigationService,
        ScriptRegistry scriptRegistry,
        DependencyManager dependencyManager,
        SettingsManager settingsManager,
        LogService logService,
        SettingsViewModel settingsViewModel,
        IDialogService dialogService,
        IUpdateService updateService)
    {
        _navigationService = navigationService;
        _scriptRegistry = scriptRegistry;
        _dependencyManager = dependencyManager;
        _settingsManager = settingsManager;
        _logService = logService;
        _settingsViewModel = settingsViewModel;
        _dialogService = dialogService;
        _updateService = updateService;

        InitializeScripts();
        UpdateLogsTabVisibility();

        // Подписываемся на сообщение об изменении видимости вкладки логов
        CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Register<MainViewModel, LogsTabVisibilityChangedMessage>(
            this,
            (r, m) => r.UpdateLogsTabVisibility());

        // Подписываемся на сообщение об изменении активного скрипта
        var messenger = CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default;
        messenger.Register<MainViewModel, ActiveScriptChangedMessage>(
            this,
            (r, m) => {
                r.HeaderTitle = m.Script.Name;
                r.HeaderSubtitle = m.Script.Description;
            });
    }

    /// <summary>
    /// Инициализация и маппинг списка скриптов на теги навигации.
    /// </summary>
    private void InitializeScripts()
    {
        var scripts = new List<ScriptInfo>
        {
            new ScriptInfo { Name = "Кодирование видео", Category = "Видео", IconName = "video", Description = "Кодирование видео: изменение формата, вшивание субтитров, фильтрация тегов и настройка звука" },
            new ScriptInfo { Name = "Конвертация контейнера", Category = "Видео", IconName = "forward", Description = "Перемещение видео/аудио потоков в другой контейнер без перекодирования" },
            new ScriptInfo { Name = "Очистка метаданных", Category = "Видео", IconName = "delete", Description = "Удаление метаданных из видеофайлов с сохранением оригинального качества" },
            new ScriptInfo { Name = "Кодирование аудио", Category = "Аудио", IconName = "music", Description = "Перекодирование аудио в QAAC, AAC, FLAC, WAV, E-AC3, AC3 и др. с настройкой качества" },
            new ScriptInfo { Name = "Даунмикс в Stereo", Category = "Аудио", IconName = "volume2", Description = "Даунмикс 5.1/7.1 в Stereo 2.0 (DDP/DD) через Dolby Encoding Engine" },
            new ScriptInfo { Name = "Изменение скорости аудио", Category = "Аудио", IconName = "sync", Description = "Изменение скорости/тона аудио (PAL ↔ NTSC) с помощью eac3to." },
            new ScriptInfo { Name = "Разделение каналов", Category = "Аудио", IconName = "map", Description = "Разделение многоканального аудио на моно-WAV файлы с опциональной склейкой в стереопары" },
            new ScriptInfo { Name = "Сборка MKV", Category = "Контейнеры", IconName = "add", Description = "Сборка контейнера MKV из отдельных потоков видео, аудио и субтитров с сопоставлением по имени" },
            new ScriptInfo { Name = "Управление потоками", Category = "Контейнеры", IconName = "list", Description = "Удаление или сохранение выбранных дорожек (видео, аудио, субтитры) в MKV и MP4 файлах." },
            new ScriptInfo { Name = "Замена потоков", Category = "Контейнеры", IconName = "switch", Description = "Заменяет дорожки в MKV/MP4 на внешние файлы (видео, аудио, субтитры)." },
            new ScriptInfo { Name = "Разборка контейнера", Category = "Контейнеры", IconName = "download", Description = "Массовое извлечение потоков из контейнера с авто-именованием." },
            new ScriptInfo { Name = "ASS/SRT → VTT", Category = "Субтитры", IconName = "font", Description = "Конвертация субтитров ASS/SSA/SRT в WebVTT с фильтрацией по актёрам и очисткой тегов." }
        };

        _scriptsByTag.Add("script:video_encoding", scripts[0]);
        _scriptsByTag.Add("script:container_conversion", scripts[1]);
        _scriptsByTag.Add("script:metadata_cleanup", scripts[2]);
        _scriptsByTag.Add("script:audio_encoding", scripts[3]);
        _scriptsByTag.Add("script:audio_downmix", scripts[4]);
        _scriptsByTag.Add("script:audio_speed", scripts[5]);
        _scriptsByTag.Add("script:audio_channels", scripts[6]);
        _scriptsByTag.Add("script:mkv_assembly", scripts[7]);
        _scriptsByTag.Add("script:stream_management", scripts[8]);
        _scriptsByTag.Add("script:stream_replacement", scripts[9]);
        _scriptsByTag.Add("script:container_demux", scripts[10]);
        _scriptsByTag.Add("script:subtitles_convert", scripts[11]);
    }

    /// <summary>
    /// Инициализация при загрузке MainPage: проверка зависимостей и начальная навигация.
    /// </summary>
    [RelayCommand]
    private void Initialize()
    {
        _logService.Info(
            "Загрузка главного навигационного интерфейса MainPage",
            "MainPage");

        UpdateLogsTabVisibility();

        // Прогреваем кэш для тяжелой страницы настроек при первом запуске, 
        // чтобы избежать черных вспышек Acrylic при последующих переходах.
        try
        {
            _logService.Info("Выполняется предварительный прогрев кэша страницы настроек...", "MainPage");
            _navigationService.NavigateTo(typeof(SettingsPage));
        }
        catch (Exception ex)
        {
            _logService.Error($"Не удалось выполнить предварительный прогрев настроек: {ex.Message}", "MainPage");
        }

        bool hasRequired = _dependencyManager
            .AreRequiredDependenciesInstalled();
        _logService.Info(
            $"Результат проверки обязательных зависимостей: {hasRequired}",
            "MainPage");

        if (!hasRequired)
        {
            _logService.Warn(
                "Отсутствуют обязательные бинарные компоненты. "
                + "Перенаправление на страницу установки",
                "MainPage");
            Navigate("dependencies");
        }
        else
        {
            Navigate("home");
        }

        // Асинхронно запускаем автоматическую проверку обновлений, если она включена
        if (_settingsManager.AutoCheckUpdates)
        {
            _ = CheckUpdatesSilentlyAsync();
        }
    }

    /// <summary>
    /// Выполняет фоновую автоматическую проверку обновлений при старте приложения.
    /// </summary>
    private async System.Threading.Tasks.Task CheckUpdatesSilentlyAsync()
    {
        _logService.Info("Запущена автоматическая фоновая проверка обновлений...", "MainViewModel");
        try
        {
            var update = await _updateService.CheckForUpdatesAsync(_settingsManager.IncludePreReleases);
            if (update != null)
            {
                _logService.Info($"[Авто-обновление] Найдена более новая версия: {update.Version}", "MainViewModel");

                // Обновляем статус в SettingsViewModel, чтобы вкладка настроек знала о наличии релиза
                _settingsViewModel.NewUpdateInfo = update;
                _settingsViewModel.IsUpdateAvailable = true;
                _settingsViewModel.UpdateStatusText = $"Доступна новая версия: {update.Version}";

                // Предлагаем пользователю обновиться
                bool confirm = await _dialogService.ShowConfirmationAsync(
                    "Доступно обновление",
                    $"Доступна новая версия K-Tools: {update.Version}.\n\nХотите скачать и установить её прямо сейчас?",
                    "Обновиться",
                    "Позже");

                if (confirm)
                {
                    _logService.Info("Пользователь согласился на обновление. Перенаправление на страницу настроек.", "MainViewModel");
                    Navigate("settings");
                    // Запускаем процесс скачивания
                    _ = _settingsViewModel.DownloadAndInstallUpdateCommand.ExecuteAsync(null);
                }
            }
        }
        catch (Exception ex)
        {
            _logService.Exception(ex, "Ошибка при выполнении автоматической проверки обновлений", "MainViewModel");
        }
    }

    /// <summary>
    /// Маршрутизация навигации по строковому тегу элемента NavigationView.
    /// </summary>
    [RelayCommand]
    private void Navigate(string? tag)
    {
        if (string.IsNullOrEmpty(tag)) return;

        if (tag == "settings")
        {
            _logService.Info(
                "Пользователь переключился на страницу настроек приложения",
                "MainPage");
            HeaderTitle = "Настройки";
            HeaderSubtitle =
                "Общие параметры и конфигурация приложения";
            _navigationService.NavigateTo(typeof(SettingsPage));
        }
        else if (tag == "home")
        {
            _logService.Info(
                "Пользователь переключился на домашнюю страницу",
                "MainPage");
            HeaderTitle = "K-Tools";
            HeaderSubtitle =
                "Ваш персональный набор инструментов для обработки медиа";
            _navigationService.NavigateTo(typeof(HomePage));
        }
        else if (tag.StartsWith("script:"))
        {
            if (_scriptsByTag.TryGetValue(tag, out var script))
            {
                _logService.Info(
                    $"Пользователь переключился на рабочий скрипт: '{script.Name}'",
                    "MainPage");
                HeaderTitle = script.Name;
                HeaderSubtitle = script.Description;

                var realScript = _scriptRegistry
                    .GetScriptByName(script.Name);
                if (realScript != null)
                {
                    _navigationService.NavigateTo(
                        typeof(WorkPanel),
                        realScript);
                }
                else
                {
                    _logService.Error(
                        $"Не удалось найти скрипт с именем '{script.Name}' в реестре",
                        "MainPage");
                }
            }
        }
        else if (tag == "logs")
        {
            _logService.Info(
                "Пользователь переключился на страницу просмотра логов",
                "MainPage");
            HeaderTitle = "Логи";
            HeaderSubtitle =
                "Просмотр журналов выполнения и системных сообщений в реальном времени";
            _navigationService.NavigateTo(typeof(LogPage));
        }
        else if (tag == "dependencies")
        {
            _logService.Info(
                "Пользователь переключился на страницу настройки компонентов (зависимостей)",
                "MainPage");
            HeaderTitle = "Компоненты";
            HeaderSubtitle =
                "Установка, обновление и удаление внешних бинарных утилит (FFmpeg, MKVToolNix, eac3to, DEE)";
            _navigationService.NavigateTo(
                typeof(DependencySetupPage));
        }
    }

    /// <summary>
    /// Осуществляет программную навигацию к скрипту по его имени.
    /// Возвращает строковый Tag для синхронизации NavigationView из View.
    /// </summary>
    public string? GetTagForScriptName(string scriptName)
    {
        var pair = _scriptsByTag.FirstOrDefault(
            p => p.Value.Name.Equals(
                scriptName,
                StringComparison.OrdinalIgnoreCase));
        return pair.Key;
    }

    /// <summary>
    /// Обновляет видимость вкладки логов на основе пользовательских настроек.
    /// </summary>
    public void UpdateLogsTabVisibility()
    {
        IsLogsTabVisible = _settingsManager.ShowLogsTab;
    }
}
