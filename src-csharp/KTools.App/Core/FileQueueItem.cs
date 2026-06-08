// -*- coding: utf-8 -*-
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace KTools_App.Core;

/// <summary>
/// Класс элемента очереди файлов. Содержит метаданные файла
/// и состояние его обработки.
/// </summary>
public sealed class FileQueueItem : INotifyPropertyChanged
{
    private double _progress;
    private string _status = "Ожидание";

    public FileQueueItem(string filePath)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        
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

    public string FilePath { get; }
    public string FileName { get; }
    public string FileSizeStr { get; }

    private MediaStructure? _mediaInfo;
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
                OnPropertyChanged(nameof(ProgressRingVisibility));
                OnPropertyChanged(nameof(StatusIcon));
                OnPropertyChanged(nameof(StatusIconBrush));
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
            if (_status != value)
            {
                _status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProgressRingVisibility));
                OnPropertyChanged(nameof(StatusIcon));
                OnPropertyChanged(nameof(StatusIconBrush));
            }
        }
    }

    public string ProgressText => $"{Progress:F0}%";

    /// <summary>
    /// Видимость кольцевого прогресс-бара.
    /// </summary>
    public Visibility ProgressRingVisibility
    {
        get
        {
            return (Status.StartsWith("Обработка") || Status.Contains("%"))
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Иконка статуса обработки файла.
    /// </summary>
    public Symbol StatusIcon
    {
        get
        {
            if (Status == "Завершено") return Symbol.Accept;
            if (Status == "Ошибка") return Symbol.Cancel;
            if (Status == "Отменено") return Symbol.Cancel;
            
            if (Status.StartsWith("Обработка") || Status.Contains("%"))
            {
                return Symbol.Play;
            }
            
            return Symbol.Clock;
        }
    }

    /// <summary>
    /// Цвет иконки статуса.
    /// </summary>
    public Brush StatusIconBrush
    {
        get
        {
            if (Status == "Завершено")
            {
                return new SolidColorBrush(Windows.UI.Color.FromArgb(
                    255, 34, 180, 115));
            }
            if (Status == "Ошибка")
            {
                return new SolidColorBrush(Windows.UI.Color.FromArgb(
                    255, 232, 17, 35));
            }
            if (Status == "Отменено")
            {
                return (Brush)Application.Current.Resources[
                    "TextFillColorTertiaryBrush"];
            }
            
            if (Status.StartsWith("Обработка") || Status.Contains("%"))
            {
                return (Brush)Application.Current.Resources[
                    "AccentTextFillColorPrimaryBrush"];
            }
            
            return (Brush)Application.Current.Resources[
                "TextFillColorSecondaryBrush"];
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? prop = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}
