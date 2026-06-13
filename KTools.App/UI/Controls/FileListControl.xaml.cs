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
        CheckAdministratorStatus();
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
    /// Регистрация свойства зависимостей IsProcessingProperty для управления состоянием чтения списка файлов.
    /// </summary>
    public static readonly DependencyProperty IsProcessingProperty =
        DependencyProperty.Register(
            nameof(IsProcessing),
            typeof(bool),
            typeof(FileListControl),
            new PropertyMetadata(false, OnIsProcessingChanged));

    /// <summary>
    /// Получает или задает значение, указывающее, выполняется ли в данный момент обработка скрипта.
    /// Влияет на доступность добавления, удаления и очистки списка файлов.
    /// </summary>
    public bool IsProcessing
    {
        get => (bool)GetValue(IsProcessingProperty);
        set => SetValue(IsProcessingProperty, value);
    }

    private static void OnIsProcessingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FileListControl control)
        {
            control.UpdateReadOnlyState((bool)e.NewValue);
        }
    }

    /// <summary>
    /// Обновляет доступность элементов управления списка файлов в зависимости от режима обработки.
    /// </summary>
    private void UpdateReadOnlyState(bool isProcessing)
    {
        AddFilesButton.IsEnabled = !isProcessing;
        ClearListButton.IsEnabled = !isProcessing;
        RootGrid.AllowDrop = !isProcessing;
    }

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
        LogService.Instance.Info(
            "[FileListControl] Открытие диалога выбора файлов с повышенными привилегиями через Microsoft.Windows.Storage.Pickers",
            "FileListControl");

        try
        {
            // Получаем HWND главного окна
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.CurrentMainWindow);
            // Получаем WindowId из HWND
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            
            // Инициализируем picker с WindowId
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(windowId);

            picker.ViewMode = Microsoft.Windows.Storage.Pickers.PickerViewMode.List;
            picker.SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.ComputerFolder;

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
                LogService.Instance.Info(
                    $"[FileListControl] Выбрано файлов вручную: {files.Count}",
                    "FileListControl");
                AddFiles(files.Select(f => f.Path));
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Exception(
                ex,
                "Ошибка при открытии диалогового окна выбора файлов через Microsoft.Windows.Storage.Pickers",
                "FileListControl");
        }
    }

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        if (IsProcessing)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Добавить в K-Tools";
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsContentVisible = true;
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        if (IsProcessing) return;
        try
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
        catch (Exception ex)
        {
            string formatsList = string.Empty;
            try
            {
                formatsList = string.Join(", ", e.DataView.AvailableFormats);
            }
            catch
            {
                formatsList = "не удалось извлечь форматы";
            }

            LogService.Instance.Exception(
                ex,
                $"Возникло исключение при обработке события Drop (перетаскивание файлов). " +
                $"Доступные форматы в DataView: [{formatsList}]",
                "FileListControl");
        }
    }

    /// <summary>
    /// Проверяет, запущено ли приложение с повышенными привилегиями (от имени администратора),
    /// и выводит соответствующие предупреждения в интерфейсе, так как в этом режиме
    /// операционная система Windows блокирует механизм Drag-and-Drop (UIPI).
    /// </summary>
    private void CheckAdministratorStatus()
    {
        try
        {
            using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
            {
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                if (principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
                {
                    AdminWarningBar.IsOpen = true;
                    DragDropPromptTextBlock.Text = "Перетаскивание заблокировано (запущено от администратора)";
                    DragDropSubPromptTextBlock.Text = "Используйте кнопку «Добавить файлы» ниже для выбора файлов вручную.";
                    
                    LogService.Instance.Info(
                        "FileListControl: Обнаружен запуск процесса от имени администратора. " +
                        "Drag-and-Drop заблокирован операционной системой Windows (UIPI). " +
                        "Пользователю выведено предупреждение в интерфейсе.",
                        "FileListControl");
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Exception(
                ex,
                "Исключение при проверке прав администратора для управления отображением Drag-and-Drop",
                "FileListControl");
        }
    }
}
