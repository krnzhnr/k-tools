// -*- coding: utf-8 -*-
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Extensions.DependencyInjection;
using KTools_App.Services.Contracts;

namespace KTools_App.Core;

/// <summary>
/// Перечисление возможных состояний обработки файла в очереди.
/// </summary>
public enum FileProcessingState
{
    /// <summary>Файл ожидает начала обработки.</summary>
    Pending,
    
    /// <summary>Файл находится в процессе активной обработки.</summary>
    Processing,
    
    /// <summary>Обработка файла успешно завершена.</summary>
    Completed,
    
    /// <summary>Обработка файла была пропущена.</summary>
    Skipped,
    
    /// <summary>Во время обработки файла возникла ошибка.</summary>
    Failed,
    
    /// <summary>Обработка файла была отменена пользователем.</summary>
    Cancelled
}

/// <summary>
/// Класс элемента очереди файлов. Содержит метаданные файла
/// и состояние его обработки.
/// </summary>
public sealed class FileQueueItem : INotifyPropertyChanged
{
    private readonly DispatcherQueue? _dispatcherQueue;
    private double _progress;
    private string _status = "Ожидание";
    private FileProcessingState _state = FileProcessingState.Pending;
    private bool _isProcessing;
    private MediaStructure? _mediaInfo;

    public FileQueueItem(string filePath)
    {
        try
        {
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        }
        catch
        {
            // Игнорируем ошибку при вызове в юнит-тестах вне WinUI потока
            _dispatcherQueue = null;
        }
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        
        bool isUrl = !string.IsNullOrEmpty(filePath) && (
            filePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || 
            filePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        );

        if (!isUrl)
        {
            try
            {
                var info = new FileInfo(filePath);
                FileSizeStr = $"{info.Length / (1024.0 * 1024.0):F2} МБ";
            }
            catch
            {
                FileSizeStr = "Неизвестно";
            }
        }
        else
        {
            FileSizeStr = "Ссылка";
        }
    }

    public string FilePath { get; }
    public string FileName { get; }
    public string FileSizeStr { get; }

    public MediaStructure? MediaInfo
    {
        get => _mediaInfo;
        set
        {
            if (_mediaInfo != value)
            {
                _mediaInfo = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Текущее состояние обработки файла.
    /// </summary>
    public FileProcessingState State
    {
        get => _state;
        set
        {
            if (_state != value)
            {
                _state = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProgressRingVisibility));
                OnPropertyChanged(nameof(StatusIconVisibility));
                OnPropertyChanged(nameof(IsProgressIndeterminate));
                OnPropertyChanged(nameof(StatusIcon));
                OnPropertyChanged(nameof(StatusIconBrush));
                OnPropertyChanged(nameof(IsDeleteEnabled));
            }
        }
    }

    /// <summary>
    /// Индивидуальный прогресс обработки файла (0-100%).
    /// </summary>
    public double Progress
    {
        get => _progress;
        set
        {
            if (Math.Abs(_progress - value) > 0.01)
            {
                _progress = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProgressText));
                OnPropertyChanged(nameof(IsProgressIndeterminate));
            }
        }
    }

    /// <summary>
    /// Текстовый статус файла (например, "Ожидание", "Обработка...").
    /// </summary>
    public string Status
    {
        get => _status;
        set
        {
            string newStatus = value ?? "Ожидание";
            if (_status != newStatus)
            {
                _status = newStatus;
                OnPropertyChanged();
            }
        }
    }

    public string ProgressText => $"{Progress:F0}%";

    /// <summary>
    /// Видимость кольцевого прогресс-бара. Показывается только в состоянии Processing.
    /// </summary>
    public Visibility ProgressRingVisibility
    {
        get
        {
            return State == FileProcessingState.Processing ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Видимость иконки статуса обработки файла. Скрывает иконку во время выполнения активной обработки файла,
    /// чтобы вместо нее отображался кольцевой индикатор прогресса (ProgressRing).
    /// </summary>
    public Visibility StatusIconVisibility
    {
        get
        {
            return ProgressRingVisibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
    }

    /// <summary>
    /// Указывает, является ли прогресс неопределенным (когда обработка только началась и прогресс равен 0).
    /// </summary>
    public bool IsProgressIndeterminate
    {
        get
        {
            return State == FileProcessingState.Processing && Progress <= 0.0;
        }
    }

    /// <summary>
    /// Указывает, обрабатывается ли данный файл в текущий момент времени.
    /// Влияет на доступность кнопки удаления файла из очереди, а также на видимость кольцевого индикатора.
    /// </summary>
    public bool IsProcessing
    {
        get => _isProcessing;
        set
        {
            if (_isProcessing != value)
            {
                _isProcessing = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsDeleteEnabled));
            }
        }
    }

    /// <summary>
    /// Указывает, разрешено ли удаление данного файла из очереди.
    /// </summary>
    public bool IsDeleteEnabled => !IsProcessing;

    /// <summary>
    /// Иконка статуса обработки файла.
    /// </summary>
    public Symbol StatusIcon
    {
        get
        {
            switch (State)
            {
                case FileProcessingState.Completed:
                case FileProcessingState.Skipped:
                    return Symbol.Accept;
                case FileProcessingState.Failed:
                case FileProcessingState.Cancelled:
                    return Symbol.Cancel;
                default:
                    return Symbol.Clock;
            }
        }
    }

    /// <summary>
    /// Цвет иконки статуса.
    /// </summary>
    public Brush StatusIconBrush
    {
        get
        {
            switch (State)
            {
                case FileProcessingState.Completed:
                case FileProcessingState.Skipped:
                    return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 34, 180, 115)); // Зеленый
                case FileProcessingState.Failed:
                    return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 232, 17, 35));  // Красный
                case FileProcessingState.Cancelled:
                    return (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
                default:
                    return (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
            }
        }
    }

    private DownloadFormatItem? _selectedFormat;
    private DownloadSubtitleItem? _selectedSubtitle;
    private string _displayName = string.Empty;

    public System.Collections.ObjectModel.ObservableCollection<DownloadFormatItem> AvailableFormats { get; } = new();
    public System.Collections.ObjectModel.ObservableCollection<DownloadSubtitleItem> AvailableSubtitles { get; } = new();

    /// <summary>
    /// Выбранный формат/качество для скачивания.
    /// </summary>
    public DownloadFormatItem? SelectedFormat
    {
        get => _selectedFormat;
        set
        {
            if (_selectedFormat != value)
            {
                _selectedFormat = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Выбранные субтитры для скачивания.
    /// </summary>
    public DownloadSubtitleItem? SelectedSubtitle
    {
        get => _selectedSubtitle;
        set
        {
            if (_selectedSubtitle != value)
            {
                _selectedSubtitle = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Указывает, нужно ли показывать выбор субтитров.
    /// </summary>
    public bool ShowSubtitlesSelector
    {
        get
        {
            var settingsManager = App.Services.GetService<ISettingsManager>();
            if (settingsManager != null)
            {
                return settingsManager.GetSetting<bool>("Script_Загрузка_медиа", "DownloadSubtitles", false);
            }
            return false;
        }
    }

    /// <summary>
    /// Оповещает UI о возможном изменении флага отображения селектора субтитров.
    /// </summary>
    public void NotifySubtitlesSettingChanged()
    {
        OnPropertyChanged(nameof(ShowSubtitlesSelector));
    }

    /// <summary>
    /// Отображаемое имя файла или видеоролика (например, полученное с YouTube).
    /// </summary>
    public string DisplayName
    {
        get => string.IsNullOrEmpty(_displayName) ? FileName : _displayName;
        set
        {
            if (_displayName != value)
            {
                _displayName = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? prop = null)
    {
        var handler = PropertyChanged;
        if (handler != null)
        {
            if (_dispatcherQueue != null && !_dispatcherQueue.HasThreadAccess)
            {
                _dispatcherQueue.TryEnqueue(() => handler.Invoke(this, new PropertyChangedEventArgs(prop)));
            }
            else
            {
                handler.Invoke(this, new PropertyChangedEventArgs(prop));
            }
        }
    }
}

public sealed class DownloadFormatItem
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string FormatArg { get; set; } = "";

    public override string ToString() => DisplayName;
}

public sealed class DownloadSubtitleItem
{
    public string Code { get; set; } = "none";
    public string DisplayName { get; set; } = "Без субтитров";

    public override string ToString() => DisplayName;
}

