// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Services.Contracts;
using KTools_App.UI.Pages;

using Microsoft.Extensions.DependencyInjection;

namespace KTools_App.ViewModels;

/// <summary>
/// Модель представления главной страницы приложения (MainPage).
/// Управляет навигацией между экранами, заголовками и видимостью вкладки логов.
/// </summary>
public partial class MainViewModel : ThreadSafeViewModel
{
    private const string SourceName = "MainPage";
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
    private readonly IMediaProbeService _mediaProbeService;

    public MainViewModel(
        INavigationService navigationService,
        IScriptRegistry scriptRegistry,
        IDependencyManager dependencyManager,
        ISettingsManager settingsManager,
        ILogService logService,
        SettingsViewModel settingsViewModel,
        IDialogService dialogService,
        IUpdateService updateService,
        IMediaProbeService mediaProbeService)
    {
        _navigationService = navigationService;
        _scriptRegistry = scriptRegistry;
        _dependencyManager = dependencyManager;
        _settingsManager = settingsManager;
        _logService = logService;
        _settingsViewModel = settingsViewModel;
        _dialogService = dialogService;
        _updateService = updateService;
        _mediaProbeService = mediaProbeService;

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
            (r, m) =>
            {
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
    /// <summary>
    /// Динамическая инициализация и маппинг ВСЕХ зарегистрированных скриптов из ScriptRegistry.
    /// </summary>
    private void InitializeScripts()
    {
        _scriptsByTag.Clear();

        foreach (var script in _scriptRegistry.Scripts)
        {
            var info = new ScriptInfo
            {
                Name = script.Name,
                Category = script.Category,
                IconName = script.IconName,
                Description = script.Description
            };

            // Прямая регистрация по имени скрипта
            _scriptsByTag[$"script:{script.Name}"] = info;
        }

        // Сохраняем обратную совместимость для старых текстовых тегов меню
        AddLegacyTagMapping("script:video_encoding", AppConstants.ScriptMetadata.VideoProcessorName);
        AddLegacyTagMapping("script:container_conversion", AppConstants.ScriptMetadata.ContainerConvName);
        AddLegacyTagMapping("script:metadata_cleanup", AppConstants.ScriptMetadata.MetadataCleanName);
        AddLegacyTagMapping("script:audio_encoding", AppConstants.ScriptMetadata.AudioConverterName);
        AddLegacyTagMapping("script:audio_downmix", AppConstants.ScriptMetadata.AudioDownmixName);
        AddLegacyTagMapping("script:audio_speed", AppConstants.ScriptMetadata.AudioSpeedName);
        AddLegacyTagMapping("script:audio_channels", AppConstants.ScriptMetadata.AudioSplitName);
        AddLegacyTagMapping("script:audio_shift", AppConstants.ScriptMetadata.AudioShiftName);
        AddLegacyTagMapping("script:audio_transplant", AppConstants.ScriptMetadata.AudioTransplantName);
        AddLegacyTagMapping("script:mkv_assembly", AppConstants.ScriptMetadata.MuxerName);
        AddLegacyTagMapping("script:stream_management", AppConstants.ScriptMetadata.StreamMgrName);
        AddLegacyTagMapping("script:stream_replacement", AppConstants.ScriptMetadata.StreamReplName);
        AddLegacyTagMapping("script:container_demux", AppConstants.ScriptMetadata.TrackExtrName);
        AddLegacyTagMapping("script:subtitles_convert", AppConstants.ScriptMetadata.AssToVttName);
        AddLegacyTagMapping("script:subtitles_shift", AppConstants.ScriptMetadata.SubtitleShiftName);
        AddLegacyTagMapping("script:speech_recognition", AppConstants.ScriptMetadata.SpeechRecognitionName);
        AddLegacyTagMapping("script:media_downloader", "Загрузка медиа");
    }

    private void AddLegacyTagMapping(string tag, string scriptName)
    {
        var script = _scriptRegistry.GetScriptByName(scriptName);
        if (script != null)
        {
            _scriptsByTag[tag] = new ScriptInfo
            {
                Name = script.Name,
                Category = script.Category,
                IconName = script.IconName,
                Description = script.Description
            };
        }
    }

    /// <summary>
    /// Инициализация при загрузке MainPage: проверка зависимостей и начальная навигация.
    /// </summary>
    [RelayCommand]
    private void Initialize()
    {
        UpdateLogsTabVisibility();

        bool hasRequired = _dependencyManager
            .AreRequiredDependenciesInstalled();
        _logService.Write(
            "app.dependencies.checked",
            LogLevel.Info,
            LogStatus.Succeeded,
            hasRequired
                ? "Обязательные компоненты установлены"
                : "Обнаружены отсутствующие обязательные компоненты",
            source: SourceName,
            properties: LogProps
                .Create("Status", hasRequired ? "Ready" : "Missing")
                .With("Group", "Required"));

        if (!hasRequired)
        {
            _logService.Write(
                "app.dependencies.setup_required",
                LogLevel.Warning,
                LogStatus.Skipped,
                "Отсутствуют обязательные бинарные компоненты, выполняется переход на страницу установки",
                source: SourceName,
                properties: LogProps.Create("ErrorCode", "REQUIRED_DEPENDENCIES_MISSING"));
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

        // Асинхронно запускаем фоновую проверку обновлений всех зависимостей (yt-dlp, FFmpeg, MKVToolNix и др.)
        _ = _dependencyManager.CheckAllDependencyUpdatesAsync();
    }

    /// <summary>
    /// Выполняет фоновую автоматическую проверку обновлений при старте приложения.
    /// </summary>
    private async System.Threading.Tasks.Task CheckUpdatesSilentlyAsync()
    {
        _logService.Write(
            "app.update.check_started",
            LogLevel.Debug,
            LogStatus.Running,
            "Запущена фоновая проверка обновлений приложения",
            source: SourceName,
            properties: LogProps
                .Create("Group", "Update")
                .With("Reason", "AutoCheck"));

        try
        {
            var update = await _updateService.CheckForUpdatesAsync(_settingsManager.IncludePreReleases);
            if (update != null)
            {
                _logService.Write(
                    "app.update.available",
                    LogLevel.Info,
                    LogStatus.Succeeded,
                    $"Доступна более новая версия: {update.Version}",
                    source: SourceName,
                    properties: LogProps
                        .Create("Version", LogRedactor.CompactSafeToken(update.Version))
                        .With("Reason", "AutoCheck"));

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
            _logService.Write(
                "app.update.check_failed",
                LogLevel.Warning,
                LogStatus.Failed,
                "Фоновая проверка обновлений не завершена",
                ex,
                SourceName,
                properties: LogProps
                    .Create("ErrorCode", "UPDATE_CHECK_FAILED")
                    .With("Reason", "AutoCheck"));
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
            HeaderTitle = "Настройки";
            HeaderSubtitle =
                "Общие параметры и конфигурация приложения";
            IsUpdateBannerVisible = false; // Скрываем баннер обновлений, чтобы избежать дублирования интерфейса обновлений
            _navigationService.NavigateTo(typeof(SettingsPage));
        }
        else if (tag == "home")
        {
            HeaderTitle = "K-Tools";
            HeaderSubtitle =
                "Ваш персональный набор инструментов для обработки медиа";
            // Показываем баннер обновлений снова, если обновление доступно, но еще не загружается/устанавливается
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
                    _logService.Write(
                        "ui.navigation.script_not_registered",
                        LogLevel.Error,
                        LogStatus.Failed,
                        $"Выбранный скрипт '{script.Name}' не найден в реестре",
                        source: SourceName,
                        properties: LogProps
                            .Create("ScriptId", LogRedactor.CompactSafeToken(script.Name))
                            .With("ErrorCode", "SCRIPT_NOT_REGISTERED"));
                }
            }
        }
        else if (tag == "logs")
        {
            HeaderTitle = "Журнал";
            HeaderSubtitle =
                "Просмотр журнала выполнения и системных сообщений в реальном времени";
            _navigationService.NavigateTo(typeof(LogPage));
        }
        else if (tag == "tool:timing_calculator")
        {
            HeaderTitle = "Калькулятор сдвига";
            HeaderSubtitle =
                "Расчет разницы во времени между двумя таймингами для корректировки сдвига аудио и субтитров";
            _navigationService.NavigateTo(typeof(TimingCalculatorPage));
        }
        else if (tag == "dependencies")
        {
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
    }

    /// <summary>
    /// Переходит на страницу настроек и запускает процесс скачивания обновления.
    /// </summary>
    [RelayCommand]
    private void GoToUpdate()
    {
        IsUpdateBannerVisible = false;
        _logService.Write(
            "app.update.install_requested",
            LogLevel.Info,
            LogStatus.Running,
            "Пользователь запустил загрузку обновления",
            source: SourceName,
            properties: LogProps.Create("Group", "Update"));
        _navigationService.NavigateTo(typeof(SettingsPage), "scroll_to_updates");
        _ = _settingsViewModel.DownloadAndInstallUpdateCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// Обрабатывает активацию приложения из командной строки или контекстного меню Проводника.
    /// </summary>
    private void HandleShellActivation(ShellActivationMessage message)
    {
        _isShellActivated = true;
        _logService.Write(
            "app.shell.activated",
            LogLevel.Info,
            LogStatus.Running,
            "Получена активация приложения из оболочки",
            source: SourceName,
            properties: LogProps
                .Create("ScriptId", LogRedactor.CompactSafeToken(message.ScriptTag))
                .With("Count", message.Files.Count));

        string? targetTag = null;
        if (!string.IsNullOrEmpty(message.ScriptTag))
        {
            var cleanTag = message.ScriptTag.Trim().ToLowerInvariant();
            var normalizedTag = cleanTag.Replace("_", " ");

            // 1. Поиск по точному совпадению ключа тега (например, "script:video_encoding" или "video_encoding")
            var key = _scriptsByTag.Keys.FirstOrDefault(k =>
                k.Equals(cleanTag, StringComparison.OrdinalIgnoreCase) ||
                k.Equals($"script:{cleanTag}", StringComparison.OrdinalIgnoreCase) ||
                k.Equals($"script:{normalizedTag}", StringComparison.OrdinalIgnoreCase));

            if (key != null)
            {
                targetTag = key;
            }
            else
            {
                // 2. Поиск по имени скрипта (например, "конвертация_субтитров" -> "конвертация субтитров")
                var pair = _scriptsByTag.FirstOrDefault(p =>
                    p.Value.Name.Contains(message.ScriptTag, StringComparison.OrdinalIgnoreCase) ||
                    p.Value.Name.Contains(normalizedTag, StringComparison.OrdinalIgnoreCase) ||
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
                _logService.Write(
                    "app.shell.files_routed",
                    LogLevel.Debug,
                    LogStatus.Succeeded,
                    "Файлы из оболочки переданы выбранному скрипту",
                    source: SourceName,
                    properties: LogProps
                        .Create("ScriptId", LogRedactor.CompactSafeToken(realScript.Name))
                        .With("Count", message.Files.Count));
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
                    _logService.Write(
                        "app.shell.script_unrecognized",
                        LogLevel.Warning,
                        LogStatus.Skipped,
                        $"Скрипт '{message.ScriptTag}' не распознан, файлы переданы скрипту по умолчанию '{realScript.Name}'",
                        source: SourceName,
                        properties: LogProps
                            .Create("ErrorCode", "SHELL_SCRIPT_UNRECOGNIZED")
                            .With("ScriptId", LogRedactor.CompactSafeToken(realScript.Name))
                            .With("Count", message.Files.Count));
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
        var mediaProbeService = _mediaProbeService;
        var existingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var queued in script.FilesQueue)
        {
            existingPaths.Add(queued.FilePath);
        }

        foreach (var file in files)
        {
            // Проверяем поддерживается ли расширение файла выбранным скриптом
            if (script.FileExtensions != null && script.FileExtensions.Length > 0)
            {
                string ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
                if (!script.FileExtensions.Contains(ext))
                {
                    _logService.Write(
                        "ui.file.unsupported_extension",
                        LogLevel.Warning,
                        LogStatus.Skipped,
                        $"Файл '{LogProps.FileName(file)}' пропущен: расширение '{ext}' не поддерживается скриптом '{script.Name}'",
                        source: SourceName,
                        properties: LogProps
                            .Create("FileName", LogProps.FileName(file))
                            .With("Extension", LogRedactor.CompactSafeToken(ext))
                            .With("ScriptId", LogRedactor.CompactSafeToken(script.Name))
                            .With("ErrorCode", "UNSUPPORTED_EXTENSION"));
                    continue;
                }
            }

            if (!existingPaths.Add(file))
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
                    _logService.Write(
                        "media.probe.background_failed",
                        LogLevel.Warning,
                        LogStatus.Failed,
                        $"Фоновый технический анализ файла '{LogProps.FileName(item.FilePath)}' не выполнен",
                        ex,
                        SourceName,
                        properties: LogProps
                            .Create("FileName", LogProps.FileName(item.FilePath))
                            .With("ErrorCode", "BACKGROUND_PROBE_FAILED"));
                    // Присваиваем пустую структуру, чтобы скрыть бесконечный спиннер в интерфейсе
                    item.MediaInfo = new MediaStructure { FilePath = item.FilePath };
                }
            });
        }
    }
}
