// -*- coding: utf-8 -*-
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KTools_App.Core;
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
    /// Указывает, доступна ли кнопка пакетной установки всех компонентов.
    /// </summary>
    [ObservableProperty]
    public partial bool IsInstallAllEnabled { get; set; }

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

        var registry = _dependencyManager.GetRegistry();
        foreach (var dep in registry)
        {
            var vm = new DependencyVM(dep, _dependencyManager);
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
    /// Обновляет логические состояния интерфейса: доступность кнопок и режим кнопки перепроверки.
    /// </summary>
    private void UpdateUIStates()
    {
        bool hasMissing = RequiredDependencies.Concat(OptionalDependencies)
            .Any(d => d.Status == DependencyStatus.NotInstalled || d.Status == DependencyStatus.Error);

        IsInstallAllEnabled = hasMissing;

        bool hasRequired = _dependencyManager.AreRequiredDependenciesInstalled();
        IsHomeState = hasRequired;
        RefreshText = hasRequired ? "На главную" : "Проверить заново";
    }

    private DependencyVM? FindViewModel(string key)
    {
        return RequiredDependencies.Concat(OptionalDependencies)
            .FirstOrDefault(d => d.Info.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    private void OnStatusChanged(string key, DependencyStatus status)
    {
        var vm = FindViewModel(key);
        if (vm != null)
        {
            vm.Status = status;
            UpdateUIStates();
        }
    }

    private void OnProgressChanged(string key, int percent)
    {
        var vm = FindViewModel(key);
        if (vm != null)
        {
            vm.Progress = percent;
        }
    }

    private void OnSpeedUpdated(string key, string speedStr)
    {
        var vm = FindViewModel(key);
        if (vm != null)
        {
            vm.Speed = speedStr;
        }
    }

    private void OnInstallFinished(string key, bool success, string errorMsg)
    {
        var vm = FindViewModel(key);
        if (vm != null)
        {
            if (!success)
            {
                vm.ErrorMessage = errorMsg;
            }
            UpdateUIStates();
            CheckAndRedirectToHome();
        }
    }

    /// <summary>
    /// Выполняет автоматический переход на домашний экран, если все критические зависимости установлены.
    /// </summary>
    private void CheckAndRedirectToHome()
    {
        if (_dependencyManager.AreRequiredDependenciesInstalled())
        {
            _logService.Info(
                "Все обязательные компоненты успешно установлены. Перенаправление на главную страницу.",
                "DependencySetupViewModel");
            _navigationService.NavigateTo(typeof(HomePage));
        }
    }

    /// <summary>
    /// Запускает асинхронный процесс установки одной конкретной зависимости.
    /// </summary>
    [RelayCommand]
    private async Task InstallDependencyAsync(DependencyVM? vm)
    {
        if (vm == null) return;
        _logService.Info($"Запущена ручная установка зависимости: {vm.Info.Key}", "DependencySetupViewModel");
        await _dependencyManager.InstallDependencyAsync(vm.Info.Key);
    }

    /// <summary>
    /// Отменяет текущий процесс скачивания/установки зависимости.
    /// </summary>
    [RelayCommand]
    private void CancelInstallation(DependencyVM? vm)
    {
        if (vm == null) return;
        _logService.Warn($"Пользователь отменил установку зависимости: {vm.Info.Key}", "DependencySetupViewModel");
        _dependencyManager.CancelInstallation(vm.Info.Key);
    }

    /// <summary>
    /// Удаляет установленную зависимость с физического накопителя.
    /// </summary>
    [RelayCommand]
    private void RemoveDependency(DependencyVM? vm)
    {
        if (vm == null) return;
        _logService.Warn($"Пользователь инициировал удаление зависимости: {vm.Info.Key}", "DependencySetupViewModel");
        _dependencyManager.RemoveDependency(vm.Info.Key);
    }

    /// <summary>
    /// Выполняет параллельную пакетную установку всех отсутствующих зависимостей.
    /// </summary>
    [RelayCommand]
    private async Task InstallAllAsync()
    {
        _logService.Info("Запущена пакетная установка всех отсутствующих компонентов", "DependencySetupViewModel");
        IsInstallAllEnabled = false;

        var missing = RequiredDependencies.Concat(OptionalDependencies)
            .Where(d => d.Status == DependencyStatus.NotInstalled || d.Status == DependencyStatus.Error)
            .ToList();

        var tasks = missing.Select(vm => _dependencyManager.InstallDependencyAsync(vm.Info.Key));
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Перепроверяет физическое наличие бинарных компонентов на диске.
    /// Если все обязательные утилиты на месте, перенаправляет пользователя на главный экран.
    /// </summary>
    [RelayCommand]
    private void RefreshAll()
    {
        _logService.Info("Ручная перепроверка статусов компонентов", "DependencySetupViewModel");
        _dependencyManager.RefreshAllStatuses();
        LoadDependencies();

        if (_dependencyManager.AreRequiredDependenciesInstalled())
        {
            _navigationService.NavigateTo(typeof(HomePage));
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

            _logService.Info($"Открытие папки бинарных файлов: '{binDir}'", "DependencySetupViewModel");
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{binDir}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logService.Error($"Не удалось открыть папку бинарных компонентов: {ex.Message}", "DependencySetupViewModel");
        }
    }
}
