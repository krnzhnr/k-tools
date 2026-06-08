using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
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
/// Пользовательский элемент управления списком файлов с полной поддержкой Drag-and-Drop.
/// </summary>
public sealed partial class FileListControl : UserControl
{
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue = 
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
    private ObservableCollection<FileQueueItem> _files = new();

    public FileListControl()
    {
        InitializeComponent();
        _files.CollectionChanged += OnFilesCollectionChanged;
        FilesListView.ItemsSource = _files;
        UpdateEmptyState();
    }

    /// <summary>
    /// Список файлов в очереди.
    /// </summary>
    public ObservableCollection<FileQueueItem> Files
    {
        get => _files;
        private set
        {
            if (_files != value)
            {
                _files.CollectionChanged -= OnFilesCollectionChanged;
                _files = value;
                _files.CollectionChanged += OnFilesCollectionChanged;
                FilesListView.ItemsSource = _files;
                UpdateEmptyState();
            }
        }
    }

    /// <summary>
    /// Устанавливает коллекцию файлов из бизнес-логики скрипта.
    /// </summary>
    public void SetFiles(ObservableCollection<FileQueueItem> files)
    {
        Files = files;
    }

    private void OnFilesCollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        UpdateEmptyState();
    }

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
                    _dispatcherQueue.TryEnqueue(() =>
                    {
                        item.MediaInfo = structure;
                    });
                    LogService.Instance.Info(
                        $"Фоновый анализ завершен для '{item.FileName}'. " +
                        $"Дорожек: {structure.Tracks.Count}, " +
                        $"вложений: {structure.Attachments.Count}",
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
