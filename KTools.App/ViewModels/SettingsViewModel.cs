// -*- coding: utf-8 -*-
using System;
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
public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsManager _settingsManager;
    private readonly IDialogService _dialogService;
    private readonly IUpdateService _updateService;

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
    /// Инициализирует новый экземпляр SettingsViewModel с внедрением зависимостей.
    /// </summary>
    public SettingsViewModel(
        SettingsManager settingsManager,
        IDialogService dialogService,
        IUpdateService updateService)
    {
        _settingsManager = settingsManager;
        _dialogService = dialogService;
        _updateService = updateService;

        UpdateStatusText = "Обновления не проверялись";
        DefaultOutputSubfolder = "KTools_Result";
        LogDir = string.Empty;

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
        MaxParallelTasks = _settingsManager.MaxParallelTasks;
        DefaultOutputSubfolder = _settingsManager.DefaultOutputSubfolder;
        UseAutoSubfolder = _settingsManager.UseAutoSubfolder;

        SelectedThemeIndex = _settingsManager.Theme
            .Equals("Light", StringComparison.OrdinalIgnoreCase)
            ? 1
            : 0;

        SelectedBackdropIndex = _settingsManager.BackdropType
            .Equals("Acrylic", StringComparison.OrdinalIgnoreCase)
            ? 1
            : 0;

        ShowLogsTab = _settingsManager.ShowLogsTab;
        LogDir = string.IsNullOrEmpty(_settingsManager.LogDir)
            ? "Используется папка по умолчанию"
            : _settingsManager.LogDir;

        AutoCheckUpdates = _settingsManager.AutoCheckUpdates;
        IncludePreReleases = _settingsManager.IncludePreReleases;
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
        _settingsManager.MaxParallelTasks = value;
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
        string newTheme = value == 1 ? "Light" : "Dark";
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

    /// <summary>
    /// Устанавливает путь к директории хранения логов.
    /// </summary>
    public void SetLogDirectory(string path)
    {
        _settingsManager.LogDir = path;
        LogDir = string.IsNullOrEmpty(path)
            ? "Используется папка по умолчанию"
            : path;
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
            _settingsManager.IncludePreReleases = false;

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
            LogService.Instance.Exception(ex, "Ошибка при ручной проверке обновлений из панели настроек", "SettingsViewModel");
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
            LogService.Instance.Exception(ex, "Ошибка при скачивании или установке обновления", "SettingsViewModel");
            await _dialogService.ShowMessageAsync("Ошибка", $"Не удалось загрузить или установить обновление: {ex.Message}");
        }
    }
}
