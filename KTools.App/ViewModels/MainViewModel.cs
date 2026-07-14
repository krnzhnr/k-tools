// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
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
public partial class MainViewModel : ThreadSafeViewModel
{
    private readonly INavigationService _navigationService;
    private readonly IScriptRegistry _scriptRegistry;
    private readonly IDependencyManager _dependencyManager;
    private readonly ISettingsManager _settingsManager;
    private readonly ILogService _logService;
    private readonly SettingsViewModel _settingsViewModel;
    private readonly IDialogService _dialogService;
    private readonly IUpdateService _updateService;

    private bool _isShellActivated = false;

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
    /// Флаг видимости баннера обновлений на главном экране.
    /// </summary>
    [ObservableProperty]
    public partial bool IsUpdateBannerVisible { get; set; }

    /// <summary>
    /// Текст статуса для баннера обновлений.
    /// </summary>
    [ObservableProperty]
    public partial string UpdateStatusText { get; set; } = string.Empty;

    /// <summary>
    /// Информация о доступном обновлении для баннера.
    /// </summary>
    [ObservableProperty]
    public partial UpdateInfo? NewUpdateInfo { get; set; }

    /// <summary>
    /// Инициализирует ViewModel главной страницы с внедрением зависимостей.
    /// </summary>
    public MainViewModel(
        INavigationService navigationService,
        IScriptRegistry scriptRegistry,
        IDependencyManager dependencyManager,
        ISettingsManager settingsManager,
        ILogService logService,
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

        // Подписываемся на сообщения активации через контекстное меню/командную строку
        messenger.Register<MainViewModel, ShellActivationMessage>(
            this,
            (r, m) => r.HandleShellActivation(m));
    }

    /// <summary>
    /// Инициализация и маппинг списка скриптов на теги навигации.
    /// </summary>
    private void InitializeScripts()
    {
        var scripts = new List<ScriptInfo>
        {
            new ScriptInfo { Name = AppConstants.ScriptMetadata.VideoProcessorName, Category = AppConstants.ScriptCategory.Video, IconName = AppConstants.ScriptIcons.VideoEncoding, Description = AppConstants.ScriptMetadata.VideoProcessorDesc },
            new ScriptInfo { Name = AppConstants.ScriptMetadata.ContainerConvName, Category = AppConstants.ScriptCategory.Video, IconName = AppConstants.ScriptIcons.ContainerConversion, Description = AppConstants.ScriptMetadata.ContainerConvDesc },
            new ScriptInfo { Name = AppConstants.ScriptMetadata.MetadataCleanName, Category = AppConstants.ScriptCategory.Video, IconName = AppConstants.ScriptIcons.MetadataCleanup, Description = AppConstants.ScriptMetadata.MetadataCleanDesc },
            new ScriptInfo { Name = AppConstants.ScriptMetadata.AudioConverterName, Category = AppConstants.ScriptCategory.Audio, IconName = AppConstants.ScriptIcons.AudioEncoding, Description = AppConstants.ScriptMetadata.AudioConverterDesc },
            new ScriptInfo { Name = AppConstants.ScriptMetadata.AudioDownmixName, Category = AppConstants.ScriptCategory.Audio, IconName = AppConstants.ScriptIcons.AudioDownmix, Description = AppConstants.ScriptMetadata.AudioDownmixDesc },
            new ScriptInfo { Name = AppConstants.ScriptMetadata.AudioSpeedName, Category = AppConstants.ScriptCategory.Audio, IconName = AppConstants.ScriptIcons.AudioSpeed, Description = AppConstants.ScriptMetadata.AudioSpeedDesc },
            new ScriptInfo { Name = AppConstants.ScriptMetadata.AudioSplitName, Category = AppConstants.ScriptCategory.Audio, IconName = AppConstants.ScriptIcons.AudioChannels, Description = AppConstants.ScriptMetadata.AudioSplitDesc },
            new ScriptInfo { Name = AppConstants.ScriptMetadata.AudioShiftName, Category = AppConstants.ScriptCategory.Audio, IconName = AppConstants.ScriptIcons.AudioShift, Description = AppConstants.ScriptMetadata.AudioShiftDesc },
            new ScriptInfo { Name = AppConstants.ScriptMetadata.MuxerName, Category = AppConstants.ScriptCategory.Containers, IconName = AppConstants.ScriptIcons.MkvAssembly, Description = AppConstants.ScriptMetadata.MuxerDesc },
            new ScriptInfo { Name = AppConstants.ScriptMetadata.StreamMgrName, Category = AppConstants.ScriptCategory.Containers, IconName = AppConstants.ScriptIcons.StreamManagement, Description = AppConstants.ScriptMetadata.StreamMgrDesc },
            new ScriptInfo { Name = AppConstants.ScriptMetadata.StreamReplName, Category = AppConstants.ScriptCategory.Containers, IconName = AppConstants.ScriptIcons.StreamReplacement, Description = AppConstants.ScriptMetadata.StreamReplDesc },
            new ScriptInfo { Name = AppConstants.ScriptMetadata.TrackExtrName, Category = AppConstants.ScriptCategory.Containers, IconName = AppConstants.ScriptIcons.TrackExtractor, Description = AppConstants.ScriptMetadata.TrackExtrDesc },
            new ScriptInfo { Name = AppConstants.ScriptMetadata.AssToVttName, Category = AppConstants.ScriptCategory.Subtitles, IconName = AppConstants.ScriptIcons.SubtitlesConvert, Description = AppConstants.ScriptMetadata.AssToVttDesc },
            new ScriptInfo { Name = AppConstants.ScriptMetadata.SubtitleShiftName, Category = AppConstants.ScriptCategory.Subtitles, IconName = AppConstants.ScriptIcons.SubtitlesShift, Description = AppConstants.ScriptMetadata.SubtitleShiftDesc },
            new ScriptInfo { Name = "Загрузка медиа", Category = "Сеть", IconName = "World", Description = "Загрузка видео- и аудиофайлов из сети по URL-адресам через yt-dlp" }
        };

        _scriptsByTag.Add("script:video_encoding", scripts[0]);
        _scriptsByTag.Add("script:container_conversion", scripts[1]);
        _scriptsByTag.Add("script:metadata_cleanup", scripts[2]);
        _scriptsByTag.Add("script:audio_encoding", scripts[3]);
        _scriptsByTag.Add("script:audio_downmix", scripts[4]);
        _scriptsByTag.Add("script:audio_speed", scripts[5]);
        _scriptsByTag.Add("script:audio_channels", scripts[6]);
        _scriptsByTag.Add("script:audio_shift", scripts[7]);
        _scriptsByTag.Add("script:mkv_assembly", scripts[8]);
        _scriptsByTag.Add("script:stream_management", scripts[9]);
        _scriptsByTag.Add("script:stream_replacement", scripts[10]);
        _scriptsByTag.Add("script:container_demux", scripts[11]);
        _scriptsByTag.Add("script:subtitles_convert", scripts[12]);
        _scriptsByTag.Add("script:subtitles_shift", scripts[13]);
        _scriptsByTag.Add("script:media_downloader", scripts[14]);
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
        else if (!_isShellActivated)
        {
            Navigate("home");
        }

        // Асинхронно запускаем автоматическую проверку обновлений, если она включена
        if (_settingsManager.AutoCheckUpdates)
        {
            _ = CheckUpdatesSilentlyAsync();
        }

        // Асинхронно запускаем фоновую проверку обновлений yt-dlp nightly
        _ = _dependencyManager.CheckAndUpdateYtDlpAsync();
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

                // Устанавливаем свойства для отображения баннера на главной странице
                NewUpdateInfo = update;
                UpdateStatusText = $"Доступна новая версия: {update.Version}";
                IsUpdateBannerVisible = true;
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
            IsUpdateBannerVisible = false; // Скрываем баннер обновлений, чтобы избежать дублирования интерфейса обновлений
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
            // Показываем баннер обновлений снова, если обновление доступно, но еще не загружается/установлено
            if (_settingsViewModel.IsUpdateAvailable && !_settingsViewModel.IsDownloading)
            {
                NewUpdateInfo = _settingsViewModel.NewUpdateInfo;
                UpdateStatusText = _settingsViewModel.UpdateStatusText;
                IsUpdateBannerVisible = true;
            }
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
        else if (tag == "tool:timing_calculator")
        {
            _logService.Info(
                "Пользователь переключился на страницу калькулятора сдвига таймингов",
                "MainPage");
            HeaderTitle = "Калькулятор сдвига";
            HeaderSubtitle =
                "Расчет разницы во времени между двумя таймингами для корректировки сдвига аудио и субтитров";
            _navigationService.NavigateTo(typeof(TimingCalculatorPage));
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

    /// <summary>
    /// Закрывает баннер обновлений на главном экране.
    /// </summary>
    [RelayCommand]
    private void CloseUpdateBanner()
    {
        IsUpdateBannerVisible = false;
        _logService.Info("Пользователь закрыл баннер обновлений на главной странице.", "MainViewModel");
    }

    /// <summary>
    /// Переходит на страницу настроек и запускает процесс скачивания обновления.
    /// </summary>
    [RelayCommand]
    private void GoToUpdate()
    {
        IsUpdateBannerVisible = false;
        _logService.Info("Пользователь кликнул по кнопке 'Обновиться' в баннере. Перенаправление в настройки.", "MainViewModel");
        _navigationService.NavigateTo(typeof(SettingsPage), "scroll_to_updates");
        _ = _settingsViewModel.DownloadAndInstallUpdateCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// Обрабатывает активацию приложения из командной строки или контекстного меню Проводника.
    /// </summary>
    private void HandleShellActivation(ShellActivationMessage message)
    {
        _isShellActivated = true;
        _logService.Info($"Получен запрос активации через командную строку. Скрипт: '{message.ScriptTag ?? "не указан"}', файлов: {message.Files.Count}", "MainViewModel");

        string? targetTag = null;
        if (!string.IsNullOrEmpty(message.ScriptTag))
        {
            var cleanTag = message.ScriptTag.Trim().ToLowerInvariant();

            // 1. Поиск по точному совпадению ключа тега (например, "script:video_encoding" или "video_encoding")
            var key = _scriptsByTag.Keys.FirstOrDefault(k =>
                k.Equals(cleanTag, StringComparison.OrdinalIgnoreCase) ||
                k.Equals($"script:{cleanTag}", StringComparison.OrdinalIgnoreCase));

            if (key != null)
            {
                targetTag = key;
            }
            else
            {
                // 2. Поиск по началу имени скрипта (например, "Ремуксинг" или "Transmuxing")
                var pair = _scriptsByTag.FirstOrDefault(p =>
                    p.Value.Name.Contains(message.ScriptTag, StringComparison.OrdinalIgnoreCase) ||
                    p.Key.Contains(cleanTag, StringComparison.OrdinalIgnoreCase));
                if (pair.Key != null)
                {
                    targetTag = pair.Key;
                }
            }
        }

        // Если нашли подходящий скрипт, добавляем файлы в его очередь и переключаемся
        if (targetTag != null && _scriptsByTag.TryGetValue(targetTag, out var scriptInfo))
        {
            var realScript = _scriptRegistry.GetScriptByName(scriptInfo.Name);
            if (realScript != null)
            {
                _logService.Info($"Перенаправление файлов в скрипт '{realScript.Name}' и навигация на вкладку", "MainViewModel");
                AddFilesToScript(realScript, message.Files);
                Navigate(targetTag);
            }
        }
        else if (message.Files.Count > 0)
        {
            // Если скрипт не распознан, но файлы переданы, по умолчанию добавляем в первый доступный скрипт
            // или выводим предупреждение. Давайте добавим файлы в "Кодирование видео" (первый скрипт).
            var defaultPair = _scriptsByTag.FirstOrDefault();
            if (defaultPair.Key != null)
            {
                var realScript = _scriptRegistry.GetScriptByName(defaultPair.Value.Name);
                if (realScript != null)
                {
                    _logService.Warn($"Скрипт '{message.ScriptTag}' не распознан. Файлы добавлены в скрипт по умолчанию: '{realScript.Name}'", "MainViewModel");
                    AddFilesToScript(realScript, message.Files);
                    Navigate(defaultPair.Key);
                }
            }
        }
    }

    /// <summary>
    /// Асинхронно добавляет файлы в очередь скрипта и запускает их технический анализ.
    /// </summary>
    private void AddFilesToScript(AbstractScript script, List<string> files)
    {
        var mediaProbeService = App.Services.GetRequiredService<IMediaProbeService>();
        foreach (var file in files)
        {
            // Проверяем поддерживается ли расширение файла выбранным скриптом
            if (script.FileExtensions != null && script.FileExtensions.Length > 0)
            {
                string ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
                if (!script.FileExtensions.Contains(ext))
                {
                    _logService.Warn($"Файл '{file}' пропущен: расширение '{ext}' не поддерживается скриптом '{script.Name}'", "MainViewModel");
                    continue;
                }
            }

            if (script.FilesQueue.Any(f => f.FilePath.Equals(file, StringComparison.OrdinalIgnoreCase)))
            {
                continue; // Исключаем дубликаты
            }

            var item = new FileQueueItem(file);
            script.FilesQueue.Add(item);

            // Запускаем фоновый асинхронный анализ структуры файла
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var structure = await mediaProbeService.ProbeAsync(item.FilePath);
                    item.MediaInfo = structure ?? new MediaStructure { FilePath = item.FilePath };
                }
                catch (Exception ex)
                {
                    _logService.Exception(ex, $"Не удалось выполнить технический анализ файла: {item.FileName}", "MainViewModel");
                    // Присваиваем пустую структуру, чтобы скрыть бесконечный спиннер в интерфейсе
                    item.MediaInfo = new MediaStructure { FilePath = item.FilePath };
                }
            });
        }
    }
}
