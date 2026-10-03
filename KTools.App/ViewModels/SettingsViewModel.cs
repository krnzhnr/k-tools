// -*- coding: utf-8 -*-
using System;
using System.Reflection;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

using KTools_App.Core;
using KTools_App.Diagnostics;
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
    private const string SourceName = nameof(SettingsViewModel);
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
        _logService.Write("settings.debug.dependency_simulation", LogLevel.Debug, LogStatus.Changed, $"Имитация обновления зависимостей установлена: {value}", source: SourceName, properties: LogProps.Create("Changed", value).With("Group", "Debug"));
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

    /// <summary>
    /// Состояние последней фиксации пользовательской настройки.
    /// </summary>
    [ObservableProperty]
    public partial SettingCommitState LastCommitState { get; set; } = SettingCommitState.None;

    /// <summary>
    /// Пользовательский статус последней фиксации: без значения параметра, путей и текста исключения.
    /// </summary>
    [ObservableProperty]
    public partial string LastCommitStatusText { get; set; } = "Изменений настроек ещё не было";

    /// <summary>
    /// Типизированный код ошибки последней фиксации (PersistenceErrorCodes.None при успехе).
    /// </summary>
    [ObservableProperty]
    public partial string LastCommitErrorCode { get; set; } = PersistenceErrorCodes.None;

    private readonly IDependencyManager _dependencyManager;
    private bool _isApplyingManagerState;

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
    /// Загрузка не является пользовательским изменением, поэтому фиксация в менеджере
    /// подавляется: чтение состояния никогда не порождает запись и не меняет кэш.
    /// </summary>
    private void LoadCurrentSettings()
    {
        _isApplyingManagerState = true;
        try
        {
            OverwriteExisting = _settingsManager.OverwriteExisting;
            ClearListOnAdd = _settingsManager.ClearListOnAdd;
            EnableParallel = _settingsManager.EnableParallel;
            MaxParallelTasks = Math.Max(1, _settingsManager.MaxParallelTasks);
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
        finally
        {
            _isApplyingManagerState = false;
        }
    }

    partial void OnDebugDisableUpdateActionChanged(bool value)
    {
        CommitSetting("Debug", "DebugDisableUpdateAction", value, "Отключение действия кнопок обновления");
    }

    partial void OnOverwriteExistingChanged(bool value)
    {
        CommitSetting("General", "OverwriteExisting", value, "Перезапись существующих файлов");
    }

    partial void OnClearListOnAddChanged(bool value)
    {
        CommitSetting("General", "ClearListOnAdd", value, "Очистка списка файлов");
    }

    partial void OnMaxParallelTasksChanged(int value)
    {
        // Если значение меньше 1 (например, слайдер перетащили в крайнее левое положение 0),
        // принудительно возвращаем его к 1 для предотвращения некорректной настройки.
        if (value < 1)
        {
            MaxParallelTasks = 1;
            return;
        }

        CommitSetting("General", "MaxParallelTasks", value, "Число параллельных задач");
    }

    partial void OnEnableParallelChanged(bool value)
    {
        CommitSetting("General", "EnableParallel", value, "Параллельное выполнение задач");
    }

    partial void OnDefaultOutputSubfolderChanged(string value)
    {
        string subfolder = string.IsNullOrEmpty(value)
            ? "KTools_Result"
            : value;
        CommitSetting("General", "DefaultOutputSubfolder", subfolder, "Имя папки результатов");
    }

    partial void OnUseAutoSubfolderChanged(bool value)
    {
        CommitSetting("General", "UseAutoSubfolder", value, "Автопапка результатов");
    }

    partial void OnSelectedThemeIndexChanged(int value)
    {
        string newTheme = value switch
        {
            1 => "Dark",
            2 => "Light",
            _ => "System"
        };

        if (!_isApplyingManagerState)
        {
            CommitSetting("General", "Theme", newTheme, "Тема оформления");
        }

        WeakReferenceMessenger.Default.Send(new ThemeChangedMessage(newTheme));
    }

    partial void OnSelectedBackdropIndexChanged(int value)
    {
        string newBackdrop = value == 1 ? "Acrylic" : "Mica";

        if (!_isApplyingManagerState)
        {
            CommitSetting("General", "BackdropType", newBackdrop, "Тип фона окон");
        }

        WeakReferenceMessenger.Default.Send(new BackdropChangedMessage(newBackdrop));
    }

    partial void OnShowLogsTabChanged(bool value)
    {
        if (!_isApplyingManagerState)
        {
            CommitSetting("Logging", "ShowLogsTab", value, "Вкладка журнала");
        }

        WeakReferenceMessenger.Default.Send(
            new LogsTabVisibilityChangedMessage(value));
    }

    partial void OnAutoCheckUpdatesChanged(bool value)
    {
        CommitSetting("Updates", "AutoCheckUpdates", value, "Проверка обновлений при запуске");
    }

    partial void OnIncludePreReleasesChanged(bool value)
    {
        CommitSetting("Updates", "IncludePreReleases", value, "Предварительные версии");
    }

    partial void OnDebugSimulateOldVersionChanged(bool value)
    {
        CommitSetting("Debug", "DebugSimulateOldVersion", value, "Имитация старой версии");
    }

    partial void OnRenameEnableRegexChanged(bool value)
    {
        CommitSetting("General", "RenameEnableRegex", value, "Переименование по регулярным выражениям");
    }

    partial void OnRenameRegexSearchChanged(string value)
    {
        CommitSetting("General", "RenameRegexSearch", value, "Шаблон поиска при переименовании");
    }

    partial void OnRenameRegexReplaceChanged(string value)
    {
        CommitSetting("General", "RenameRegexReplace", value, "Шаблон замены при переименовании");
    }

    partial void OnRenameUseRegexChanged(bool value)
    {
        CommitSetting("General", "RenameUseRegex", value, "Использование регулярных выражений");
    }

    partial void OnRenameCaseSensitiveChanged(bool value)
    {
        CommitSetting("General", "RenameCaseSensitive", value, "Учёт регистра при переименовании");
    }

    partial void OnIsContextMenuEnabledChanged(bool value)
    {
        if (!_isApplyingManagerState)
        {
            ApplySettingCommit(
                "Интеграция с контекстным меню Проводника",
                PersistenceResult.Normalize(
                    _settingsManager.SetSetting("Shell", "IsContextMenuEnabled", value)));
        }

        bool persisted = _isApplyingManagerState || LastCommitState == SettingCommitState.Persisted;

        try
        {
            if (value)
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                {
                    var scripts = _scriptRegistry.Scripts.Select(s => s.Name).ToList();
                    ShellIntegration.Register(exePath, scripts);
                    _logService.Write(SettingsEventIds.DefaultsDeferred, persisted ? LogLevel.Info : LogLevel.Warning, persisted ? LogStatus.Succeeded : LogStatus.Changed, persisted ? "Интеграция с контекстным меню Проводника включена, настройка сохранена" : "Интеграция с контекстным меню Проводника включена только в текущем сеансе, настройка не сохранена", source: SourceName, properties: LogProps.Create("Key", "ShellIntegrationEnabled").With("Persisted", persisted));
                }
            }
            else
            {
                ShellIntegration.Unregister();
                _logService.Write(SettingsEventIds.DefaultsDeferred, persisted ? LogLevel.Info : LogLevel.Warning, persisted ? LogStatus.Succeeded : LogStatus.Changed, persisted ? "Интеграция с контекстным меню Проводника отключена, настройка сохранена" : "Интеграция с контекстным меню Проводника отключена только в текущем сеансе, настройка не сохранена", source: SourceName, properties: LogProps.Create("Key", "ShellIntegrationEnabled").With("Persisted", persisted));
            }
        }
        catch (Exception ex)
        {
            _logService.Write("settings.shell_integration.change_failed", LogLevel.Warning, LogStatus.Failed, "Состояние интеграции с контекстным меню не изменено", ex, SourceName, properties: LogProps.Create("Key", "ShellIntegrationEnabled").With("ErrorCode", "SHELL_INTEGRATION_CHANGE_FAILED").With("Persisted", false));
            _ = _dialogService.ShowMessageAsync(
                "Ошибка интеграции",
                "Не удалось изменить состояние интеграции с контекстным меню. Подробности записаны в журнал.");
        }
    }

    /// <summary>
    /// Фиксирует пользовательское изменение настройки и публикует типизированный результат.
    /// Значение параметра в журнал и в интерфейс не попадает.
    /// </summary>
    /// <typeparam name="T">Тип значения настройки.</typeparam>
    /// <param name="group">Группа настройки.</param>
    /// <param name="key">Ключ настройки.</param>
    /// <param name="value">Новое значение.</param>
    /// <param name="displayName">Понятное пользователю название настройки.</param>
    private void CommitSetting<T>(string group, string key, T value, string displayName)
    {
        if (_isApplyingManagerState)
        {
            return;
        }

        ApplySettingCommit(
            displayName,
            PersistenceResult.Normalize(_settingsManager.SetSetting(group, key, value)));
    }

    /// <summary>
    /// Переводит типизированный результат persistence в наблюдаемое состояние фиксации.
    /// </summary>
    /// <param name="displayName">Понятное пользователю название настройки.</param>
    /// <param name="result">Результат операции сохранения.</param>
    private void ApplySettingCommit(string displayName, PersistenceResult result)
    {
        LastCommitErrorCode = result.ErrorCode;

        if (result.IsFailure)
        {
            LastCommitState = SettingCommitState.Failed;
            LastCommitStatusText = $"{displayName}: изменено только в текущем сеансе, сохранить не удалось ({result.ErrorCode}). {result.UserSummary}";
            _logService.Write("settings.persistence.deferred", LogLevel.Warning, LogStatus.Changed, LastCommitStatusText, source: SourceName, properties: LogProps.Create("Persisted", false).With("Group", "Persistence"));
            return;
        }

        if (result.IsPending)
        {
            LastCommitState = SettingCommitState.ChangedInMemory;
            LastCommitStatusText = $"{displayName}: применено в текущем сеансе, запись на диск отложена.";
            return;
        }

        if (result.ChangedCount == 0)
        {
            LastCommitState = SettingCommitState.None;
            LastCommitStatusText = $"{displayName}: без изменений.";
            return;
        }

        LastCommitState = SettingCommitState.Persisted;
        LastCommitStatusText = $"{displayName}: сохранено.";
    }

    /// <summary>
    /// Устанавливает путь к директории хранения логов.
    /// Каталог активной сессии журналирования не переинициализируется:
    /// изменение применяется со следующего запуска приложения и только при
    /// подтверждённой записи настройки на диск.
    /// </summary>
    /// <param name="path">Новый путь к каталогу логов.</param>
    /// <returns>Результат сохранения настройки.</returns>
    public PersistenceResult SetLogDirectory(string path)
    {
        PersistenceResult result = PersistenceResult.Normalize(_settingsManager.SetLogDirectory(path));

        if (result.IsSuccess && result.Persisted)
        {
            LogDir = path;
            LastCommitState = SettingCommitState.Persisted;
            LastCommitErrorCode = result.ErrorCode;
            LastCommitStatusText = $"Каталог журналирования сохранён. {PersistenceSummaries.NextLaunchOnly}";
            return result;
        }

        LastCommitState = SettingCommitState.Failed;
        LastCommitErrorCode = result.ErrorCode;
        LastCommitStatusText = $"Каталог журналирования не сохранён ({result.ErrorCode}). {PersistenceSummaries.NextLaunchLost}";
        _logService.Write("settings.persistence.deferred", LogLevel.Warning, LogStatus.Changed, LastCommitStatusText, source: SourceName, properties: LogProps.Create("Persisted", false).With("Group", "Persistence"));
        return result;
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

        if (!confirm)
        {
            return;
        }

        PersistenceResult resetResult = PersistenceResult.Normalize(
            _settingsManager.ResetToDefaults(_scriptRegistry.Scripts, BuildResetDefaults()));

        ApplyResetToViewModel();

        if (resetResult.IsSuccess && resetResult.Persisted)
        {
            LastCommitState = SettingCommitState.Persisted;
            LastCommitErrorCode = PersistenceErrorCodes.None;
            LastCommitStatusText = "Настройки сброшены к значениям по умолчанию и сохранены на диск.";
            await _dialogService.ShowMessageAsync(
                "Настройки сброшены",
                "Все настройки сброшены к значениям по умолчанию и сохранены на диск.");
            return;
        }

        LastCommitState = SettingCommitState.Failed;
        LastCommitErrorCode = resetResult.ErrorCode;
        LastCommitStatusText = $"Сброс настроек не сохранён на диск ({resetResult.ErrorCode}). {resetResult.UserSummary}";
        _logService.Write("settings.persistence.deferred", LogLevel.Warning, LogStatus.Changed, LastCommitStatusText, source: SourceName, properties: LogProps.Create("Persisted", false).With("Group", "Persistence"));

        await _dialogService.ShowMessageAsync(
            "Сброс не сохранён",
            $"Настройки сброшены к значениям по умолчанию, но не записаны на диск ({resetResult.ErrorCode}). {resetResult.UserSummary}");
    }

    /// <summary>
    /// Формирует явный набор значений по умолчанию для сброса.
    /// Шаблоны переименования и флаг интеграции с контекстным меню входят в набор,
    /// поэтому сброс действительно очищает их, а не только переключатели страницы.
    /// </summary>
    private static List<KeyValuePair<string, object?>> BuildResetDefaults()
    {
        return new List<KeyValuePair<string, object?>>(24)
        {
            new("General/OverwriteExisting", false),
            new("General/ClearListOnAdd", false),
            new("General/EnableParallel", true),
            new("General/MaxParallelTasks", Math.Max(1, Environment.ProcessorCount / 2)),
            new("General/DefaultOutputSubfolder", "KTools_Result"),
            new("General/UseAutoSubfolder", false),
            new("General/Theme", "Dark"),
            new("General/BackdropType", "Mica"),
            new("General/RenameEnableRegex", false),
            new("General/RenameRegexSearch", string.Empty),
            new("General/RenameRegexReplace", string.Empty),
            new("General/RenameUseRegex", true),
            new("General/RenameCaseSensitive", false),
            new("General/SearchTemplates", SettingsDefaults.GetDefaultSearchTemplates()),
            new("General/ReplaceTemplates", SettingsDefaults.GetDefaultReplaceTemplates()),
            new("Logging/ShowLogsTab", false),
            new("Logging/LogDir", string.Empty),
            new("Updates/AutoCheckUpdates", true),
            new("Updates/IncludePreReleases", true),
            new("Debug/DebugSimulateOldVersion", false),
            new("Debug/DebugDisableUpdateAction", false),
            new("Shell/IsContextMenuEnabled", false)
        };
    }

    /// <summary>
    /// Синхронизирует свойства модели представления со сброшенным состоянием.
    /// Все изменения проходят через свойства и события, поэтому интеграция с контекстным
    /// меню снимается, а сообщения о теме и фоне отправляются подписчикам.
    /// </summary>
    private void ApplyResetToViewModel()
    {
        _isApplyingManagerState = true;
        try
        {
            OverwriteExisting = false;
            ClearListOnAdd = false;
            EnableParallel = true;
            MaxParallelTasks = Math.Max(1, Environment.ProcessorCount / 2);
            DefaultOutputSubfolder = "KTools_Result";
            UseAutoSubfolder = false;
            SelectedThemeIndex = 1;
            SelectedBackdropIndex = 0;
            ShowLogsTab = false;
            LogDir = string.Empty;
            AutoCheckUpdates = true;
            IncludePreReleases = true;
            RenameEnableRegex = false;
            RenameRegexSearch = string.Empty;
            RenameRegexReplace = string.Empty;
            RenameUseRegex = true;
            RenameCaseSensitive = false;
            DebugSimulateOldVersion = false;
            DebugDisableUpdateAction = false;
            IsContextMenuEnabled = false;
            IsDebugSettingsVisible = false;
        }
        finally
        {
            _isApplyingManagerState = false;
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
            _logService.Write("settings.debug.update_check_blocked", LogLevel.Debug, LogStatus.Skipped, "Проверка обновлений заблокирована отладочным переключателем", source: SourceName, properties: LogProps.Create("Reason", "DebugToggle").With("Group", "Update"));
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
            _logService.Write("app.update.check_failed", LogLevel.Warning, LogStatus.Failed, "Ручная проверка обновлений не завершена", ex, SourceName, properties: LogProps.Create("ErrorCode", "UPDATE_CHECK_FAILED").With("Reason", "Manual").With("Retryable", true));
            await _dialogService.ShowMessageAsync(
                "Ошибка",
                "Не удалось проверить обновления. Подробности записаны в журнал.");
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
            _logService.Write("settings.debug.update_install_blocked", LogLevel.Debug, LogStatus.Skipped, "Загрузка и установка обновлений заблокирована отладочным переключателем", source: SourceName, properties: LogProps.Create("Reason", "DebugToggle").With("Group", "Update"));
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
            _logService.Write("app.update.download_install_failed", LogLevel.Warning, LogStatus.Failed, "Скачивание или установка обновления не завершены", ex, SourceName, properties: LogProps.Create("ErrorCode", "UPDATE_DOWNLOAD_INSTALL_FAILED").With("Retryable", true));
            await _dialogService.ShowMessageAsync(
                "Ошибка",
                "Не удалось загрузить или установить обновление. Подробности записаны в журнал.");
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
        _logService.Write("ui.version_button.clicked", LogLevel.Debug, LogStatus.Changed, $"Нажата кнопка версии {_versionClickCount} из 7", source: SourceName, properties: LogProps.Create("Count", _versionClickCount).With("Total", 7));
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
