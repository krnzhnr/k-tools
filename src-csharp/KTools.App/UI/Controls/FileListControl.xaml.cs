using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

using KTools_App.Core;

namespace KTools_App.UI.Controls;

/// <summary>
/// Класс элемента очереди файлов. Содержит метаданные файла и состояние его обработки.
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
    /// Текстовый статус файла (например, "Ожидание", "Обработка...", "Завершено").
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

/// <summary>
/// Пользовательский элемент управления списком файлов с полной поддержкой Drag-and-Drop.
/// </summary>
public sealed partial class FileListControl : UserControl
{
    public FileListControl()
    {
        InitializeComponent();
        Files = new ObservableCollection<FileQueueItem>();
        
        // Подписываемся на событие изменения состава коллекции
        // файлов. Это критически важно для корректного обновления
        // видимости элементов интерфейса (списка и заглушки),
        // когда коллекция наполняется или очищается из ViewModel
        // при восстановлении состояния после переходов между страницами.
        Files.CollectionChanged += (sender, args) => UpdateEmptyState();
        
        FilesListView.ItemsSource = Files;
        UpdateEmptyState();
    }

    /// <summary>
    /// Список файлов в очереди.
    /// </summary>
    public ObservableCollection<FileQueueItem> Files { get; }

    /// <summary>
    /// Ссылка на текущий активный скрипт для фильтрации входящих расширений файлов.
    /// </summary>
    public Core.AbstractScript? ActiveScript { get; set; }

    /// <summary>
    /// Очистить список файлов.
    /// </summary>
    public void Clear()
    {
        Files.Clear();
        UpdateEmptyState();
    }

    /// <summary>
    /// Восстановить очередь файлов с их сохраненными
    /// статусами и прогрессом обработки.
    /// </summary>
    public void AddSavedFiles(IEnumerable<Core.SavedFileState> savedFiles)
    {
        bool addedAny = false;
        foreach (var state in savedFiles)
        {
            if (!File.Exists(state.FilePath)) continue;

            // Валидация расширения
            if (ActiveScript != null &&
                ActiveScript.FileExtensions.Length > 0)
            {
                string ext = Path.GetExtension(state.FilePath)
                    .ToLowerInvariant();
                if (!ActiveScript.FileExtensions.Contains(ext))
                {
                    continue; // Пропускаем неподдерживаемые
                }
            }

            // Исключаем дубликаты
            if (Files.Any(f => f.FilePath.Equals(
                state.FilePath, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var item = new FileQueueItem(state.FilePath)
            {
                Status = state.Status,
                Progress = state.Progress
            };
            Files.Add(item);
            addedAny = true;

            // Запускаем фоновый асинхронный анализ структуры
            StartFileAnalysis(item);
        }

        if (addedAny)
        {
            UpdateEmptyState();
        }
    }

    /// <summary>
    /// Добавить файлы в очередь с валидацией допустимых расширений скрипта.
    /// </summary>
    public void AddFiles(IEnumerable<string> filePaths)
    {
        bool addedAny = false;
        foreach (string path in filePaths)
        {
            if (!File.Exists(path)) continue;

            // Валидация расширения
            if (ActiveScript != null && ActiveScript.FileExtensions.Length > 0)
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (!ActiveScript.FileExtensions.Contains(ext))
                {
                    continue; // Пропускаем неподдерживаемые расширения
                }
            }

            // Исключаем дубликаты
            if (Files.Any(f => f.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var item = new FileQueueItem(path);
            Files.Add(item);
            addedAny = true;

            // Запускаем фоновый асинхронный анализ структуры
            StartFileAnalysis(item);
        }

        if (addedAny)
        {
            UpdateEmptyState();
        }
    }

    /// <summary>
    /// Запустить фоновый асинхронный анализ технической структуры медиафайла.
    /// Все логи и обработка ошибок на русском языке.
    /// </summary>
    private void StartFileAnalysis(FileQueueItem item)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var structure = await MediaProbeService.Instance.ProbeAsync(item.FilePath);
                if (structure != null)
                {
                    item.MediaInfo = structure;
                    LogService.Instance.Info(
                        $"Фоновый анализ завершен для '{item.FileName}'. " +
                        $"Дорожек: {structure.Tracks.Count}, вложений: {structure.Attachments.Count}",
                        "FileListControl");
                }
            }
            catch (Exception ex)
            {
                LogService.Instance.Exception(
                    ex,
                    $"Ошибка при попытке фонового анализа структуры файла '{item.FileName}'",
                    "FileListControl");
            }
        });
    }

    private void UpdateEmptyState()
    {
        if (Files.Count == 0)
        {
            EmptyPanel.Visibility = Visibility.Visible;
            FilesListView.Visibility = Visibility.Collapsed;
        }
        else
        {
            EmptyPanel.Visibility = Visibility.Collapsed;
            FilesListView.Visibility = Visibility.Visible;
        }
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is FileQueueItem item)
        {
            Files.Remove(item);
            UpdateEmptyState();
        }
    }

    private void ClearListButton_Click(object sender, RoutedEventArgs e)
    {
        Clear();
    }

    private async void AddFilesButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        
        // Настройка сопоставления окна с главным окном в WinUI 3
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.CurrentMainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        picker.ViewMode = PickerViewMode.List;
        picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;

        // Фильтры расширений
        if (ActiveScript != null && ActiveScript.FileExtensions.Length > 0)
        {
            foreach (var ext in ActiveScript.FileExtensions)
            {
                picker.FileTypeFilter.Add(ext);
            }
        }
        else
        {
            picker.FileTypeFilter.Add("*");
        }

        var files = await picker.PickMultipleFilesAsync();
        if (files != null && files.Count > 0)
        {
            AddFiles(files.Select(f => f.Path));
        }
    }

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Добавить в K-Tools";
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsContentVisible = true;
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = new List<string>();

            foreach (var item in items)
            {
                if (item is StorageFile file)
                {
                    paths.Add(file.Path);
                }
            }

            AddFiles(paths);
        }
    }
}
