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
/// Обёртка сопутствующего файла в строке сборки MKV с категорией дорожки
/// (аудио, полные субтитры, надписи, прочие субтитры) и признаком ручной привязки.
/// </summary>
public sealed class MuxTrackItem : INotifyPropertyChanged
{
    /// <summary>
    /// Файл дорожки из общей очереди.
    /// </summary>
    public FileQueueItem File { get; }

    /// <summary>
    /// Подпись категории для отображения ("Аудио", "Полные", "Надписи", "Субтитры").
    /// </summary>
    public string KindLabel { get; }

    /// <summary>
    /// Показывать ли селектор роли (только для субтитров; у аудио роли нет).
    /// </summary>
    public bool ShowRoleSelector { get; }

    private bool _isMainTrack;

    /// <summary>
    /// Признак основной аудиодорожки группы (зона "RU Аудио (Осн.)" или первая по имени).
    /// </summary>
    public bool IsMainTrack
    {
        get => _isMainTrack;
        set
        {
            if (_isMainTrack != value)
            {
                _isMainTrack = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Признак ручной привязки файла к группе (показывает кнопку открепления).
    /// </summary>
    public bool IsPinned => File.IsPinned;

    /// <summary>
    /// Признак аудиодорожки (у субтитров вместо этого селектор роли).
    /// </summary>
    public bool IsAudioTrack => !ShowRoleSelector;

    /// <summary>
    /// Встроенный заголовок дорожки из метаданных файла (поле Title/Name зонда MediaInfo),
    /// например "AniLibria". Пусто, если метаданных нет или заголовок не задан.
    /// </summary>
    public string TitleHint
    {
        get
        {
            var tracks = File.MediaInfo?.Tracks;
            if (tracks == null || tracks.Count == 0)
            {
                return string.Empty;
            }

            foreach (var track in tracks)
            {
                bool isAudioType = track.TrackType.Equals("audio", StringComparison.OrdinalIgnoreCase);
                if (isAudioType == IsAudioTrack && !string.IsNullOrWhiteSpace(track.Name))
                {
                    return $"— {track.Name.Trim()}";
                }
            }

            return string.Empty;
        }
    }

    /// <summary>
    /// Признак наличия встроенного заголовка (для видимости подписи в UI).
    /// </summary>
    public bool HasTitleHint => !string.IsNullOrEmpty(TitleHint);

    /// <summary>
    /// Индекс выбранной роли в селекторе (0 — Авто, 1 — Полные, 2 — Надписи, 3 — Другие).
    /// </summary>
    public int RoleOptionIndex => File.MuxRoleOverride switch
    {
        MuxSubsRole.Full => 1,
        MuxSubsRole.Signs => 2,
        MuxSubsRole.Other => 3,
        _ => 0
    };

    public MuxTrackItem(FileQueueItem file, string kindLabel, bool showRoleSelector = false, bool isMainTrack = false)
    {
        File = file;
        KindLabel = kindLabel;
        ShowRoleSelector = showRoleSelector;
        _isMainTrack = isMainTrack;
        File.PropertyChanged += OnFilePropertyChanged;
    }

    /// <summary>
    /// Отписывается от уведомлений файла при удалении обёртки из коллекции.
    /// </summary>
    public void Detach()
    {
        File.PropertyChanged -= OnFilePropertyChanged;
    }

    private void OnFilePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileQueueItem.IsPinned))
        {
            OnPropertyChanged(nameof(IsPinned));
        }
        else if (e.PropertyName == nameof(FileQueueItem.MuxRoleOverride))
        {
            OnPropertyChanged(nameof(RoleOptionIndex));
        }
        else if (e.PropertyName == nameof(FileQueueItem.MediaInfo))
        {
            OnPropertyChanged(nameof(TitleHint));
            OnPropertyChanged(nameof(HasTitleHint));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? prop = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}

/// <summary>
/// Модель данных строки в таблице сборки MKV (Муксинга).
/// Группирует видео и все сопутствующие ему дорожки по категориям:
/// аудио, полные субтитры, надписи, прочие субтитры.
/// Сопоставление выполняется по имени файла через <see cref="MuxGroupMatcher"/>:
/// точное совпадение либо префикс через разделитель ("video.dub", "video_en", "video.signs").
/// Ручная привязка через дроп-зону (<see cref="FileQueueItem.MuxPinnedStem"/>) имеет приоритет.
/// Отслеживает внутреннее состояние изменения файлов для корректной блокировки кнопок удаления.
/// </summary>
public sealed class MuxingRowItem : INotifyPropertyChanged
{
    private FileQueueItem? _videoFile;

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
    /// Аудиодорожки группы (несколько озвучек). Отсортированы по имени файла.
    /// </summary>
    public ObservableCollection<MuxTrackItem> AudioTracks { get; } = new();

    /// <summary>
    /// Полные субтитры группы. Отсортированы по имени файла.
    /// </summary>
    public ObservableCollection<MuxTrackItem> FullSubsTracks { get; } = new();

    /// <summary>
    /// Надписи группы (.signs). Отсортированы по имени файла.
    /// </summary>
    public ObservableCollection<MuxTrackItem> SignsSubsTracks { get; } = new();

    /// <summary>
    /// Прочие субтитры группы (языковые суффиксы, forced и т.п.). Отсортированы по имени файла.
    /// </summary>
    public ObservableCollection<MuxTrackItem> OtherSubsTracks { get; } = new();

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

    private IEnumerable<MuxTrackItem> AllSubsTracks()
    {
        foreach (var track in SignsSubsTracks) yield return track;
        foreach (var track in FullSubsTracks) yield return track;
        foreach (var track in OtherSubsTracks) yield return track;
    }

    public string AudioDisplayText
    {
        get
        {
            if (AudioTracks.Count == 0) return "—";
            string joined = string.Join("; ", AudioTracks.Select(t => t.File.FileName));
            string prefix = AudioTracks.Count > 1 ? $"{AudioTracks.Count} файла: " : string.Empty;
            return $"{prefix}{joined}{AudioWarning}";
        }
    }

    public string SubsDisplayText
    {
        get
        {
            var names = AllSubsTracks().Select(t => t.File.FileName).ToList();
            if (names.Count == 0) return "—";
            string prefix = names.Count > 1 ? $"{names.Count} файла: " : string.Empty;
            return $"{prefix}{string.Join("; ", names)}{SubsWarning}";
        }
    }

    /// <summary>
    /// Количество аудиодорожек в группе (для бейджей в UI).
    /// </summary>
    public int AudioCount => AudioTracks.Count;

    /// <summary>
    /// Количество дорожек субтитров в группе (для бейджей в UI).
    /// </summary>
    public int SubsCount => FullSubsTracks.Count + SignsSubsTracks.Count + OtherSubsTracks.Count;

    /// <summary>
    /// Разрешено ли удаление строки из таблицы (разрешено, если все входящие в нее файлы разблокированы для удаления).
    /// </summary>
    public bool IsDeleteEnabled
    {
        get
        {
            if (VideoFile != null && !VideoFile.IsDeleteEnabled) return false;
            if (AudioTracks.Any(t => !t.File.IsDeleteEnabled)) return false;
            if (AllSubsTracks().Any(t => !t.File.IsDeleteEnabled)) return false;
            return true;
        }
    }

    /// <summary>
    /// Инициализирует новую строку муксинга для заданного имени.
    /// </summary>
    public MuxingRowItem(string stem)
    {
        Stem = stem;
        AudioTracks.CollectionChanged += OnTrackCollectionChanged;
        FullSubsTracks.CollectionChanged += OnTrackCollectionChanged;
        SignsSubsTracks.CollectionChanged += OnTrackCollectionChanged;
        OtherSubsTracks.CollectionChanged += OnTrackCollectionChanged;
    }

    private void OnTrackCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (MuxTrackItem item in e.OldItems)
            {
                item.File.PropertyChanged -= OnFilePropertyChanged;
                item.Detach();
            }
        }
        if (e.NewItems != null)
        {
            foreach (MuxTrackItem item in e.NewItems)
            {
                item.File.PropertyChanged += OnFilePropertyChanged;
            }
        }
        OnPropertyChanged(nameof(AudioDisplayText));
        OnPropertyChanged(nameof(SubsDisplayText));
        OnPropertyChanged(nameof(AudioCount));
        OnPropertyChanged(nameof(SubsCount));
        OnPropertyChanged(nameof(IsDeleteEnabled));
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
    /// Имя файла — главный источник правды: сопутствующий файл привязывается к видео
    /// при точном совпадении имени либо префиксе через разделитель (см. MuxGroupMatcher).
    /// Ручная привязка через дроп-зону (MuxPinnedStem) имеет приоритет над автосопоставлением.
    /// Субтитры раскладываются по категориям через MuxTrackTyper (.full / .signs).
    /// К одному видео может быть привязано несколько аудио и субтитров.
    /// </summary>
    private void SyncMuxingRows()
    {
        _muxingRows.Clear();
        if (ActiveScript is not MkvAssemblyScript)
        {
            return;
        }

        var groups = new Dictionary<string, MuxingRowItem>(StringComparer.OrdinalIgnoreCase);
        var videoStems = new List<string>();
        var companions = new List<(FileQueueItem File, string Stem, string Ext)>();

        foreach (var file in _files)
        {
            string stem = Path.GetFileNameWithoutExtension(file.FilePath);
            string ext = Path.GetExtension(file.FilePath).ToLowerInvariant();

            if (AppConstants.VideoContainers.Contains(ext))
            {
                if (!groups.TryGetValue(stem, out var videoRow))
                {
                    videoRow = new MuxingRowItem(stem);
                    groups[stem] = videoRow;
                    videoStems.Add(stem);
                }

                if (videoRow.VideoFile == null)
                {
                    videoRow.VideoFile = file;
                }
                else
                {
                    // Дубликат видео с тем же именем (другое расширение) — отдельной строкой, чтобы не потерять файл.
                    string dupKey = $"{stem} ({ext})";
                    if (!groups.TryGetValue(dupKey, out var dupRow))
                    {
                        dupRow = new MuxingRowItem(dupKey);
                        groups[dupKey] = dupRow;
                    }
                    if (dupRow.VideoFile == null)
                    {
                        dupRow.VideoFile = file;
                    }
                }
            }
            else if (AppConstants.AudioContainers.Contains(ext)
                || AppConstants.AudioStreams.Contains(ext)
                || AppConstants.SubtitleExtensions.Contains(ext))
            {
                companions.Add((file, stem, ext));
            }
        }

        foreach (var (file, stem, ext) in companions)
        {
            MuxingRowItem row;
            if (!string.IsNullOrEmpty(file.MuxPinnedStem)
                && groups.TryGetValue(file.MuxPinnedStem, out var pinnedRow)
                && pinnedRow.VideoFile != null)
            {
                row = pinnedRow;
            }
            else
            {
                string? bestStem = MuxGroupMatcher.FindBestVideoStem(videoStems, stem);
                if (bestStem != null && groups.TryGetValue(bestStem, out var videoRow))
                {
                    row = videoRow;
                }
                else
                {
                    // Сирота без подходящего видео — показываем отдельной строкой.
                    if (!groups.TryGetValue(stem, out var orphanRow))
                    {
                        orphanRow = new MuxingRowItem(stem);
                        groups[stem] = orphanRow;
                    }
                    row = orphanRow;
                }
            }

            bool isAudio = AppConstants.AudioContainers.Contains(ext) || AppConstants.AudioStreams.Contains(ext);
            if (isAudio)
            {
                InsertTrackSorted(row.AudioTracks, file, "Аудио");
            }
            else
            {
                var role = MuxTrackTyper.ResolveSubsRole(row.Stem, stem, file.MuxRoleOverride);
                var target = role switch
                {
                    MuxSubsRole.Full => row.FullSubsTracks,
                    MuxSubsRole.Signs => row.SignsSubsTracks,
                    _ => row.OtherSubsTracks
                };
                InsertTrackSorted(target, file, MuxTrackTyper.GetRoleLabel(role), showRoleSelector: true);
            }
        }

        string groupName = _settingsManager.GetSafeGroupName(ActiveScript.Name);
        string containerChoice = _settingsManager.GetSetting(groupName, "output_container", "MKV");
        bool isMp4 = containerChoice.Equals("MP4", StringComparison.OrdinalIgnoreCase);

        foreach (var row in groups.Values)
        {
            // Основная аудиодорожка — явно помеченная или первая по имени; идёт первой в списке.
            var orderedAudio = row.AudioTracks
                .OrderBy(t => t.File.MuxAudioMain ? 0 : 1)
                .ThenBy(t => t.File.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            for (int i = 0; i < orderedAudio.Count; i++)
            {
                orderedAudio[i].IsMainTrack = i == 0;
                int current = row.AudioTracks.IndexOf(orderedAudio[i]);
                if (current != i)
                {
                    row.AudioTracks.Move(current, i);
                }
            }

            if (isMp4)
            {
                bool badAudio = row.AudioTracks.Any(t =>
                {
                    string aExt = Path.GetExtension(t.File.FilePath).ToLowerInvariant();
                    return aExt == ".flac" || aExt == ".thd" || aExt == ".truehd" || aExt == ".dts" || aExt == ".dtshd";
                });
                row.AudioWarning = badAudio ? " ⚠️ [Часть дорожек не поддерживается в MP4]" : string.Empty;

                bool badSubs = row.FullSubsTracks.Concat(row.SignsSubsTracks).Concat(row.OtherSubsTracks).Any(t =>
                {
                    string sExt = Path.GetExtension(t.File.FilePath).ToLowerInvariant();
                    return sExt == ".ass" || sExt == ".ssa";
                });
                row.SubsWarning = badSubs ? " ⚠️ [Часть дорожек не поддерживается в MP4]" : string.Empty;
            }
            else
            {
                row.AudioWarning = string.Empty;
                row.SubsWarning = string.Empty;
            }

            _muxingRows.Add(row);
        }
    }

    /// <summary>
    /// Вставляет дорожку в коллекцию категории с сохранением порядка по имени файла.
    /// Дубликаты по пути игнорируются.
    /// </summary>
    private static void InsertTrackSorted(ObservableCollection<MuxTrackItem> target, FileQueueItem file, string kindLabel, bool showRoleSelector = false)
    {
        if (target.Any(t => t.File.FilePath.Equals(file.FilePath, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        int insertAt = 0;
        while (insertAt < target.Count &&
            string.Compare(target[insertAt].File.FileName, file.FileName, StringComparison.OrdinalIgnoreCase) < 0)
        {
            insertAt++;
        }
        target.Insert(insertAt, new MuxTrackItem(file, kindLabel, showRoleSelector));
    }

    private void UpdateEmptyState()
    {
        // Сбрасываем подсветку всех дроп-зон: drag-сессия могла оборваться
        // без DragLeave/Drop (Esc, сброс мимо зоны), и пунктир остался бы подсвеченным.
        ResetAllZoneHighlights();

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

    private void CopyUrlButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is FileQueueItem item)
        {
            try
            {
                var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                package.SetText(item.FilePath);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                _logService.Info($"Ссылка скопирована в буфер обмена: '{item.FilePath}'", "FileListControl");
            }
            catch (Exception ex)
            {
                _logService.Exception(ex, "Ошибка при копировании ссылки в буфер обмена", "FileListControl");
            }
        }
    }

    private void RetryDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is FileQueueItem item)
        {
            item.ResetStateForRetry();
            _logService.Info($"Состояние элемента очереди '{item.DisplayName}' сброшено для повторного скачивания.", "FileListControl");
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
    /// Удаляет видео и все привязанные дорожки текущей строки из основной очереди.
    /// </summary>
    private void DeleteMuxingRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is MuxingRowItem row)
        {
            if (row.VideoFile != null) Files.Remove(row.VideoFile);
            foreach (var track in row.AudioTracks.ToList()) Files.Remove(track.File);
            foreach (var track in row.FullSubsTracks.Concat(row.SignsSubsTracks).Concat(row.OtherSubsTracks).ToList()) Files.Remove(track.File);
            SyncMuxingRows();
            UpdateEmptyState();
        }
    }

    /// <summary>
    /// Удаляет одну дорожку из очереди (кнопка × в развёрнутой категории).
    /// </summary>
    private void RemoveMuxTrack_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is MuxTrackItem track)
        {
            Files.Remove(track.File);
            SyncMuxingRows();
            UpdateEmptyState();
        }
    }

    /// <summary>
    /// Сбрасывает ручную привязку дорожки к группе (возврат к автосопоставлению по имени).
    /// </summary>
    private void UnpinMuxTrack_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is MuxTrackItem track)
        {
            track.File.MuxPinnedStem = null;
            SyncMuxingRows();
        }
    }

    /// <summary>
    /// Применяет выбранную вручную роль субтитров (Авто — по суффиксу имени).
    /// </summary>
    private void TrackRole_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo || combo.DataContext is not MuxTrackItem track)
        {
            return;
        }

        MuxSubsRole? selected = combo.SelectedIndex switch
        {
            1 => MuxSubsRole.Full,
            2 => MuxSubsRole.Signs,
            3 => MuxSubsRole.Other,
            _ => null
        };

        if (track.File.MuxRoleOverride == selected)
        {
            return;
        }

        track.File.MuxRoleOverride = selected;
        SyncMuxingRows();
    }

    private void TrackZone_DragOver(object sender, DragEventArgs e)
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
            e.DragUIOverride.IsCaptionVisible = false;
            SetGlobalZoneHighlight(sender as FrameworkElement, true);
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }

        e.Handled = true;
    }

    /// <summary>
    /// Снимает подсветку глобальной дроп-зоны при уходе курсора.
    /// </summary>
    private void TrackZone_DragLeave(object sender, DragEventArgs e)
    {
        SetGlobalZoneHighlight(sender as FrameworkElement, false);
        e.Handled = true;
    }

    /// <summary>
    /// Включает или выключает акцентную подсветку оверлея глобальной дроп-зоны.
    /// </summary>
    private void SetGlobalZoneHighlight(FrameworkElement? zone, bool isHighlighted)
    {
        string tag = zone?.Tag as string ?? string.Empty;
        DropZoneOverlay? overlay = tag switch
        {
            "global-audio-main" => AudioMainZoneOverlay,
            "global-audio-extra" => AudioExtraZoneOverlay,
            "global-full" => FullZoneOverlay,
            "global-signs" => SignsZoneOverlay,
            _ => null
        };
        overlay?.SetHighlighted(isHighlighted);
    }

    /// <summary>
    /// Обработчик сброса файлов на верхние дроп-зоны категорий 2x2.
    /// Файлы добавляются в очередь и расходятся по видео-группам по совпадению имен.
    /// Зона "RU Аудио (Осн.)" помечает дорожку основной (русский, default/forced) —
    /// в каждой серии основная только одна, предыдущая разжалуется в дополнительные.
    /// Зона "RU Аудио (Доп.)" помечает дорожку дополнительной.
    /// Зоны субтитров определяют роль: "global-full" — полные, "global-signs" — надписи.
    /// </summary>
    private async void GlobalZone_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (IsProcessing) return;

        SetGlobalZoneHighlight(sender as FrameworkElement, false);
        string zoneTag = (sender as FrameworkElement)?.Tag as string ?? string.Empty;
        MuxSubsRole? zoneRole = zoneTag.Equals("global-full", StringComparison.OrdinalIgnoreCase)
            ? MuxSubsRole.Full
            : zoneTag.Equals("global-signs", StringComparison.OrdinalIgnoreCase)
                ? MuxSubsRole.Signs
                : null;
        bool isAudioMainZone = zoneTag.Equals("global-audio-main", StringComparison.OrdinalIgnoreCase);
        bool isAudioExtraZone = zoneTag.Equals("global-audio-extra", StringComparison.OrdinalIgnoreCase);

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

                int attached = 0;
                foreach (string path in paths)
                {
                    var queueItem = Files.FirstOrDefault(f => f.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase));
                    if (queueItem == null) continue;

                    string ext = Path.GetExtension(path).ToLowerInvariant();
                    bool isSubs = AppConstants.SubtitleExtensions.Contains(ext);
                    bool isAudio = AppConstants.AudioContainers.Contains(ext) || AppConstants.AudioStreams.Contains(ext);
                    if (!isSubs && !isAudio) continue;

                    if (isAudio && isAudioMainZone)
                    {
                        DemoteOtherAudioMains(queueItem);
                        queueItem.MuxAudioMain = true;
                    }
                    else if (isAudio && isAudioExtraZone)
                    {
                        queueItem.MuxAudioMain = false;
                    }

                    if (isSubs && zoneRole != null)
                    {
                        queueItem.MuxRoleOverride = zoneRole;
                    }
                    attached++;
                }

                SyncMuxingRows();
                _logService.Info($"Дроп-зона '{zoneTag}': добавлено файлов: {attached}", "FileListControl");
            }
        }
        catch (Exception ex)
        {
            _logService.Exception(ex, "Ошибка при сбросе файлов на дроп-зону категорий сборки MKV", "FileListControl");
        }
    }

    /// <summary>
    /// Снимает флаг основной аудиодорожки с других файлов той же видео-группы,
    /// так как в каждой серии основная дорожка только одна.
    /// </summary>
    private void DemoteOtherAudioMains(FileQueueItem newMain)
    {
        string newStem = Path.GetFileNameWithoutExtension(newMain.FilePath);
        var videoStems = Files
            .Where(f => AppConstants.VideoContainers.Contains(Path.GetExtension(f.FilePath).ToLowerInvariant()))
            .Select(f => Path.GetFileNameWithoutExtension(f.FilePath))
            .ToList();
        string? targetStem = MuxGroupMatcher.FindBestVideoStem(videoStems, newStem);
        if (targetStem == null) return;

        foreach (var other in Files)
        {
            if (ReferenceEquals(other, newMain) || !other.MuxAudioMain) continue;

            string otherExt = Path.GetExtension(other.FilePath).ToLowerInvariant();
            bool otherIsAudio = AppConstants.AudioContainers.Contains(otherExt) || AppConstants.AudioStreams.Contains(otherExt);
            if (!otherIsAudio) continue;

            string otherStem = Path.GetFileNameWithoutExtension(other.FilePath);
            if (MuxGroupMatcher.BelongsToVideo(targetStem, otherStem, other.MuxPinnedStem))
            {
                other.MuxAudioMain = false;
                _logService.Info($"Дорожка '{other.FileName}' переведена в дополнительные (основная: '{newMain.FileName}')", "FileListControl");
            }
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

    /// <summary>
    /// Гасит подсветку основной дроп-зоны и всех маленьких зон категорий.
    /// SetHighlighted внутри оверлеев игнорирует повторный сброс, поэтому вызов дешёвый.
    /// </summary>
    private void ResetAllZoneHighlights()
    {
        SetFileDropHighlight(false);
        AudioMainZoneOverlay?.SetHighlighted(false);
        AudioExtraZoneOverlay?.SetHighlighted(false);
        FullZoneOverlay?.SetHighlighted(false);
        SignsZoneOverlay?.SetHighlighted(false);
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
            e.DragUIOverride.IsCaptionVisible = false;
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
        ResetAllZoneHighlights();
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
        item.AvailableFormats.Add(new DownloadFormatItem
        {
            Id = "best_quality",
            DisplayName = "Наилучшее качество (Видео+Аудио)",
            FormatArg = "bv*+ba/b",
            IsAudioOnly = false,
            Height = 99999
        });
        item.AvailableFormats.Add(new DownloadFormatItem
        {
            Id = "best_audio",
            DisplayName = "Только звук (Наилучшее качество)",
            FormatArg = "ba/b",
            IsAudioOnly = true,
            Height = 0
        });
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

                // 2. Parse Formats с дедупликацией по уникальным разрешениям
                var tempFormats = new List<DownloadFormatItem>();
                tempFormats.Add(new DownloadFormatItem
                {
                    Id = "best_quality",
                    DisplayName = "Наилучшее качество (Видео+Аудио)",
                    FormatArg = "bv*+ba/b",
                    IsAudioOnly = false,
                    Height = 99999
                });
                tempFormats.Add(new DownloadFormatItem
                {
                    Id = "best_audio",
                    DisplayName = "Только звук (Наилучшее качество)",
                    FormatArg = "ba/b",
                    IsAudioOnly = true,
                    Height = 0
                });

                if (root.TryGetProperty("formats", out var formatsProp) && formatsProp.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    // Вспомогательный класс для сбора и дедупликации потоков
                    var videoStreamGroups = new Dictionary<string, (int height, int fps, double tbr, string formatId, string note)>();

                    foreach (var format in formatsProp.EnumerateArray())
                    {
                        string formatId = format.TryGetProperty("format_id", out var fid) ? fid.GetString() ?? "" : "";
                        if (string.IsNullOrEmpty(formatId)) continue;

                        bool hasVideo = format.TryGetProperty("vcodec", out var vcodecProp) && vcodecProp.GetString() != "none";
                        if (!hasVideo) continue;

                        int height = 0;
                        if (format.TryGetProperty("height", out var hP) && hP.ValueKind == System.Text.Json.JsonValueKind.Number)
                        {
                            height = (int)Math.Round(hP.GetDouble());
                        }

                        if (height <= 0) continue;

                        int fps = 0;
                        if (format.TryGetProperty("fps", out var fpsP) && fpsP.ValueKind == System.Text.Json.JsonValueKind.Number)
                        {
                            fps = (int)Math.Round(fpsP.GetDouble());
                        }

                        double tbr = 0;
                        if (format.TryGetProperty("tbr", out var tbrP) && tbrP.ValueKind == System.Text.Json.JsonValueKind.Number)
                        {
                            tbr = tbrP.GetDouble();
                        }

                        // Нормализуем FPS для группировки: 50-60 -> 60fps, иначе стандарт
                        int fpsGroup = fps >= 48 ? 60 : 0;
                        string groupKey = $"{height}p" + (fpsGroup > 0 ? $"_{fpsGroup}fps" : "");

                        // Понятное обозначение разрешения
                        string resName = height switch
                        {
                            >= 2160 => $"4K Ultra HD ({height}p)",
                            >= 1440 => $"2K Quad HD ({height}p)",
                            >= 1080 => $"Full HD ({height}p)",
                            >= 720 => $"HD ({height}p)",
                            _ => $"{height}p"
                        };

                        if (fpsGroup > 0)
                        {
                            resName += $" {fpsGroup}fps";
                        }

                        // Сохраняем поток с максимальным битрейтом для данного разрешения
                        if (!videoStreamGroups.TryGetValue(groupKey, out var existing) || tbr > existing.tbr)
                        {
                            videoStreamGroups[groupKey] = (height, fpsGroup, tbr, formatId, resName);
                        }
                    }

                    // Сортируем разрешения по убыванию качества (высота, затем fps)
                    var sortedStreams = videoStreamGroups.Values
                        .OrderByDescending(v => v.height)
                        .ThenByDescending(v => v.fps)
                        .ToList();

                    foreach (var stream in sortedStreams)
                    {
                        tempFormats.Add(new DownloadFormatItem
                        {
                            Id = $"video_{stream.height}p_{stream.fps}",
                            DisplayName = stream.note,
                            FormatArg = $"bv*[height<={stream.height}]+ba/b[height<={stream.height}]/b",
                            IsAudioOnly = false,
                            Height = stream.height
                        });
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
