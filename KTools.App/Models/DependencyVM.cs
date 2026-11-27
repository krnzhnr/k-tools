// -*- coding: utf-8 -*-
using System;
using System.Threading.Tasks;
using System.Windows.Input;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using KTools_App.Core;
using KTools_App.Services.Contracts;
using KTools_App.ViewModels;

using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace KTools_App.Models;

/// <summary>
/// Представитель слоя представления (ViewModel) для конкретной зависимости.
/// Связывает модель данных зависимости с пользовательским интерфейсом.
/// Наследует ThreadSafeViewModel: создание в фоне безопасно, а уведомления
/// об изменении свойств маршалируются в поток UI (ишью 141).
/// </summary>
public partial class DependencyVM : ThreadSafeViewModel
{
    private readonly IDependencyManager _dependencyManager;

    /// <summary>Ссылка на базовую модель данных зависимости.</summary>
    public DependencyInfo Info { get; }

    /// <summary>Текущий статус установки/загрузки.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusColor))]
    [NotifyPropertyChangedFor(nameof(IsExtracting))]
    [NotifyPropertyChangedFor(nameof(ProgressVisibility))]
    [NotifyPropertyChangedFor(nameof(SubStatusVisibility))]
    [NotifyPropertyChangedFor(nameof(SubStatusText))]
    [NotifyPropertyChangedFor(nameof(InstallButtonVisibility))]
    [NotifyPropertyChangedFor(nameof(UpdateButtonVisibility))]
    [NotifyPropertyChangedFor(nameof(CancelButtonVisibility))]
    [NotifyPropertyChangedFor(nameof(NormalDeleteButtonVisibility))]
    [NotifyPropertyChangedFor(nameof(SmallDeleteButtonVisibility))]
    [NotifyPropertyChangedFor(nameof(BuiltinSourceVisibility))]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    private DependencyStatus _status;

    /// <summary>Признак нестандартного источника бинарника (свой путь или PATH).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CustomSourceVisibility))]
    [NotifyPropertyChangedFor(nameof(BuiltinSourceVisibility))]
    [NotifyPropertyChangedFor(nameof(InstallButtonVisibility))]
    [NotifyPropertyChangedFor(nameof(UpdateButtonVisibility))]
    [NotifyPropertyChangedFor(nameof(NormalDeleteButtonVisibility))]
    [NotifyPropertyChangedFor(nameof(SmallDeleteButtonVisibility))]
    private bool _isCustomSource;

    /// <summary>Текущий эффективный путь к исполняемому файлу (для своего источника).</summary>
    [ObservableProperty]
    private string _currentBinaryPath = string.Empty;

    /// <summary>
    /// Делегат выбора исполняемого файла через системный диалог (ишью 54).
    /// Задаётся моделью страницы; в юнит-тестах подменяется лямбдой.
    /// </summary>
    public Func<Task<string?>>? PickCustomBinaryPathAsync { get; set; }

    partial void OnStatusChanged(DependencyStatus value)
    {
        IsUpdateAvailable = _dependencyManager.IsUpdateAvailable(Info.Key);
        if (value == DependencyStatus.Installed)
        {
            LoadVersionAsync();
        }
        else if (value == DependencyStatus.NotInstalled)
        {
            InstalledVersion = string.Empty;
        }
    }

    /// <summary>Текущий прогресс скачивания в процентах (0-100).</summary>
    [ObservableProperty]
    private int _progress;

    /// <summary>Текущая скорость скачивания.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubStatusText))]
    private string _speed = string.Empty;

    /// <summary>Сообщение об ошибке, возникшей при установке.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubStatusText))]
    private string _errorMessage = string.Empty;

    /// <summary>Флаг доступности обновления для данной зависимости.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusColor))]
    [NotifyPropertyChangedFor(nameof(UpdateButtonVisibility))]
    [NotifyPropertyChangedFor(nameof(NormalDeleteButtonVisibility))]
    [NotifyPropertyChangedFor(nameof(SmallDeleteButtonVisibility))]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    private bool _isUpdateAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    private string _installedVersion = string.Empty;

    /// <summary>Форматированный текст размера компонента и версии.</summary>
    public string SizeText => Status == DependencyStatus.Installed && !string.IsNullOrEmpty(InstalledVersion)
        ? $"Версия: {InstalledVersion}"
        : $"~{Info.SizeMb:F1} МБ (архив ~{Info.ArchiveSizeMb:F1} МБ)";

    /// <summary>Локализованный текст статуса зависимости.</summary>
    public string StatusText => Status switch
    {
        DependencyStatus.Installed => IsUpdateAvailable ? "Доступно обновление" : "Установлено",
        DependencyStatus.NotInstalled => "Не установлено",
        DependencyStatus.Downloading => "Загрузка архива...",
        DependencyStatus.Extracting => "Распаковка архива...",
        DependencyStatus.Error => "Ошибка установки",
        _ => "Неизвестный статус"
    };

    /// <summary>Цветовое оформление индикатора статуса в зависимости от состояния.</summary>
    public Brush StatusColor => Status switch
    {
        DependencyStatus.Installed => IsUpdateAvailable
            ? new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0xE3, 0x72, 0x0C))  // Оранжевый акцент при обновлении
            : new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x10, 0x7C, 0x41)), // Насыщенный зеленый
        DependencyStatus.Error => new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0xE8, 0x11, 0x23)),      // Красный
        DependencyStatus.Downloading => new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x00, 0x78, 0xD4)), // Fluent-синий
        DependencyStatus.Extracting => new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x00, 0x78, 0xD4)),  // Fluent-синий
        _ => new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x7A, 0x7A, 0x7A))                             // Нейтральный серый
    };

    /// <summary>Указывает, выполняется ли в данный момент распаковка архива.</summary>
    public bool IsExtracting => Status == DependencyStatus.Extracting;

    /// <summary>Определяет видимость прогресс-бара.</summary>
    public Visibility ProgressVisibility =>
        (Status == DependencyStatus.Downloading || Status == DependencyStatus.Extracting)
        ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Определяет видимость дополнительного текстового блока скорости/ошибки.</summary>
    public Visibility SubStatusVisibility =>
        (Status == DependencyStatus.Downloading || Status == DependencyStatus.Error)
        ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Дополнительный текст состояния (скорость загрузки или сообщение об ошибке).</summary>
    public string SubStatusText => Status switch
    {
        DependencyStatus.Downloading => Speed,
        DependencyStatus.Error => ErrorMessage,
        _ => string.Empty
    };

    /// <summary>Определяет видимость кнопки установки.</summary>
    public Visibility InstallButtonVisibility =>
        !IsCustomSource && (Status == DependencyStatus.NotInstalled || Status == DependencyStatus.Error)
        ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Определяет видимость кнопки обновления.</summary>
    public Visibility UpdateButtonVisibility =>
        !IsCustomSource && Status == DependencyStatus.Installed && IsUpdateAvailable
        ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Определяет видимость кнопки отмены.</summary>
    public Visibility CancelButtonVisibility =>
        (Status == DependencyStatus.Downloading || Status == DependencyStatus.Extracting)
        ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Определяет видимость стандартной большой кнопки удаления.</summary>
    public Visibility NormalDeleteButtonVisibility =>
        !IsCustomSource && Status == DependencyStatus.Installed && !IsUpdateAvailable
        ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Определяет видимость маленькой квадратной кнопки удаления рядом с Обновить.</summary>
    public Visibility SmallDeleteButtonVisibility =>
        !IsCustomSource && Status == DependencyStatus.Installed && IsUpdateAvailable
        ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Определяет видимость панели своего пути (вместо кнопок Скачать/Удалить).</summary>
    public Visibility CustomSourceVisibility =>
        IsCustomSource ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Определяет видимость панели выбора своего источника (встроенный режим,
    /// вне активных операций: смена источника во время скачивания запрещена).
    /// </summary>
    public Visibility BuiltinSourceVisibility =>
        !IsCustomSource && Status != DependencyStatus.Downloading && Status != DependencyStatus.Extracting
        ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Команда установки данной зависимости.</summary>
    public ICommand InstallCommand { get; }

    /// <summary>Команда обновления данной зависимости.</summary>
    public ICommand UpdateCommand { get; }

    /// <summary>Команда отмены установки данной зависимости.</summary>
    public ICommand CancelCommand { get; }

    /// <summary>Команда удаления данной зависимости.</summary>
    public ICommand RemoveCommand { get; }

    /// <summary>
    /// Обновляет признаки своего источника бинарника из менеджера (ишью 54).
    /// </summary>
    public void RefreshBinarySource()
    {
        BinarySource source = _dependencyManager.GetBinarySource(Info.Key);
        IsCustomSource = source != BinarySource.Builtin;
        if (!IsCustomSource)
        {
            CurrentBinaryPath = string.Empty;
            return;
        }

        string path = source == BinarySource.Custom
            ? (_dependencyManager.GetCustomBinaryPath(Info.Key) ?? string.Empty)
            : _dependencyManager.GetEffectiveBinaryPath(Info.Key);
        CurrentBinaryPath = string.IsNullOrWhiteSpace(path)
            ? "Не найдено в системном PATH"
            : path;
    }

    /// <summary>Инициализирует новый экземпляр ViewModel для зависимости.</summary>
    public DependencyVM(DependencyInfo info, IDependencyManager dependencyManager)
    {
        // Очередь диспетчера здесь не захватывается: базовый ThreadSafeViewModel
        // резолвит её лениво, поэтому конструктор безопасен в фоновом потоке (ишью 141).
        Info = info;
        _dependencyManager = dependencyManager ?? throw new ArgumentNullException(nameof(dependencyManager));
        _status = _dependencyManager.GetStatus(info.Key);
        _isUpdateAvailable = _dependencyManager.IsUpdateAvailable(info.Key);
        _progress = _dependencyManager.GetDownloadProgress(info.Key);
        _speed = _dependencyManager.GetDownloadSpeed(info.Key);

        InstallCommand = new AsyncRelayCommand(async () =>
            await _dependencyManager.InstallDependencyAsync(Info.Key));

        UpdateCommand = new AsyncRelayCommand(async () =>
            await _dependencyManager.InstallDependencyAsync(Info.Key));

        CancelCommand = new RelayCommand(() =>
            _dependencyManager.CancelInstallation(Info.Key));

        RemoveCommand = new AsyncRelayCommand(async () =>
            await _dependencyManager.RemoveDependencyAsync(Info.Key));

        RefreshBinarySource();
        LoadVersionAsync();
    }

    /// <summary>
    /// Фоновая асинхронная загрузка версии без блокировки UI-потока.
    /// </summary>
    public void LoadVersionAsync()
    {
        if (Status != DependencyStatus.Installed) return;

        _ = Task.Run(async () =>
        {
            string ver = await _dependencyManager.GetInstalledVersionAsync(Info.Key);
            if (!string.IsNullOrEmpty(ver))
            {
                // Присвоение безопасно с любого потока: базовый ThreadSafeViewModel
                // маршалит уведомление об изменении в поток UI (ишью 141).
                InstalledVersion = ver;
            }
        });
    }

    /// <summary>
    /// Открывает диалог выбора исполняемого файла и задаёт свой путь (ишью 54).
    /// Без заданного делегата выбора — ничего не делает.
    /// </summary>
    [RelayCommand]
    private async Task SelectCustomPathAsync()
    {
        if (PickCustomBinaryPathAsync == null)
        {
            return;
        }

        string? path = await PickCustomBinaryPathAsync().ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        PersistenceResult result = PersistenceResult.Normalize(
            _dependencyManager.SetCustomBinaryPath(Info.Key, path));
        if (!result.IsFailure)
        {
            RefreshBinarySource();
        }
    }

    /// <summary>
    /// Включает поиск исполняемого файла в системном PATH (ишью 54).
    /// </summary>
    [RelayCommand]
    private void UseSystemPath()
    {
        PersistenceResult result = PersistenceResult.Normalize(
            _dependencyManager.SetBinaryUseSystemPath(Info.Key, true));
        if (!result.IsFailure)
        {
            RefreshBinarySource();
        }
    }

    /// <summary>
    /// Сбрасывает источник зависимости к встроенным бинарникам (ишью 54).
    /// </summary>
    [RelayCommand]
    private void ResetBinarySource()
    {
        PersistenceResult result = PersistenceResult.Normalize(
            _dependencyManager.ClearBinarySource(Info.Key));
        if (!result.IsFailure)
        {
            RefreshBinarySource();
        }
    }
}
