using System;
using System.Diagnostics;
using KTools_App.Services.Contracts;
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
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Hosting;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

using KTools_App.Core;
using KTools_App.Scripts;
using Microsoft.Extensions.DependencyInjection;

namespace KTools_App.UI.Controls;

/// <summary>
/// Модель данных строки в таблице сборки MKV (Муксинга).
/// Группирует сопутствующие видео, аудио и субтитры с одинаковым базовым именем.
/// Отслеживает внутреннее состояние изменения файлов для корректной блокировки кнопок удаления.
/// </summary>
public sealed class MuxingRowItem : INotifyPropertyChanged
{
    private FileQueueItem? _videoFile;
    private FileQueueItem? _audioFile;
    private FileQueueItem? _subsFile;

    /// <summary>
    /// Базовое имя группы файлов.
    /// </summary>
    public string Stem { get; }

    /// <summary>
    /// Элемент видеофайла.
    /// </summary>
    public FileQueueItem? VideoFile
    {
        get => _videoFile;
        set
        {
            if (_videoFile != value)
            {
                if (_videoFile != null) _videoFile.PropertyChanged -= OnFilePropertyChanged;
                _videoFile = value;
                if (_videoFile != null) _videoFile.PropertyChanged += OnFilePropertyChanged;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsDeleteEnabled));
            }
        }
    }

    /// <summary>
    /// Элемент сопутствующего аудиофайла.
    /// </summary>
    public FileQueueItem? AudioFile
    {
        get => _audioFile;
        set
        {
            if (_audioFile != value)
            {
                if (_audioFile != null) _audioFile.PropertyChanged -= OnFilePropertyChanged;
                _audioFile = value;
                if (_audioFile != null) _audioFile.PropertyChanged += OnFilePropertyChanged;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsDeleteEnabled));
            }
        }
    }

    /// <summary>
    /// Элемент сопутствующих субтитров.
    /// </summary>
    public FileQueueItem? SubsFile
    {
        get => _subsFile;
        set
        {
            if (_subsFile != value)
            {
                if (_subsFile != null) _subsFile.PropertyChanged -= OnFilePropertyChanged;
                _subsFile = value;
                if (_subsFile != null) _subsFile.PropertyChanged += OnFilePropertyChanged;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsDeleteEnabled));
            }
        }
    }

    private string _audioWarning = string.Empty;
    public string AudioWarning
    {
        get => _audioWarning;
        set
        {
            if (_audioWarning != value)
            {
                _audioWarning = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AudioDisplayText));
            }
        }
    }

    private string _subsWarning = string.Empty;
    public string SubsWarning
    {
        get => _subsWarning;
        set
        {
            if (_subsWarning != value)
            {
                _subsWarning = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SubsDisplayText));
            }
        }
    }

    public string AudioDisplayText
    {
        get
        {
            if (AudioFile == null) return "—";
            return $"{AudioFile.FileName}{AudioWarning}";
        }
    }

    public string SubsDisplayText
    {
        get
        {
            if (SubsFile == null) return "—";
            return $"{SubsFile.FileName}{SubsWarning}";
        }
    }

    /// <summary>
    /// Разрешено ли удаление строки из таблицы (разрешено, если все входящие в нее файлы разблокированы для удаления).
    /// </summary>
    public bool IsDeleteEnabled
    {
        get
        {
            if (VideoFile != null && !VideoFile.IsDeleteEnabled) return false;
            if (AudioFile != null && !AudioFile.IsDeleteEnabled) return false;
            if (SubsFile != null && !SubsFile.IsDeleteEnabled) return false;
            return true;
        }
    }

    /// <summary>
    /// Инициализирует новую строку муксинга для заданного имени.
    /// </summary>
    public MuxingRowItem(string stem)
    {
        Stem = stem;
    }

    private void OnFilePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileQueueItem.IsDeleteEnabled) || 
            e.PropertyName == nameof(FileQueueItem.IsProcessing))
        {
            OnPropertyChanged(nameof(IsDeleteEnabled));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? prop = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}

/// <summary>
/// Пользовательский элемент управления списком файлов с поддержкой Drag-and-Drop и табличного представления сборки MKV.
/// </summary>
public sealed partial class FileListControl : UserControl
{
    private readonly ILogService _logService;
    private readonly ISettingsManager _settingsManager;
    private readonly IMediaProbeService _mediaProbeService;
    private readonly IPathManager _pathManager;

    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue = 
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
    private ObservableCollection<FileQueueItem> _files = new();
    private readonly ObservableCollection<MuxingRowItem> _muxingRows = new();
    private AbstractScript? _activeScript;

    /// <summary>
    /// Инициализирует FileListControl.
    /// </summary>
    public FileListControl()
    {
        _logService = App.Services.GetRequiredService<ILogService>();
        _settingsManager = App.Services.GetRequiredService<ISettingsManager>();
        _mediaProbeService = App.Services.GetRequiredService<IMediaProbeService>();
        _pathManager = App.Services.GetRequiredService<IPathManager>();

        InitializeComponent();
        _files.CollectionChanged += OnFilesCollectionChanged;
        FilesListView.ItemsSource = _files;
        MuxingListView.ItemsSource = _muxingRows;
        DownloaderListView.ItemsSource = _files;
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
                DownloaderListView.ItemsSource = _files;
                SyncMuxingRows();
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
        SyncMuxingRows();
        UpdateEmptyState();
    }

    /// <summary>
    /// Ссылка на текущий активный скрипт для фильтрации входящих расширений файлов.
    /// </summary>
    public AbstractScript? ActiveScript
    {
        get => _activeScript;
        set
        {
            if (_activeScript != value)
            {
                _activeScript = value;
                SyncMuxingRows();
                UpdateEmptyState();

                if (_activeScript is MediaDownloaderScript)
                {
                    AddFilesButton.Visibility = Visibility.Collapsed;
                }
                else
                {
                    AddFilesButton.Visibility = Visibility.Visible;
                }
            }
        }
    }

    /// <summary>
    /// Оповещает все элементы в очереди об изменении настройки субтитров.
    /// </summary>
    public void NotifySubtitlesSettingChanged()
    {
        foreach (var item in Files)
        {
            item.NotifySubtitlesSettingChanged();
        }
    }

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
        SyncMuxingRows();
        UpdateEmptyState();
    }

    /// <summary>
    /// Добавить файлы в очередь с валидацией допустимых расширений скрипта.
    /// </summary>
    public void AddFiles(IEnumerable<string> filePaths)
    {
        if (_settingsManager.ClearListOnAdd)
        {
            Clear();
        }

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
            SyncMuxingRows();
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
                var structure = await _mediaProbeService.ProbeAsync(item.FilePath);
                if (structure != null)
                {
                    _dispatcherQueue.TryEnqueue(() =>
                    {
                        item.MediaInfo = structure;
                    });
                    _logService.Info(
                        $"Фоновый анализ завершен для '{item.FileName}'. " +
                        $"Дорожек: {structure.Tracks.Count}, " +
                        $"вложений: {structure.Attachments.Count}",
                        "FileListControl");
                }
            }
            catch (Exception ex)
            {
                _logService.Exception(
                    ex,
                    $"Ошибка при попытке фонового анализа структуры файла '{item.FileName}'",
                    "FileListControl");
            }
        });
    }

    /// <summary>
    /// Синхронизирует плоский список файлов с табличной моделью муксинга (сборки MKV).
    /// </summary>
    private void SyncMuxingRows()
    {
        _muxingRows.Clear();
        if (ActiveScript is not MkvAssemblyScript)
        {
            return;
        }

        var groups = new Dictionary<string, MuxingRowItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in _files)
        {
            string stem = Path.GetFileNameWithoutExtension(file.FilePath);
            string ext = Path.GetExtension(file.FilePath).ToLowerInvariant();

            if (!groups.TryGetValue(stem, out var row))
            {
                row = new MuxingRowItem(stem);
                groups[stem] = row;
            }

            if (AppConstants.VideoContainers.Contains(ext))
            {
                row.VideoFile = file;
            }
            else if (AppConstants.AudioContainers.Contains(ext) || AppConstants.AudioStreams.Contains(ext))
            {
                row.AudioFile = file;
            }
            else if (AppConstants.SubtitleExtensions.Contains(ext))
            {
                row.SubsFile = file;
            }
        }

        string groupName = _settingsManager.GetSafeGroupName(ActiveScript.Name);
        string containerChoice = _settingsManager.GetSetting(groupName, "output_container", "MKV");
        bool isMp4 = containerChoice.Equals("MP4", StringComparison.OrdinalIgnoreCase);

        foreach (var row in groups.Values)
        {
            if (isMp4)
            {
                if (row.AudioFile != null)
                {
                    string aExt = Path.GetExtension(row.AudioFile.FilePath).ToLowerInvariant();
                    if (aExt == ".flac" || aExt == ".thd" || aExt == ".truehd" || aExt == ".dts" || aExt == ".dtshd")
                    {
                        row.AudioWarning = " ⚠️ [Не поддерживается в MP4]";
                    }
                }
                if (row.SubsFile != null)
                {
                    string sExt = Path.GetExtension(row.SubsFile.FilePath).ToLowerInvariant();
                    if (sExt == ".ass" || sExt == ".ssa")
                    {
                        row.SubsWarning = " ⚠️ [Не поддерживается в MP4]";
                    }
                }
            }

            _muxingRows.Add(row);
        }
    }

    private void UpdateEmptyState()
    {
        if (Files.Count == 0)
        {
            EmptyPanel.Visibility = Visibility.Visible;
            FilesListView.Visibility = Visibility.Collapsed;
            MuxingGrid.Visibility = Visibility.Collapsed;
            DownloaderListView.Visibility = Visibility.Collapsed;
        }
        else
        {
            EmptyPanel.Visibility = Visibility.Collapsed;
            if (ActiveScript is MkvAssemblyScript)
            {
                FilesListView.Visibility = Visibility.Collapsed;
                MuxingGrid.Visibility = Visibility.Visible;
                DownloaderListView.Visibility = Visibility.Collapsed;
            }
            else if (ActiveScript is MediaDownloaderScript)
            {
                FilesListView.Visibility = Visibility.Collapsed;
                MuxingGrid.Visibility = Visibility.Collapsed;
                DownloaderListView.Visibility = Visibility.Visible;
            }
            else
            {
                FilesListView.Visibility = Visibility.Visible;
                MuxingGrid.Visibility = Visibility.Collapsed;
                DownloaderListView.Visibility = Visibility.Collapsed;
            }
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

    private void OpenBitrateGraphButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is FileQueueItem item)
        {
            item.OpenBitrateGraph();
        }
    }

    /// <summary>
    /// Обработчик кнопки удаления строки из таблицы муксинга.
    /// Удаляет видео, аудио и субтитры текущей строки из основной очереди.
    /// </summary>
    private void DeleteMuxingRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is MuxingRowItem row)
        {
            if (row.VideoFile != null) Files.Remove(row.VideoFile);
            if (row.AudioFile != null) Files.Remove(row.AudioFile);
            if (row.SubsFile != null) Files.Remove(row.SubsFile);
            SyncMuxingRows();
            UpdateEmptyState();
        }
    }

    private void ClearListButton_Click(object sender, RoutedEventArgs e)
    {
        Clear();
    }

    private async void AddFilesButton_Click(object sender, RoutedEventArgs e)
    {
        _logService.Info(
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
                _logService.Info(
                    $"[FileListControl] Выбрано файлов вручную: {files.Count}",
                    "FileListControl");
                AddFiles(files.Select(f => f.Path));
            }
        }
        catch (Exception ex)
        {
            _logService.Exception(
                ex,
                "Ошибка при открытии диалогового окна выбора файлов через Microsoft.Windows.Storage.Pickers",
                "FileListControl");
        }
    }

    private void SetFileDropHighlight(bool isHighlighted)
    {
        DropOverlay?.SetHighlighted(isHighlighted);

        if (EmptyPanel != null)
        {
            if (Application.Current.Resources.TryGetValue(isHighlighted ? "CardBackgroundFillColorSecondaryBrush" : "CardBackgroundFillColorDefaultBrush", out var bgBrush) && bgBrush is Brush bg)
            {
                EmptyPanel.Background = bg;
            }
        }
    }

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        if (IsProcessing)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            e.Handled = true;
            return;
        }

        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            SetFileDropHighlight(true);
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }

        e.Handled = true;
    }

    private void RootGrid_DragLeave(object sender, DragEventArgs e)
    {
        SetFileDropHighlight(false);
        e.Handled = true;
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        SetFileDropHighlight(false);
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

            _logService.Exception(
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
                    
                    _logService.Info(
                        "FileListControl: Обнаружен запуск процесса от имени администратора. " +
                        "Drag-and-Drop заблокирован операционной системой Windows (UIPI). " +
                        "Пользователю выведено предупреждение в интерфейсе.",
                        "FileListControl");
                }
            }
        }
        catch (Exception ex)
        {
            _logService.Exception(
                ex,
                "Исключение при проверке прав администратора для управления отображением Drag-and-Drop",
                "FileListControl");
        }
    }

    /// <summary>
    /// Добавляет ссылку для скачивания и запускает фоновое получение её метаданных (качества и названия).
    /// </summary>
    public void AddUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        var item = new FileQueueItem(url);
        
        // Добавляем дефолтные варианты качеств
        item.AvailableFormats.Add(new DownloadFormatItem { Id = "best_quality", DisplayName = "Наилучшее качество (Видео+Аудио)", FormatArg = "bv*+ba/b" });
        item.AvailableFormats.Add(new DownloadFormatItem { Id = "best_video", DisplayName = "Только наилучшее видео", FormatArg = "bv*" });
        item.AvailableFormats.Add(new DownloadFormatItem { Id = "best_audio", DisplayName = "Только наилучший звук", FormatArg = "ba" });
        item.SelectedFormat = item.AvailableFormats[0];

        item.AvailableSubtitles.Add(new DownloadSubtitleItem { Code = "none", DisplayName = "Без субтитров" });
        item.SelectedSubtitle = item.AvailableSubtitles[0];

        Files.Add(item);
        
        // Запуск фонового получения информации
        _ = Task.Run(() => FetchUrlInfoAsync(item));
    }

    private async Task FetchUrlInfoAsync(FileQueueItem item)
    {
        try
        {
            string ytdlpPath = _pathManager.GetBinaryPath("yt-dlp");
            if (!File.Exists(ytdlpPath)) return;

            string nodePath = _pathManager.GetBinaryPath("node");
            string jsRuntimeArg = "";
            if (File.Exists(nodePath))
            {
                jsRuntimeArg = $"--js-runtimes \"node:{nodePath}\" ";
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = ytdlpPath,
                Arguments = $"{jsRuntimeArg}--dump-json \"{item.FilePath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = false
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            ActiveProcessTracker.Register(process);

            string stdout;
            try
            {
                // Читаем stdout асинхронно
                stdout = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();
            }
            finally
            {
                ActiveProcessTracker.Unregister(process);
            }

            if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(stdout);
                var root = doc.RootElement;

                // 1. Get Title
                if (root.TryGetProperty("title", out var titleProp))
                {
                    string title = titleProp.GetString() ?? "";
                    if (!string.IsNullOrEmpty(title))
                    {
                        _dispatcherQueue.TryEnqueue(() =>
                        {
                            item.DisplayName = title;
                        });
                    }
                }

                // 2. Parse Formats
                var tempFormats = new List<DownloadFormatItem>();
                tempFormats.Add(new DownloadFormatItem { Id = "best_quality", DisplayName = "Наилучшее качество (Видео+Аудио)", FormatArg = "bv*+ba/b" });
                tempFormats.Add(new DownloadFormatItem { Id = "best_video", DisplayName = "Только наилучшее видео", FormatArg = "bv*" });
                tempFormats.Add(new DownloadFormatItem { Id = "best_audio", DisplayName = "Только наилучший звук", FormatArg = "ba" });

                if (root.TryGetProperty("formats", out var formatsProp) && formatsProp.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var format in formatsProp.EnumerateArray())
                    {
                        string formatId = format.TryGetProperty("format_id", out var fid) ? fid.GetString() ?? "" : "";
                        if (string.IsNullOrEmpty(formatId)) continue;

                        string ext = format.TryGetProperty("ext", out var extP) ? extP.GetString() ?? "" : "";
                        
                        bool hasVideo = format.TryGetProperty("vcodec", out var vcodecProp) && vcodecProp.GetString() != "none";
                        bool hasAudio = format.TryGetProperty("acodec", out var acodecProp) && acodecProp.GetString() != "none";
                        
                        double tbr = 0;
                        if (format.TryGetProperty("tbr", out var tbrP) && tbrP.ValueKind == System.Text.Json.JsonValueKind.Number)
                        {
                            tbr = tbrP.GetDouble();
                        }
                        
                        int fps = 0;
                        if (format.TryGetProperty("fps", out var fpsP) && fpsP.ValueKind == System.Text.Json.JsonValueKind.Number)
                        {
                            fps = (int)Math.Round(fpsP.GetDouble());
                        }
                        
                        int height = 0;
                        if (format.TryGetProperty("height", out var hP) && hP.ValueKind == System.Text.Json.JsonValueKind.Number)
                        {
                            height = (int)Math.Round(hP.GetDouble());
                        }
                        
                        int width = 0;
                        if (format.TryGetProperty("width", out var wP) && wP.ValueKind == System.Text.Json.JsonValueKind.Number)
                        {
                            width = (int)Math.Round(wP.GetDouble());
                        }

                        string vcodec = format.TryGetProperty("vcodec", out var vc) ? vc.GetString() ?? "" : "";
                        string acodec = format.TryGetProperty("acodec", out var ac) ? ac.GetString() ?? "" : "";

                        if (vcodec.Contains(".")) vcodec = vcodec.Split('.')[0];
                        if (acodec.Contains(".")) acodec = acodec.Split('.')[0];

                        string disp = "";
                        string formatArg = formatId;

                        if (hasVideo && hasAudio)
                        {
                            disp = $"[Видео+Аудио] {width}x{height} ({ext})";
                            if (fps > 0) disp += $" - {fps}fps";
                            if (tbr > 0) disp += $", ~{tbr:F0}k";
                            disp += $" ({vcodec}/{acodec})";
                        }
                        else if (hasVideo)
                        {
                            disp = $"[Только видео] {width}x{height} ({ext})";
                            if (fps > 0) disp += $" - {fps}fps";
                            if (tbr > 0) disp += $", ~{tbr:F0}k";
                            disp += $" ({vcodec})";

                            // Добавляем также вариант склеивания этого видеопотока с лучшим звуком
                            tempFormats.Add(new DownloadFormatItem
                            {
                                Id = formatId + "_merged",
                                DisplayName = $"[Видео+Звук] {width}x{height} ({ext}) + Лучший звук",
                                FormatArg = $"{formatId}+ba/b"
                            });
                        }
                        else if (hasAudio)
                        {
                            disp = $"[Только аудио] {ext}";
                            if (tbr > 0) disp += $" - ~{tbr:F0}k";
                            disp += $" ({acodec})";
                        }
                        else
                        {
                            continue;
                        }

                        tempFormats.Add(new DownloadFormatItem { Id = formatId, DisplayName = disp, FormatArg = formatArg });
                    }
                }

                // 3. Parse Subtitles
                var tempSubtitles = new List<DownloadSubtitleItem>();
                tempSubtitles.Add(new DownloadSubtitleItem { Code = "none", DisplayName = "Без субтитров" });
                tempSubtitles.Add(new DownloadSubtitleItem { Code = "all", DisplayName = "Все субтитры" });

                var addedCodes = new HashSet<string>();

                if (root.TryGetProperty("subtitles", out var subsProp) && subsProp.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    foreach (var prop in subsProp.EnumerateObject())
                    {
                        string code = prop.Name;
                        addedCodes.Add(code);
                        string name = TranslateLanguageCode(code);
                        tempSubtitles.Add(new DownloadSubtitleItem { Code = code, DisplayName = $"{name} ({code})" });
                    }
                }

                if (root.TryGetProperty("automatic_captions", out var autoSubsProp) && autoSubsProp.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    foreach (var prop in autoSubsProp.EnumerateObject())
                    {
                        string code = prop.Name;
                        if (!addedCodes.Contains(code))
                        {
                            addedCodes.Add(code);
                            string name = TranslateLanguageCode(code);
                            tempSubtitles.Add(new DownloadSubtitleItem { Code = code, DisplayName = $"{name} ({code}) [авто]" });
                        }
                    }
                }

                _dispatcherQueue.TryEnqueue(() =>
                {
                    item.AvailableFormats.Clear();
                    foreach (var f in tempFormats)
                    {
                        item.AvailableFormats.Add(f);
                    }
                    item.SelectedFormat = item.AvailableFormats.FirstOrDefault(f => f.Id == "best_quality") ?? item.AvailableFormats.FirstOrDefault();

                    item.AvailableSubtitles.Clear();
                    foreach (var s in tempSubtitles)
                    {
                        item.AvailableSubtitles.Add(s);
                    }
                    item.SelectedSubtitle = item.AvailableSubtitles.FirstOrDefault(s => s.Code == "none") ?? item.AvailableSubtitles.FirstOrDefault();
                });
            }
        }
        catch (Exception ex)
        {
            _logService.Exception(ex, $"Ошибка фонового запроса информации для {item.FilePath}", "FileListControl");
        }
    }

    private static string TranslateLanguageCode(string code)
    {
        string baseCode = code.Split('-')[0].ToLowerInvariant();
        return baseCode switch
        {
            "ru" => "Русский",
            "en" => "Английский",
            "ja" => "Японский",
            "de" => "Немецкий",
            "fr" => "Французский",
            "es" => "Испанский",
            "zh" => "Китайский",
            "ko" => "Корейский",
            "it" => "Итальянский",
            _ => code.ToUpperInvariant()
        };
    }
}
