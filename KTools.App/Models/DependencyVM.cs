// -*- coding: utf-8 -*-
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Dispatching;
using CommunityToolkit.Mvvm.Input;
using KTools_App.Core;
using KTools_App.Services.Contracts;

namespace KTools_App.Models;

/// <summary>
/// Представитель слоя представления (ViewModel) для конкретной зависимости.
/// Связывает модель данных зависимости с пользовательским интерфейсом.
/// Реализует INotifyPropertyChanged для мгновенного обновления UI.
/// </summary>
public class DependencyVM : INotifyPropertyChanged
{
    private readonly DispatcherQueue? _dispatcherQueue;
    private DependencyStatus _status;
    private int _progress;
    private string _speed = string.Empty;
    private string _errorMessage = string.Empty;

    /// <summary>Ссылка на базовую модель данных зависимости.</summary>
    public DependencyInfo Info { get; }

    /// <summary>Текущий статус установки/загрузки.</summary>
    public DependencyStatus Status
    {
        get => _status;
        set
        {
            if (_status != value)
            {
                _status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(StatusColor));
                OnPropertyChanged(nameof(IsExtracting));
                OnPropertyChanged(nameof(ProgressVisibility));
                OnPropertyChanged(nameof(SubStatusVisibility));
                OnPropertyChanged(nameof(SubStatusText));
                OnPropertyChanged(nameof(InstallButtonVisibility));
                OnPropertyChanged(nameof(CancelButtonVisibility));
                OnPropertyChanged(nameof(DeleteButtonVisibility));
            }
        }
    }

    /// <summary>Текущий прогресс скачивания в процентах (0-100).</summary>
    public int Progress
    {
        get => _progress;
        set
        {
            if (_progress != value)
            {
                _progress = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Текущая скорость скачивания.</summary>
    public string Speed
    {
        get => _speed;
        set
        {
            if (_speed != value)
            {
                _speed = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SubStatusText));
            }
        }
    }

    /// <summary>Сообщение об ошибке, возникшей при установке.</summary>
    public string ErrorMessage
    {
        get => _errorMessage;
        set
        {
            if (_errorMessage != value)
            {
                _errorMessage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SubStatusText));
            }
        }
    }

    /// <summary>Форматированный текст размера компонента.</summary>
    public string SizeText => $"~{Info.SizeMb:F1} МБ (архив ~{Info.ArchiveSizeMb:F1} МБ)";

    /// <summary>Локализованный текст статуса зависимости.</summary>
    public string StatusText => Status switch
    {
        DependencyStatus.Installed => "Установлено",
        DependencyStatus.NotInstalled => "Не установлено",
        DependencyStatus.Downloading => "Загрузка архива...",
        DependencyStatus.Extracting => "Распаковка архива...",
        DependencyStatus.Error => "Ошибка установки",
        _ => "Неизвестный статус"
    };

    /// <summary>Цветовое оформление индикатора статуса в зависимости от состояния.</summary>
    public Brush StatusColor => Status switch
    {
        DependencyStatus.Installed => new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x10, 0x7C, 0x41)), // Насыщенный зеленый
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
        (Status == DependencyStatus.NotInstalled || Status == DependencyStatus.Error) 
        ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Определяет видимость кнопки отмены.</summary>
    public Visibility CancelButtonVisibility => 
        (Status == DependencyStatus.Downloading || Status == DependencyStatus.Extracting) 
        ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Определяет видимость кнопки удаления.</summary>
    public Visibility DeleteButtonVisibility => 
        Status == DependencyStatus.Installed ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Команда установки данной зависимости.</summary>
    public ICommand InstallCommand { get; }

    /// <summary>Команда отмены установки данной зависимости.</summary>
    public ICommand CancelCommand { get; }

    /// <summary>Команда удаления данной зависимости.</summary>
    public ICommand RemoveCommand { get; }

    private readonly IDependencyManager _dependencyManager;

    /// <summary>Инициализирует новый экземпляр ViewModel для зависимости.</summary>
    public DependencyVM(DependencyInfo info, IDependencyManager dependencyManager)
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        Info = info;
        _dependencyManager = dependencyManager ?? throw new ArgumentNullException(nameof(dependencyManager));
        _status = _dependencyManager.GetStatus(info.Key);

        InstallCommand = new AsyncRelayCommand(async () => 
            await _dependencyManager.InstallDependencyAsync(Info.Key));

        CancelCommand = new RelayCommand(() => 
            _dependencyManager.CancelInstallation(Info.Key));

        RemoveCommand = new RelayCommand(() => 
            _dependencyManager.RemoveDependency(Info.Key));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        var handler = PropertyChanged;
        if (handler != null)
        {
            if (_dispatcherQueue != null && !_dispatcherQueue.HasThreadAccess)
            {
                _dispatcherQueue.TryEnqueue(() => handler.Invoke(this, new PropertyChangedEventArgs(propertyName)));
            }
            else
            {
                handler.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }
}
