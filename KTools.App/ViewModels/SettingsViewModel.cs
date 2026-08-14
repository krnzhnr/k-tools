// -*- coding: utf-8 -*-
using System;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using KTools_App.Core;
using KTools_App.Services.Contracts;

namespace KTools_App.ViewModels;

/// <summary>
/// Сообщение для уведомления о том, что видимость вкладки логов была изменена.
/// </summary>
public sealed class LogsTabVisibilityChangedMessage
{
    /// <summary>Указывает, должна ли быть видима вкладка логов.</summary>
    public bool IsVisible { get; }

    /// <summary>
    /// Инициализирует новый экземпляр сообщения.
    /// </summary>
    public LogsTabVisibilityChangedMessage(bool isVisible)
    {
        IsVisible = isVisible;
    }
}

/// <summary>
/// Сообщение для уведомления об изменении темы оформления приложения.
/// </summary>
public sealed class ThemeChangedMessage
{
    /// <summary>Новая выбранная тема ("Light" или "Dark").</summary>
    public string NewTheme { get; }

    /// <summary>
    /// Инициализирует новый экземпляр сообщения.
    /// </summary>
    public ThemeChangedMessage(string newTheme)
    {
        NewTheme = newTheme;
    }
}

/// <summary>
/// Сообщение для уведомления об изменении типа фона (Mica/Acrylic) приложения.
/// </summary>
public sealed class BackdropChangedMessage
{
    /// <summary>Новый выбранный тип фона ("Mica" или "Acrylic").</summary>
    public string NewBackdrop { get; }

    /// <summary>
    /// Инициализирует новый экземпляр сообщения.
    /// </summary>
    public BackdropChangedMessage(string newBackdrop)
    {
        NewBackdrop = newBackdrop;
    }
}

/// <summary>
/// Модель представления страницы настроек приложения.
/// Управляет всеми пользовательскими конфигурациями и синхронизирует их с SettingsManager.
/// </summary>
public partial class SettingsViewModel : ThreadSafeViewModel
{
    private readonly ISettingsManager _settingsManager;
    private readonly IDialogService _dialogService;
    private readonly IUpdateService _updateService;
    private readonly ILogService _logService;
    private readonly IPathManager _pathManager;
    private readonly IScriptRegistry _scriptRegistry;

    /// <summary>
    /// Строка версии приложения для отображения в блоке "О программе".
    /// </summary>
    public string CurrentVersionText { get; }

    /// <summary>
    /// Путь к папке логов по умолчанию.
    /// </summary>
    public string DefaultLogDir { get; }

    /// <summary>
    /// Флаг процесса проверки обновлений.
    /// </summary>
    [ObservableProperty]
    public partial bool IsChecking { get; set; }

    /// <summary>
    /// Текст статуса проверки обновлений.
    /// </summary>
    [ObservableProperty]
    public partial string UpdateStatusText { get; set; }

    /// <summary>
    /// Указывает, доступно ли обновление.
    /// </summary>
    [ObservableProperty]
    public partial bool IsUpdateAvailable { get; set; }

    /// <summary>
    /// Метаданные доступного обновления.
    /// </summary>
    [ObservableProperty]
    public partial UpdateInfo? NewUpdateInfo { get; set; }

    /// <summary>
    /// Флаг процесса скачивания файла обновления.
    /// </summary>
    [ObservableProperty]
    public partial bool IsDownloading { get; set; }

    /// <summary>
    /// Прогресс скачивания обновления (от 0 до 100).
    /// </summary>
    [ObservableProperty]
    public partial int DownloadProgress { get; set; }

    /// <summary>
    /// Флаг перезаписи существующих файлов результатов обработки.
    /// </summary>
    [ObservableProperty]
    public partial bool OverwriteExisting { get; set; }

    /// <summary>
    /// Флаг имитации старой версии (1.0.0) для проверки обновлений.
    /// </summary>
    [ObservableProperty]
    public partial bool DebugSimulateOldVersion { get; set; }

    /// <summary>
    /// Флаг имитации доступности обновлений зависимостей для проверки UI.
    /// </summary>
    [ObservableProperty]
    public partial bool DebugSimulateDepUpdate { get; set; }

    partial void OnDebugSimulateDepUpdateChanged(bool value)
    {
        _dependencyManager.SetSimulatedUpdateAvailable("ffmpeg", value);
        _dependencyManager.SetSimulatedUpdateAvailable("mkvtoolnix", value);
        _dependencyManager.SetSimulatedUpdateAvailable("yt-dlp", value);
        _logService.Info($"[Debug] Имитация обновления зависимостей установлена в state={value}", "SettingsViewModel");
    }

    /// <summary>
    /// Флаг отключения действия кнопок обновления и скачивания (имитация пустышек).
    /// </summary>
    [ObservableProperty]
    public partial bool DebugDisableUpdateAction { get; set; }

    /// <summary>
    /// Видим ли раздел настроек отладки.
    /// </summary>
    [ObservableProperty]
    public partial bool IsDebugSettingsVisible { get; set; }

    /// <summary>
    /// Флаг очистки списка файлов перед добавлением новых.
    /// </summary>
    [ObservableProperty]
    public partial bool ClearListOnAdd { get; set; }

    /// <summary>
    /// Количество параллельно выполняемых задач обработки медиа.
    /// </summary>
    [ObservableProperty]
    public partial int MaxParallelTasks { get; set; }

    /// <summary>
    /// Разрешить ли параллельное выполнение задач обработки.
    /// </summary>
    [ObservableProperty]
    public partial bool EnableParallel { get; set; }

    /// <summary>
    /// Максимально допустимый лимит параллельных задач обработки, основанный на количестве ядер процессора.
    /// </summary>
    [ObservableProperty]
    public partial int MaxParallelLimit { get; set; }

    /// <summary>
    /// Имя папки по умолчанию для сохранения выходных файлов.
    /// </summary>
    [ObservableProperty]
    public partial string DefaultOutputSubfolder { get; set; }

    /// <summary>
    /// Флаг автоматического создания и использования вложенных папок для вывода.
    /// </summary>
    [ObservableProperty]
    public partial bool UseAutoSubfolder { get; set; }

    /// <summary>
    /// Индекс выбранной темы оформления (0 - Темная, 1 - Светлая).
    /// </summary>
    [ObservableProperty]
    public partial int SelectedThemeIndex { get; set; }

    /// <summary>
    /// Индекс выбранного типа фона окон (0 - Mica, 1 - Acrylic).
    /// </summary>
    [ObservableProperty]
    public partial int SelectedBackdropIndex { get; set; }

    /// <summary>
    /// Флаг отображения вкладки логов в основном меню навигации.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowLogsTab { get; set; }

    /// <summary>
    /// Путь к пользовательской директории хранения файлов журналов (логов).
    /// </summary>
    [ObservableProperty]
    public partial string LogDir { get; set; }

    /// <summary>
    /// Флаг автоматической проверки доступных обновлений приложения при запуске.
    /// </summary>
    [ObservableProperty]
    public partial bool AutoCheckUpdates { get; set; }

    /// <summary>
    /// Флаг включения предварительных версий (Pre-Releases) в проверку обновлений.
    /// </summary>
    [ObservableProperty]
    public partial bool IncludePreReleases { get; set; }

    /// <summary>
    /// Флаг включения переименования выходных файлов по регулярным выражениям (Regex).
    /// </summary>
    [ObservableProperty]
    public partial bool RenameEnableRegex { get; set; }

    /// <summary>
    /// Шаблон поиска (регулярное выражение) для переименования выходных файлов.
    /// </summary>
    [ObservableProperty]
    public partial string RenameRegexSearch { get; set; }

    /// <summary>
    /// Строка замены для переименования выходных файлов.
    /// </summary>
    [ObservableProperty]
    public partial string RenameRegexReplace { get; set; }

    /// <summary>
    /// Использовать ли регулярные выражения для переименования выходных файлов.
    /// </summary>
    [ObservableProperty]
    public partial bool RenameUseRegex { get; set; }

    /// <summary>
    /// Учитывать ли регистр при переименовании выходных файлов.
    /// </summary>
    [ObservableProperty]
    public partial bool RenameCaseSensitive { get; set; }

    /// <summary>
    /// Флаг включения интеграции с контекстным меню Проводника Windows.
    /// </summary>
    [ObservableProperty]
    public partial bool IsContextMenuEnabled { get; set; }

    private readonly IDependencyManager _dependencyManager;

    /// <summary>
    /// Инициализирует новый экземпляр SettingsViewModel с внедрением зависимостей.
    /// </summary>
    public SettingsViewModel(
        ISettingsManager settingsManager,
        IDialogService dialogService,
        IUpdateService updateService,
        ILogService logService,
        IPathManager pathManager,
        IScriptRegistry scriptRegistry,
        IDependencyManager dependencyManager)
    {
        _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _updateService = updateService ?? throw new ArgumentNullException(nameof(updateService));
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _pathManager = pathManager ?? throw new ArgumentNullException(nameof(pathManager));
        _scriptRegistry = scriptRegistry ?? throw new ArgumentNullException(nameof(scriptRegistry));
        _dependencyManager = dependencyManager ?? throw new ArgumentNullException(nameof(dependencyManager));

        UpdateStatusText = "Обновления не проверялись";
        DefaultOutputSubfolder = "KTools_Result";
        LogDir = string.Empty;
        RenameRegexSearch = string.Empty;
        RenameRegexReplace = string.Empty;

        CurrentVersionText = $"Версия {GetAppVersion()} (WinAppSDK / WinUI 3)";
        DefaultLogDir = System.IO.Path.Combine(_pathManager.GetSettingsDirectory(), "logs");

        MaxParallelLimit = Environment.ProcessorCount;
        LoadCurrentSettings();
    }

    /// <summary>
    /// Загружает текущие значения настроек из SettingsManager в свойства ViewModel.
    /// </summary>
    private void LoadCurrentSettings()
    {
        OverwriteExisting = _settingsManager.OverwriteExisting;
        ClearListOnAdd = _settingsManager.ClearListOnAdd;
        EnableParallel = _settingsManager.EnableParallel;
        MaxParallelTasks = _settingsManager.MaxParallelTasks;
        DefaultOutputSubfolder = _settingsManager.DefaultOutputSubfolder;
        UseAutoSubfolder = _settingsManager.UseAutoSubfolder;

        SelectedThemeIndex = _settingsManager.Theme.ToLowerInvariant() switch
        {
            "dark" => 1,
            "light" => 2,
            _ => 0
        };

        SelectedBackdropIndex = _settingsManager.BackdropType
            .Equals("Acrylic", StringComparison.OrdinalIgnoreCase)
            ? 1
            : 0;

        ShowLogsTab = _settingsManager.ShowLogsTab;
        LogDir = _settingsManager.LogDir;

        AutoCheckUpdates = _settingsManager.AutoCheckUpdates;
        IncludePreReleases = _settingsManager.IncludePreReleases;
        RenameEnableRegex = _settingsManager.RenameEnableRegex;
        RenameRegexSearch = _settingsManager.RenameRegexSearch;
        RenameRegexReplace = _settingsManager.RenameRegexReplace;
        RenameUseRegex = _settingsManager.RenameUseRegex;
        RenameCaseSensitive = _settingsManager.RenameCaseSensitive;
        DebugSimulateOldVersion = _settingsManager.DebugSimulateOldVersion;
        DebugDisableUpdateAction = _settingsManager.DebugDisableUpdateAction;
        IsContextMenuEnabled = _settingsManager.GetSetting("Shell", "IsContextMenuEnabled", false);
    }

    partial void OnDebugDisableUpdateActionChanged(bool value)
    {
        _settingsManager.DebugDisableUpdateAction = value;
    }

    partial void OnOverwriteExistingChanged(bool value)
    {
        _settingsManager.OverwriteExisting = value;
    }

    partial void OnClearListOnAddChanged(bool value)
    {
        _settingsManager.ClearListOnAdd = value;
    }

    partial void OnMaxParallelTasksChanged(int value)
    {
        // Если значение меньше 1 (например, слайдер перетащили в крайнее левое положение 0),
        // принудительно возвращаем его к 1 для предотвращения некорректной настройки.
        if (value < 1)
        {
            MaxParallelTasks = 1;
            _settingsManager.MaxParallelTasks = 1;
        }
        else
        {
            _settingsManager.MaxParallelTasks = value;
        }
    }

    partial void OnEnableParallelChanged(bool value)
    {
        _settingsManager.EnableParallel = value;
    }

    partial void OnDefaultOutputSubfolderChanged(string value)
    {
        string subfolder = string.IsNullOrEmpty(value)
            ? "KTools_Result"
            : value;
        _settingsManager.DefaultOutputSubfolder = subfolder;
    }

    partial void OnUseAutoSubfolderChanged(bool value)
    {
        _settingsManager.UseAutoSubfolder = value;
    }

    partial void OnSelectedThemeIndexChanged(int value)
    {
        string newTheme = value switch
        {
            1 => "Dark",
            2 => "Light",
            _ => "System"
        };
        _settingsManager.Theme = newTheme;
        WeakReferenceMessenger.Default.Send(new ThemeChangedMessage(newTheme));
    }

    partial void OnSelectedBackdropIndexChanged(int value)
    {
        string newBackdrop = value == 1 ? "Acrylic" : "Mica";
        _settingsManager.BackdropType = newBackdrop;
        WeakReferenceMessenger.Default.Send(new BackdropChangedMessage(newBackdrop));
    }

    partial void OnShowLogsTabChanged(bool value)
    {
        _settingsManager.ShowLogsTab = value;
        WeakReferenceMessenger.Default.Send(
            new LogsTabVisibilityChangedMessage(value));
    }

    partial void OnAutoCheckUpdatesChanged(bool value)
    {
        _settingsManager.AutoCheckUpdates = value;
    }

    partial void OnIncludePreReleasesChanged(bool value)
    {
        _settingsManager.IncludePreReleases = value;
    }

    partial void OnDebugSimulateOldVersionChanged(bool value)
    {
        _settingsManager.DebugSimulateOldVersion = value;
    }

    partial void OnRenameEnableRegexChanged(bool value)
    {
        _settingsManager.RenameEnableRegex = value;
    }

    partial void OnRenameRegexSearchChanged(string value)
    {
        _settingsManager.RenameRegexSearch = value;
    }

    partial void OnRenameRegexReplaceChanged(string value)
    {
        _settingsManager.RenameRegexReplace = value;
    }

    partial void OnRenameUseRegexChanged(bool value)
    {
        _settingsManager.RenameUseRegex = value;
    }

    partial void OnRenameCaseSensitiveChanged(bool value)
    {
        _settingsManager.RenameCaseSensitive = value;
    }

    partial void OnIsContextMenuEnabledChanged(bool value)
    {
        _settingsManager.SetSetting("Shell", "IsContextMenuEnabled", value);
        _settingsManager.SaveSettings();

        try
        {
            if (value)
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                {
                    var scripts = _scriptRegistry.Scripts.Select(s => s.Name).ToList();
                    ShellIntegration.Register(exePath, scripts);
                    _logService.Info("Интеграция с контекстным меню Проводника успешно включена", "SettingsViewModel");
                }
            }
            else
            {
                ShellIntegration.Unregister();
                _logService.Info("Интеграция с контекстным меню Проводника успешно отключена", "SettingsViewModel");
            }
        }
        catch (Exception ex)
        {
            _logService.Exception(ex, "Ошибка при изменении состояния контекстного меню", "SettingsViewModel");
            _dialogService.ShowMessageAsync(
                "Ошибка интеграции",
                $"Не удалось изменить состояние интеграции с контекстным меню: {ex.Message}");
        }
    }

    /// <summary>
    /// Устанавливает путь к директории хранения логов.
    /// </summary>
    public void SetLogDirectory(string path)
    {
        _settingsManager.LogDir = path;
        LogDir = path;
    }

    /// <summary>
    /// Сбрасывает настройки приложения к исходным значениям по умолчанию.
    /// Перед сбросом запрашивает подтверждение у пользователя.
    /// </summary>
    [RelayCommand]
    private async System.Threading.Tasks.Task ResetSettingsAsync()
    {
        bool confirm = await _dialogService.ShowConfirmationAsync(
            "Сброс настроек",
            "Вы уверены, что хотите восстановить все настройки по умолчанию?",
            "Да",
            "Отмена");

        if (confirm)
        {
            _settingsManager.OverwriteExisting = false;
            _settingsManager.ClearListOnAdd = false;
            _settingsManager.EnableParallel = true;
            _settingsManager.MaxParallelTasks = Math.Max(
                1,
                Environment.ProcessorCount / 2);
            _settingsManager.DefaultOutputSubfolder = "KTools_Result";
            _settingsManager.UseAutoSubfolder = false;
            _settingsManager.Theme = "Dark";
            _settingsManager.BackdropType = "Mica";
            _settingsManager.ShowLogsTab = false;
            _settingsManager.LogDir = string.Empty;
            _settingsManager.AutoCheckUpdates = true;
            _settingsManager.IncludePreReleases = true;
            _settingsManager.RenameEnableRegex = false;
            _settingsManager.RenameRegexSearch = string.Empty;
            _settingsManager.RenameRegexReplace = string.Empty;
            _settingsManager.RenameUseRegex = true;
            _settingsManager.RenameCaseSensitive = false;
            _settingsManager.DebugSimulateOldVersion = false;
            IsDebugSettingsVisible = false;

            LoadCurrentSettings();

            WeakReferenceMessenger.Default.Send(
                new LogsTabVisibilityChangedMessage(false));

            await _dialogService.ShowMessageAsync(
                "Настройки сброшены",
                "Все настройки были успешно сброшены к значениям по умолчанию.");
        }
    }

    /// <summary>
    /// Выполняет проверку наличия обновлений на основе текущих настроек пользователя.
    /// </summary>
    [RelayCommand]
    public async System.Threading.Tasks.Task CheckForUpdatesAsync()
    {
        if (IsChecking) return;

        if (DebugDisableUpdateAction)
        {
            _logService.Info("[Debug] Проверка обновлений заблокирована переключателем.", "SettingsViewModel");
            return;
        }

        IsChecking = true;
        UpdateStatusText = "Выполняется проверка обновлений...";
        IsUpdateAvailable = false;
        NewUpdateInfo = null;

        try
        {
            var update = await _updateService.CheckForUpdatesAsync(IncludePreReleases);
            if (update != null)
            {
                NewUpdateInfo = update;
                IsUpdateAvailable = true;
                UpdateStatusText = $"Доступна новая версия: {update.Version}";
            }
            else
            {
                UpdateStatusText = "Установлена последняя версия приложения";
            }
        }
        catch (Exception ex)
        {
            UpdateStatusText = "Не удалось выполнить проверку обновлений";
            _logService.Exception(ex, "Ошибка при ручной проверке обновлений из панели настроек", "SettingsViewModel");
            await _dialogService.ShowMessageAsync("Ошибка", $"Не удалось проверить обновления: {ex.Message}");
        }
        finally
        {
            IsChecking = false;
        }
    }

    /// <summary>
    /// Запускает скачивание и установку найденного обновления.
    /// </summary>
    [RelayCommand]
    public async System.Threading.Tasks.Task DownloadAndInstallUpdateAsync()
    {
        if (NewUpdateInfo == null || IsDownloading) return;

        if (DebugDisableUpdateAction)
        {
            _logService.Info("[Debug] Загрузка и установка обновлений заблокирована переключателем.", "SettingsViewModel");
            return;
        }

        IsDownloading = true;
        DownloadProgress = 0;

        try
        {
            await _updateService.DownloadAndInstallUpdateAsync(
                NewUpdateInfo.DownloadUrl,
                NewUpdateInfo.FileName,
                progress =>
                {
                    DownloadProgress = (int)Math.Round(progress);
                });
        }
        catch (Exception ex)
        {
            IsDownloading = false;
            _logService.Exception(ex, "Ошибка при скачивании или установке обновления", "SettingsViewModel");
            await _dialogService.ShowMessageAsync("Ошибка", $"Не удалось загрузить или установить обновление: {ex.Message}");
        }
    }

    /// <summary>
    /// Возвращает информационную версию текущей сборки приложения из метаданных сборки.
    /// </summary>
    private string GetAppVersion()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrEmpty(infoVersion))
            {
                int plusIdx = infoVersion.IndexOf('+');
                return plusIdx > 0 ? infoVersion.Substring(0, plusIdx) : infoVersion;
            }
            return assembly.GetName().Version?.ToString() ?? "2.0.0";
        }
        catch
        {
            return "2.0.0";
        }
    }

    private int _versionClickCount = 0;

    /// <summary>
    /// Обработчик клика по версии программы. При 7 кликах активирует меню отладки.
    /// </summary>
    [RelayCommand]
    private async System.Threading.Tasks.Task VersionClickedAsync()
    {
        _versionClickCount++;
        _logService.Info($"Клик по кнопке версии: {_versionClickCount}/7", "SettingsViewModel");
        if (_versionClickCount >= 7)
        {
            IsDebugSettingsVisible = true;
            _versionClickCount = 0;
            await _dialogService.ShowMessageAsync(
                "Режим разработчика",
                "Режим разработчика успешно активирован! Настройки отладки будут доступны внизу страницы до перезапуска приложения.");
        }
    }
}
