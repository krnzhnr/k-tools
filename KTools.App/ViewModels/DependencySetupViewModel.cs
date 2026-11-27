// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Models;
using KTools_App.Services.Contracts;
using KTools_App.UI.Pages;

namespace KTools_App.ViewModels;

/// <summary>
/// Модель представления для страницы настройки и установки зависимостей (DependencySetupPage).
/// Управляет жизненным циклом внешних бинарных компонентов и координирует вызовы DependencyManager.
/// </summary>
public partial class DependencySetupViewModel : ThreadSafeViewModel
{
    private const string SourceName = nameof(DependencySetupViewModel);
    private readonly IDependencyManager _dependencyManager;
    private readonly INavigationService _navigationService;
    private readonly ILogService _logService;
    private readonly IPathManager _pathManager;

    /// <summary>
    /// Коллекция обязательных зависимостей приложения.
    /// </summary>
    [ObservableProperty]
    public partial ObservableCollection<DependencyVM> RequiredDependencies { get; set; } = new();

    /// <summary>
    /// Коллекция необязательных зависимостей приложения.
    /// </summary>
    [ObservableProperty]
    public partial ObservableCollection<DependencyVM> OptionalDependencies { get; set; } = new();

    /// <summary>
    /// Словарь для быстрого поиска ViewModel зависимости по её ключу.
    /// </summary>
    private readonly Dictionary<string, DependencyVM> _dependencyLookup = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Последние доложенные в UI значения прогресса и скорости по ключу зависимости (ишью 141).
    /// Фон скачивания шлёт события на каждый чанк: повторы отбрасываются до записи,
    /// чтобы не штормить очередь диспетчера. Доступ синхронизирован, т.к. события
    /// приходят с фоновых потоков.
    /// </summary>
    private readonly Dictionary<string, int> _lastReportedPercent = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string> _lastReportedSpeed = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _progressSync = new();

    /// <summary>
    /// Делегат выбора исполняемого файла через системный диалог (ишью 54).
    /// По умолчанию открывает диалог *.exe, в юнит-тестах подменяется лямбдой.
    /// </summary>
    public Func<Task<string?>> BinaryPathPicker { get; set; }

    /// <summary>
    /// Указывает, доступна ли кнопка установки обязательных компонентов.
    /// </summary>
    [ObservableProperty]
    public partial bool IsInstallRequiredEnabled { get; set; }

    /// <summary>
    /// Указывает, находится ли интерфейс в состоянии готовности к переходу на главный экран.
    /// </summary>
    [ObservableProperty]
    public partial bool IsHomeState { get; set; }

    /// <summary>
    /// Текст на кнопке обновления/возврата на главный экран.
    /// </summary>
    [ObservableProperty]
    public partial string RefreshText { get; set; } = "Проверить заново";

    /// <summary>
    /// Инициализирует новый экземпляр DependencySetupViewModel.
    /// </summary>
    public DependencySetupViewModel(
        IDependencyManager dependencyManager,
        INavigationService navigationService,
        ILogService logService,
        IPathManager pathManager)
    {
        _dependencyManager = dependencyManager ?? throw new ArgumentNullException(nameof(dependencyManager));
        _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _pathManager = pathManager ?? throw new ArgumentNullException(nameof(pathManager));
        BinaryPathPicker = PickBinaryPathWithDialogAsync;

        LoadDependencies();

        _dependencyManager.StatusChanged += OnStatusChanged;
        _dependencyManager.ProgressChanged += OnProgressChanged;
        _dependencyManager.SpeedUpdated += OnSpeedUpdated;
        _dependencyManager.InstallFinished += OnInstallFinished;
    }

    /// <summary>
    /// Освобождает подписки на события DependencyManager при уничтожении ViewModel.
    /// </summary>
    public void Cleanup()
    {
        _dependencyManager.StatusChanged -= OnStatusChanged;
        _dependencyManager.ProgressChanged -= OnProgressChanged;
        _dependencyManager.SpeedUpdated -= OnSpeedUpdated;
        _dependencyManager.InstallFinished -= OnInstallFinished;
    }

    /// <summary>
    /// Загружает список зарегистрированных зависимостей и распределяет их по категориям.
    /// </summary>
    private void LoadDependencies()
    {
        RequiredDependencies.Clear();
        OptionalDependencies.Clear();
        _dependencyLookup.Clear();
        lock (_progressSync)
        {
            _lastReportedPercent.Clear();
            _lastReportedSpeed.Clear();
        }

        var registry = _dependencyManager.GetRegistry();
        foreach (var dep in registry)
        {
            var vm = new DependencyVM(dep, _dependencyManager)
            {
                PickCustomBinaryPathAsync = BinaryPathPicker
            };
            _dependencyLookup[dep.Key] = vm;
            if (dep.IsRequired)
            {
                RequiredDependencies.Add(vm);
            }
            else
            {
                OptionalDependencies.Add(vm);
            }
        }

        UpdateUIStates();
    }

    /// <summary>
    /// Обновляет логические состояния интерфейса: доступность кнопки и режим кнопки перепроверки.
    /// Кнопка учитывает только обязательные зависимости: необязательные ставятся по одной с карточки.
    /// </summary>
    private void UpdateUIStates()
    {
        bool hasMissing = RequiredDependencies
            .Any(d => d.Status == DependencyStatus.NotInstalled || d.Status == DependencyStatus.Error);

        IsInstallRequiredEnabled = hasMissing;
        RefreshText = "Проверить обновления";
    }

    private DependencyVM? FindViewModel(string key)
    {
        _dependencyLookup.TryGetValue(key, out var vm);
        return vm;
    }

    private void OnStatusChanged(string key, DependencyStatus status)
    {
        var vm = FindViewModel(key);
        if (vm != null)
        {
            vm.Status = status;
            vm.RefreshBinarySource();
            UpdateUIStates();
        }
    }

    private void OnProgressChanged(string key, int percent)
    {
        bool isNew;
        lock (_progressSync)
        {
            isNew = !_lastReportedPercent.TryGetValue(key, out int previous) || previous != percent;
            if (isNew)
            {
                _lastReportedPercent[key] = percent;
            }
        }

        if (!isNew)
        {
            return;
        }

        DependencyVM? vm = FindViewModel(key);
        if (vm != null)
        {
            // Присвоение безопасно с любого потока: DependencyVM наследует
            // ThreadSafeViewModel и маршалит уведомление через DispatcherQueue.
            vm.Progress = percent;
        }
    }

    private void OnSpeedUpdated(string key, string speedText)
    {
        bool isNew;
        lock (_progressSync)
        {
            isNew = !_lastReportedSpeed.TryGetValue(key, out string? previous)
                || !string.Equals(previous, speedText, StringComparison.Ordinal);
            if (isNew)
            {
                _lastReportedSpeed[key] = speedText;
            }
        }

        if (!isNew)
        {
            return;
        }

        DependencyVM? vm = FindViewModel(key);
        if (vm != null)
        {
            vm.Speed = speedText;
        }
    }

    private void OnInstallFinished(string key, bool success, string errorMsg)
    {
        var vm = FindViewModel(key);
        if (vm != null)
        {
            vm.IsUpdateAvailable = _dependencyManager.IsUpdateAvailable(key);
            if (!success)
            {
                vm.ErrorMessage = errorMsg;
            }
            else
            {
                vm.ErrorMessage = string.Empty;
                vm.LoadVersionAsync();
            }
            UpdateUIStates();
        }
    }

    /// <summary>
    /// Запускает асинхронный процесс установки одной конкретной зависимости.
    /// </summary>
    [RelayCommand]
    private async Task InstallDependencyAsync(DependencyVM? vm)
    {
        if (vm == null) return;
        _logService.Write("ui.dependencies.install_requested", LogLevel.Info, LogStatus.Running, $"Пользователь запустил установку компонента '{LogRedactor.CompactSafeToken(vm.Info.Key)}'", source: SourceName, properties: LogProps.Create("Key", LogRedactor.CompactSafeToken(vm.Info.Key)));
        await _dependencyManager.InstallDependencyAsync(vm.Info.Key);
    }

    /// <summary>
    /// Отменяет текущий процесс скачивания/установки зависимости.
    /// </summary>
    [RelayCommand]
    private void CancelInstallation(DependencyVM? vm)
    {
        if (vm == null) return;
        _logService.Write(
            "dependency.install_cancelled",
            LogLevel.Info,
            LogStatus.Cancelled,
            $"Пользователь отменил установку зависимости '{vm.Info.Key}'",
            null,
            "DependencySetupViewModel",
            properties: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Key"] = vm.Info.Key,
                ["ErrorCode"] = "cancelled"
            });
        _dependencyManager.CancelInstallation(vm.Info.Key);
    }

    /// <summary>
    /// Удаляет установленную зависимость с физического накопителя.
    /// </summary>
    [RelayCommand]
    private async Task RemoveDependencyAsync(DependencyVM? vm)
    {
        if (vm == null) return;
        _logService.Write("ui.dependencies.remove_requested", LogLevel.Info, LogStatus.Running, $"Пользователь запустил удаление компонента '{LogRedactor.CompactSafeToken(vm.Info.Key)}'", source: SourceName, properties: LogProps.Create("Key", LogRedactor.CompactSafeToken(vm.Info.Key)));
        await _dependencyManager.RemoveDependencyAsync(vm.Info.Key);
    }

    /// <summary>
    /// Выполняет параллельную установку всех отсутствующих обязательных зависимостей.
    /// Необязательные компоненты кнопкой не ставятся: их пользователь ставит по одному с карточки.
    /// </summary>
    [RelayCommand]
    private async Task InstallRequiredAsync()
    {
        _logService.Write("ui.dependencies.install_required_requested", LogLevel.Info, LogStatus.Running, "Пользователь запустил установку обязательных компонентов", source: SourceName, properties: LogProps.Create("Group", "Required"));
        IsInstallRequiredEnabled = false;

        var missing = RequiredDependencies
            .Where(d => d.Status == DependencyStatus.NotInstalled || d.Status == DependencyStatus.Error)
            .ToList();

        var tasks = missing.Select(vm => _dependencyManager.InstallDependencyAsync(vm.Info.Key));
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Ручная принудительная проверка обновлений всех зависимостей (FFmpeg, MKVToolNix, eac3to, yt-dlp).
    /// </summary>
    [RelayCommand]
    private async Task RefreshAll()
    {
        _logService.Write("ui.dependencies.update_check_requested", LogLevel.Info, LogStatus.Running, "Пользователь запустил ручную проверку обновлений компонентов", source: SourceName, properties: LogProps.Create("Group", "AllDependencies"));
        _dependencyManager.RefreshAllStatuses();
        await _dependencyManager.CheckAllDependencyUpdatesAsync(force: true);
        LoadDependencies();
    }

    /// <summary>
    /// Открывает системный диалог выбора исполняемого файла компонента (ишью 54).
    /// Возвращает путь к выбранному файлу или null при отмене/ошибке.
    /// </summary>
    private async Task<string?> PickBinaryPathWithDialogAsync()
    {
        _logService.Write(
            "ui.file_picker.opening",
            LogLevel.Debug,
            LogStatus.Running,
            "Открывается системный диалог выбора исполняемого файла компонента",
            source: SourceName,
            properties: LogProps.Create("Control", "BinaryPathPicker"));
        try
        {
            Microsoft.UI.Xaml.Window? window = App.CurrentMainWindow;
            if (window == null)
            {
                return null;
            }

            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(windowId);
            picker.ViewMode = Microsoft.Windows.Storage.Pickers.PickerViewMode.List;
            picker.SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
            picker.FileTypeFilter.Add(".exe");

            var file = await picker.PickSingleFileAsync();
            return file?.Path;
        }
        catch (Exception ex)
        {
            _logService.Write(
                "ui.file_picker.failed",
                LogLevel.Warning,
                LogStatus.Failed,
                "Исполняемый файл компонента не выбран",
                ex,
                SourceName,
                properties: LogProps
                    .Create("ErrorCode", "BINARY_PICKER_FAILED")
                    .With("Control", "BinaryPathPicker"));
            return null;
        }
    }

    /// <summary>
    /// Открывает локальную папку bin в Проводнике Windows.
    /// </summary>
    [RelayCommand]
    private void OpenFolder()
    {
        string binDir = _pathManager.GetBinDirectory();
        try
        {
            if (!Directory.Exists(binDir))
            {
                Directory.CreateDirectory(binDir);
            }

            _logService.Write(
                "ui.dependencies.bin_folder_opened",
                LogLevel.Debug,
                LogStatus.Succeeded,
                "Открыта папка с исполняемыми файлами компонентов",
                source: SourceName,
                properties: LogProps.Create("FileName", LogProps.FileName(binDir)));
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{binDir}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logService.Write(
                "dependency.open_folder_failed",
                LogLevel.Warning,
                LogStatus.PartiallySucceeded,
                "Не удалось открыть папку бинарных компонентов",
                ex,
                "DependencySetupViewModel",
                properties: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Stage"] = "open_folder",
                    ["ErrorCode"] = "open-folder-failed"
                });
        }
    }
}
