// -*- coding: utf-8 -*-
using System;
using KTools_App.Services.Contracts;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Text;
using Windows.Storage;
using Windows.ApplicationModel.DataTransfer;

using KTools_App.Core;
using KTools_App.Scripts;
using Microsoft.Extensions.DependencyInjection;

namespace KTools_App.UI.Controls;

/// <summary>
/// Представляет вариант замены, отображаемый в ComboBox.
/// Все комментарии и свойства выполнены исключительно на русском языке.
/// </summary>
public sealed class ReplacementOption
{
    /// <summary>
    /// Отображаемый текст в интерфейсе.
    /// </summary>
    public string DisplayText { get; set; } = string.Empty;

    /// <summary>
    /// Полный путь к файлу-замене.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Идентификатор дорожки во внешнем файле (0 для простых файлов).
    /// </summary>
    public int SourceTrackId { get; set; }

    /// <summary>
    /// Тип дорожки (video, audio, subtitles).
    /// </summary>
    public string TrackType { get; set; } = string.Empty;

    public override string ToString() => DisplayText;
}

/// <summary>
/// Представляет добавленный внешний файл для замены.
/// </summary>
public sealed class ReplacementFileItem
{
    /// <summary>
    /// Имя файла.
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Полный путь к файлу.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Дополнительная техническая информация (размер, кодеки).
    /// </summary>
    public string InfoText { get; set; } = string.Empty;

    /// <summary>
    /// Структура метаданных (null для простых медиафайлов).
    /// </summary>
    public MediaStructure? MediaInfo { get; set; }
}

/// <summary>
/// Класс логики (Code-Behind) для StreamReplaceControl.
/// Управляет интерактивным выбором внешних файлов-замен для дорожек исходного контейнера.
/// </summary>
public sealed partial class StreamReplaceControl : UserControl
{
    private readonly ILogService _logService;
    private readonly IMediaProbeService _mediaProbeService;

    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue = 
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

    private ObservableCollection<FileQueueItem>? _files;
    private readonly ObservableCollection<ReplacementFileItem> _replacementFiles = new();
    private AbstractScript? _activeScript;

    // Хранение: track_id оригинального файла -> ComboBox в UI
    private readonly Dictionary<int, ComboBox> _combos = new();
    // Хранение: track_id оригинального файла -> MediaTrack исходника
    private readonly Dictionary<int, MediaTrack> _sourceTracks = new();

    /// <summary>
    /// Текущий активный скрипт обработки.
    /// </summary>
    public AbstractScript? ActiveScript
    {
        get => _activeScript;
        set => _activeScript = value;
    }

    /// <summary>
    /// Инициализирует новый экземпляр StreamReplaceControl.
    /// </summary>
    public StreamReplaceControl()
    {
        _logService = App.Services.GetRequiredService<ILogService>();
        _mediaProbeService = App.Services.GetRequiredService<IMediaProbeService>();

        InitializeComponent();
        ReplacementFilesListView.ItemsSource = _replacementFiles;
        _replacementFiles.CollectionChanged += OnReplacementFilesChanged;
        Loaded += StreamReplaceControl_Loaded;
    }

    private void StreamReplaceControl_Loaded(object sender, RoutedEventArgs e)
    {
        _logService.Info("Загрузка виджета подмены дорожек StreamReplaceControl", "StreamReplaceControl");
        if (_files != null)
        {
            _files.CollectionChanged -= OnFilesCollectionChanged;
            _files.CollectionChanged += OnFilesCollectionChanged;
            SubscribeToItems();
            // Вызов RebuildUI() намеренно исключен, так как Populate() уже строит интерфейс,
            // а повторный вызов при Loaded (например, при переключении вкладок) сбрасывает выбор в ComboBox.
        }
    }

    /// <summary>
    /// Связывает элемент управления с очередью файлов.
    /// </summary>
    public void Populate(ObservableCollection<FileQueueItem> files)
    {
        _logService.Info("Инициализация очереди файлов в виджете подмены дорожек", "StreamReplaceControl");
        if (_files != null)
        {
            _files.CollectionChanged -= OnFilesCollectionChanged;
            UnsubscribeFromItems();
        }

        _files = files;

        if (_files != null)
        {
            _files.CollectionChanged += OnFilesCollectionChanged;
            SubscribeToItems();
        }

        RebuildUI();
    }

    /// <summary>
    /// Собирает все назначенные пользователем замены.
    /// </summary>
    /// <param name="replacements">Словарь замен: track_id исходника -> словарь с путями и ID внешних дорожек.</param>
    public void GetReplacements(out Dictionary<string, object> replacements)
    {
        replacements = new Dictionary<string, object>();

        try
        {
            _logService.Info("Сбор назначенных замен из ComboBox", "StreamReplaceControl");
            foreach (var kvp in _combos)
            {
                int comboKey = kvp.Key;
                ComboBox combo = kvp.Value;

                if (combo.SelectedItem is ReplacementOption opt && !string.IsNullOrEmpty(opt.FilePath))
                {
                    string srcFile = combo.Tag?.ToString() ?? string.Empty;
                    if (string.IsNullOrEmpty(srcFile)) continue;

                    if (!replacements.ContainsKey(srcFile))
                    {
                        replacements[srcFile] = new Dictionary<string, object>();
                    }

                    var fileDict = (Dictionary<string, object>)replacements[srcFile];

                    if (_sourceTracks.TryGetValue(comboKey, out var track))
                    {
                        int actualTrackId = track.TrackId;
                        var repData = new Dictionary<string, object>
                        {
                            { "path", opt.FilePath },
                            { "src_id", opt.SourceTrackId }
                        };
                        fileDict[actualTrackId.ToString()] = repData;
                    }
                }
            }
            _logService.Info($"Сбор замен завершен. Файлов с заменами: {replacements.Count}", "StreamReplaceControl");
        }
        catch (Exception ex)
        {
            _logService.Exception(ex, "Ошибка при сборе назначенных замен", "StreamReplaceControl");
        }
    }

    private void SubscribeToItems()
    {
        if (_files == null) return;
        foreach (var item in _files)
        {
            item.PropertyChanged -= OnItemPropertyChanged;
            item.PropertyChanged += OnItemPropertyChanged;
        }
    }

    private void UnsubscribeFromItems()
    {
        if (_files == null) return;
        foreach (var item in _files)
        {
            item.PropertyChanged -= OnItemPropertyChanged;
        }
    }

    private void OnFilesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            SubscribeToItems();
            RebuildUI();
        });
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileQueueItem.MediaInfo))
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                _logService.Info("Завершен фоновый анализ исходного файла, перестраиваем интерфейс", "StreamReplaceControl");
                RebuildUI();
            });
        }
    }

    private void OnReplacementFilesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        bool hasReplacements = _replacementFiles.Count > 0;
        if (ReplacementsEmptyState != null)
        {
            ReplacementsEmptyState.Visibility = hasReplacements ? Visibility.Collapsed : Visibility.Visible;
        }
        ReplacementFilesListView.Visibility = hasReplacements ? Visibility.Visible : Visibility.Collapsed;

        // Если есть добавленные файлы для подмены — скрываем обычный фоновый пунктир (он появится только при наведении Drag & Drop)
        DropOverlay?.SetVisible(!hasReplacements);

        // При любом изменении списка замен обновляем ComboBox-ы в карточке назначений
        UpdateAllComboBoxOptions();
    }

    /// <summary>
    /// Перестраивает интерфейс сопоставления дорожек исходных контейнеров (с поддержкой пакетной обработки нескольких файлов).
    /// </summary>
    private void RebuildUI()
    {
        try
        {
            // Сохраняем ранее выбранные значения замен (ключ: filePath_trackId)
            var savedSelections = new Dictionary<string, ReplacementOption>();
            foreach (var kvp in _combos)
            {
                int comboKey = kvp.Key;
                ComboBox combo = kvp.Value;

                if (combo.SelectedItem is ReplacementOption selOpt && !string.IsNullOrEmpty(selOpt.FilePath))
                {
                    string srcFile = combo.Tag?.ToString() ?? string.Empty;
                    if (_sourceTracks.TryGetValue(comboKey, out var track))
                    {
                        string compoundKey = $"{srcFile}_{track.TrackId}";
                        savedSelections[compoundKey] = selOpt;
                    }
                }
            }

            _combos.Clear();
            _sourceTracks.Clear();
            ReplacementsStackPanel.Children.Clear();

            if (_files == null || _files.Count == 0)
            {
                TracksEmptyState.Visibility = Visibility.Visible;
                TracksProgressRing.IsActive = false;
                TracksProgressRing.Visibility = Visibility.Collapsed;
                if (TracksEmptyIcon != null) TracksEmptyIcon.Visibility = Visibility.Visible;
                TracksEmptyText.Text = "Перетащите сюда исходные файлы или добавьте их на вкладке «Файлы».";
                TracksScrollViewer.Visibility = Visibility.Collapsed;
                TargetDropOverlay?.SetVisible(true);
                return;
            }

            // Если есть добавленные файлы — скрываем фоновую окантовку (появится только при наведении мышью с файлом)
            TargetDropOverlay?.SetVisible(false);

            // Проверяем, есть ли файлы с неполной информацией
            bool isAnyAnalyzing = _files.Any(f => f.MediaInfo == null);

            if (isAnyAnalyzing && _files.All(f => f.MediaInfo == null))
            {
                TracksEmptyState.Visibility = Visibility.Visible;
                TracksProgressRing.IsActive = true;
                TracksProgressRing.Visibility = Visibility.Visible;
                if (TracksEmptyIcon != null) TracksEmptyIcon.Visibility = Visibility.Collapsed;
                TracksEmptyText.Text = "Выполняется фоновый анализ структуры исходных файлов...";
                TracksScrollViewer.Visibility = Visibility.Collapsed;
                return;
            }

            TracksEmptyState.Visibility = Visibility.Collapsed;
            TracksScrollViewer.Visibility = Visibility.Visible;

            int globalComboId = 0;

            // Две логики отображения: если файл один — выводим компактно; если файлов несколько — оборачиваем каждый файл в отдельный разворачиваемый Expander/Card
            bool isBatch = _files.Count > 1;

            foreach (var fileItem in _files)
            {
                if (fileItem.MediaInfo == null) continue;

                var structure = fileItem.MediaInfo;

                // Выводим только видео, аудио и субтитры (без вложений)
                var tracks = structure.Tracks.Where(t => 
                    t.TrackType.Equals("video", StringComparison.OrdinalIgnoreCase) ||
                    t.TrackType.Equals("audio", StringComparison.OrdinalIgnoreCase) ||
                    t.TrackType.Equals("subtitles", StringComparison.OrdinalIgnoreCase)).ToList();

                if (tracks.Count == 0) continue;

                // Панель для дорожек конкретного файла
                var tracksContainerPanel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 4) };

                foreach (var track in tracks)
                {
                    globalComboId++;
                    int comboKey = globalComboId;
                    _sourceTracks[comboKey] = track;

                    var rowContainer = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 8) };

                    string typeLabel = track.TypeLabel;
                    string langStr = !string.IsNullOrEmpty(track.Language) && track.Language != "und" ? track.Language.ToUpperInvariant() : "UND";
                    
                    string descText = $"{typeLabel} #{track.TrackId} • {track.Codec.ToUpperInvariant()}";
                    if (track.TrackType.Equals("audio", StringComparison.OrdinalIgnoreCase))
                    {
                        descText += $" • {langStr} • {track.Channels} ch";
                    }
                    else if (track.TrackType.Equals("subtitles", StringComparison.OrdinalIgnoreCase))
                    {
                        descText += $" • {langStr}";
                    }
                    else if (track.TrackType.Equals("video", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(track.Resolution))
                    {
                        descText += $" • {track.Resolution}";
                    }

                    if (!string.IsNullOrEmpty(track.Name))
                    {
                        descText += $" • \"{track.Name}\"";
                    }

                    var descLabel = new TextBlock
                    {
                        Text = descText,
                        FontSize = 12,
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                    };
                    rowContainer.Children.Add(descLabel);

                    var combo = new ComboBox
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        Height = 32,
                        Tag = fileItem.FilePath // Сохраняем привязку к исходному файлу
                    };
                    rowContainer.Children.Add(combo);

                    _combos[comboKey] = combo;
                    tracksContainerPanel.Children.Add(rowContainer);
                }

                if (isBatch)
                {
                    // Оборачиваем в карточку Expander для пакетной обработки
                    var expander = new Expander
                    {
                        Header = $"{fileItem.FileName}",
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        IsExpanded = true,
                        Margin = new Thickness(0, 0, 0, 8)
                    };
                    expander.Content = tracksContainerPanel;
                    ReplacementsStackPanel.Children.Add(expander);
                }
                else
                {
                    ReplacementsStackPanel.Children.Add(tracksContainerPanel);
                }
            }

            UpdateAllComboBoxOptions();

            // Восстанавливаем сохраненный выбор замен
            foreach (var kvp in _combos)
            {
                int comboKey = kvp.Key;
                ComboBox combo = kvp.Value;
                string srcFile = combo.Tag?.ToString() ?? string.Empty;

                if (_sourceTracks.TryGetValue(comboKey, out var track))
                {
                    string compoundKey = $"{srcFile}_{track.TrackId}";
                    if (savedSelections.TryGetValue(compoundKey, out var saved))
                    {
                        var matchOpt = combo.Items.Cast<ReplacementOption>().FirstOrDefault(o =>
                            o.FilePath.Equals(saved.FilePath, StringComparison.OrdinalIgnoreCase) &&
                            o.SourceTrackId == saved.SourceTrackId);
                        if (matchOpt != null)
                        {
                            combo.SelectedItem = matchOpt;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logService.Exception(ex, "Критическая ошибка при перестроении UI подмены", "StreamReplaceControl");
        }
    }

    /// <summary>
    /// Обновляет доступные варианты замен во всех выпадающих списках.
    /// </summary>
    private void UpdateAllComboBoxOptions()
    {
        try
        {
            foreach (var kvp in _combos)
            {
                int trackId = kvp.Key;
                ComboBox combo = kvp.Value;
                MediaTrack originalTrack = _sourceTracks[trackId];

                // Сохраняем текущий выбор
                var currentSelected = combo.SelectedItem as ReplacementOption;

                combo.Items.Clear();

                // 1. Опция "Не заменять"
                var defaultOpt = new ReplacementOption
                {
                    DisplayText = "— Не заменять —",
                    FilePath = string.Empty,
                    SourceTrackId = 0,
                    TrackType = originalTrack.TrackType
                };
                combo.Items.Add(defaultOpt);

                // 2. Опции из добавленных файлов-замен
                foreach (var repFile in _replacementFiles)
                {
                    string ext = Path.GetExtension(repFile.FilePath).ToLowerInvariant();
                    
                    // Если это контейнер с метаданными (MKV, MP4)
                    if (repFile.MediaInfo != null)
                    {
                        var compatibleTracks = repFile.MediaInfo.Tracks.Where(t => 
                            t.TrackType.Equals(originalTrack.TrackType, StringComparison.OrdinalIgnoreCase)).ToList();

                        foreach (var subTrack in compatibleTracks)
                        {
                            string subLang = !string.IsNullOrEmpty(subTrack.Language) && subTrack.Language != "und" ? subTrack.Language.ToUpperInvariant() : "UND";
                            string disp = $"{repFile.FileName} [ID {subTrack.TrackId}: {subTrack.Codec.ToUpperInvariant()}";
                            if (subTrack.TrackType.Equals("audio", StringComparison.OrdinalIgnoreCase))
                            {
                                disp += $", {subLang}, {subTrack.Channels} ch";
                            }
                            else if (subTrack.TrackType.Equals("subtitles", StringComparison.OrdinalIgnoreCase))
                            {
                                disp += $", {subLang}";
                            }
                            disp += "]";

                            combo.Items.Add(new ReplacementOption
                            {
                                DisplayText = disp,
                                FilePath = repFile.FilePath,
                                SourceTrackId = subTrack.TrackId,
                                TrackType = originalTrack.TrackType
                            });
                        }
                    }
                    else
                    {
                        // Простые файлы: проверяем совместимость по расширению
                        if (IsExtensionCompatibleWithTrackType(ext, originalTrack.TrackType))
                        {
                            combo.Items.Add(new ReplacementOption
                            {
                                DisplayText = repFile.FileName,
                                FilePath = repFile.FilePath,
                                SourceTrackId = 0,
                                TrackType = originalTrack.TrackType
                            });
                        }
                    }
                }

                // Восстанавливаем выбор, если он все еще доступен
                if (currentSelected != null && !string.IsNullOrEmpty(currentSelected.FilePath))
                {
                    var found = combo.Items.Cast<ReplacementOption>().FirstOrDefault(o => 
                        o.FilePath.Equals(currentSelected.FilePath, StringComparison.OrdinalIgnoreCase) && 
                        o.SourceTrackId == currentSelected.SourceTrackId);

                    if (found != null)
                    {
                        combo.SelectedItem = found;
                        continue;
                    }
                }

                combo.SelectedIndex = 0;
            }
        }
        catch (Exception ex)
        {
            _logService.Exception(ex, "Ошибка при обновлении опций ComboBox", "StreamReplaceControl");
        }
    }

    /// <summary>
    /// Проверяет совместимость расширения файла-замены с типом оригинальной дорожки.
    /// </summary>
    private static bool IsExtensionCompatibleWithTrackType(string ext, string trackType)
    {
        if (trackType.Equals("audio", StringComparison.OrdinalIgnoreCase))
        {
            return AppConstants.AudioContainers.Contains(ext) || AppConstants.AudioStreams.Contains(ext);
        }
        if (trackType.Equals("subtitles", StringComparison.OrdinalIgnoreCase))
        {
            return AppConstants.SubtitleExtensions.Contains(ext);
        }
        if (trackType.Equals("video", StringComparison.OrdinalIgnoreCase))
        {
            return AppConstants.VideoContainers.Contains(ext);
        }
        return false;
    }

    /// <summary>
    /// Обработчик кнопки «Добавить файлы...» для файлов-замен.
    /// </summary>
    private async void AddReplacementButton_Click(object sender, RoutedEventArgs e)
    {
        _logService.Info("Открытие диалога выбора файлов-замен", "StreamReplaceControl");
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.CurrentMainWindow);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(windowId);

            picker.ViewMode = Microsoft.Windows.Storage.Pickers.PickerViewMode.List;
            picker.SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.ComputerFolder;

            // Разрешаем любые форматы аудио, видео и субтитров
            var allExts = AppConstants.VideoContainers
                .Concat(AppConstants.AudioContainers)
                .Concat(AppConstants.AudioStreams)
                .Concat(AppConstants.SubtitleExtensions)
                .Distinct();

            foreach (var ext in allExts)
            {
                picker.FileTypeFilter.Add(ext);
            }

            var files = await picker.PickMultipleFilesAsync();
            if (files != null && files.Count > 0)
            {
                AddReplacementFiles(files.Select(f => f.Path));
            }
        }
        catch (Exception ex)
        {
            _logService.Exception(ex, "Ошибка при выборе файлов-замен", "StreamReplaceControl");
        }
    }

    /// <summary>
    /// Добавляет переданные пути к файлам в список файлов для подмены.
    /// </summary>
    private void AddReplacementFiles(IEnumerable<string> filePaths)
    {
        int addedCount = 0;
        foreach (var filePath in filePaths)
        {
            if (string.IsNullOrWhiteSpace(filePath)) continue;

            // Проверяем на дубликаты
            if (_replacementFiles.Any(f => f.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var item = new ReplacementFileItem
            {
                FileName = Path.GetFileName(filePath),
                FilePath = filePath,
                InfoText = "Анализ файла..."
            };
            _replacementFiles.Add(item);
            addedCount++;

            // Фоновый анализ метаданных для контейнеров
            StartReplacementFileAnalysis(item);
        }

        if (addedCount > 0)
        {
            _logService.Info($"Добавлено внешних файлов-замен: {addedCount}", "StreamReplaceControl");
        }
    }

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
                }
            }
            catch (Exception ex)
            {
                _logService.Exception(ex, $"Ошибка при анализе исходного файла '{item.FileName}'", "StreamReplaceControl");
            }
        });
    }

    private void StartReplacementFileAnalysis(ReplacementFileItem item)
    {
        string ext = Path.GetExtension(item.FilePath).ToLowerInvariant();
        bool isContainer = AppConstants.VideoContainers.Contains(ext) || AppConstants.AudioContainers.Contains(ext);

        if (!isContainer)
        {
            // Для простых файлов (например, .srt) вычисляем размер
            try
            {
                var fi = new FileInfo(item.FilePath);
                item.InfoText = $"Файл • {fi.Length / 1024.0:F1} КБ";
            }
            catch
            {
                item.InfoText = "Внешний файл";
            }
            // Форсируем обновление списка
            int idx = _replacementFiles.IndexOf(item);
            if (idx >= 0)
            {
                _replacementFiles[idx] = item;
            }
            return;
        }

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
                        var tracksInfo = structure.Tracks
                            .GroupBy(t => t.TrackType)
                            .Select(g => $"{g.Count()} {g.Key.ToLowerInvariant()}");
                        item.InfoText = $"Контейнер • {string.Join(", ", tracksInfo)}";

                        // Обновляем элемент в коллекции
                        int idx = _replacementFiles.IndexOf(item);
                        if (idx >= 0)
                        {
                            _replacementFiles[idx] = item;
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                _logService.Exception(ex, $"Ошибка при анализе файла-замены '{item.FileName}'", "StreamReplaceControl");
                _dispatcherQueue.TryEnqueue(() =>
                {
                    item.InfoText = "Ошибка анализа";
                    int idx = _replacementFiles.IndexOf(item);
                    if (idx >= 0)
                    {
                        _replacementFiles[idx] = item;
                    }
                });
            }
        });
    }

    private void DeleteReplacementFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is ReplacementFileItem item)
        {
            _replacementFiles.Remove(item);
        }
    }

    private void ClearReplacementsButton_Click(object sender, RoutedEventArgs e)
    {
        _replacementFiles.Clear();
    }

    #region Drag and Drop Handling

    private void SetDropTargetHighlight(bool isHighlighted)
    {
        DropOverlay?.SetHighlighted(isHighlighted);

        if (ReplacementFilesCardBorder != null)
        {
            if (Application.Current.Resources.TryGetValue(isHighlighted ? "CardBackgroundFillColorSecondaryBrush" : "CardBackgroundFillColorDefaultBrush", out var bgBrush) && bgBrush is Brush bg)
            {
                ReplacementFilesCardBorder.Background = bg;
            }
        }
    }

    private void ReplacementCard_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Добавить файлы для подмены";
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsContentVisible = true;
            e.DragUIOverride.IsGlyphVisible = true;
            e.Handled = true;
            
            SetDropTargetHighlight(true);
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }
    }

    private void ReplacementCard_DragLeave(object sender, DragEventArgs e)
    {
        SetDropTargetHighlight(false);
    }

    private async void ReplacementCard_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        SetDropTargetHighlight(false);

        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            var deferral = e.GetDeferral();
            try
            {
                var items = await e.DataView.GetStorageItemsAsync();
                var paths = new List<string>();

                foreach (var item in items)
                {
                    if (item is StorageFile file)
                    {
                        paths.Add(file.Path);
                    }
                    else if (item is StorageFolder folder)
                    {
                        var files = await folder.GetFilesAsync();
                        paths.AddRange(files.Select(f => f.Path));
                    }
                }

                if (paths.Count > 0)
                {
                    AddReplacementFiles(paths);
                }
            }
            catch (Exception ex)
            {
                _logService.Exception(ex, "Ошибка при обработке перетаскивания файлов для подмены", "StreamReplaceControl");
            }
            finally
            {
                deferral.Complete();
            }
        }
    }
    #endregion

    #region Target Card Drag and Drop Handling

    private void SetTargetDropHighlight(bool isHighlighted)
    {
        TargetDropOverlay?.SetHighlighted(isHighlighted);

        if (TargetAssignmentsCardBorder != null)
        {
            if (Application.Current.Resources.TryGetValue(isHighlighted ? "CardBackgroundFillColorSecondaryBrush" : "CardBackgroundFillColorDefaultBrush", out var bgBrush) && bgBrush is Brush bg)
            {
                TargetAssignmentsCardBorder.Background = bg;
            }
        }
    }

    private void TargetCard_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Добавить исходные файлы в очередь";
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsContentVisible = true;
            e.DragUIOverride.IsGlyphVisible = true;
            e.Handled = true;

            SetTargetDropHighlight(true);
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }
    }

    private void TargetCard_DragLeave(object sender, DragEventArgs e)
    {
        SetTargetDropHighlight(false);
    }

    private async void TargetCard_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        SetTargetDropHighlight(false);

        if (e.DataView.Contains(StandardDataFormats.StorageItems) && _files != null)
        {
            var deferral = e.GetDeferral();
            try
            {
                var items = await e.DataView.GetStorageItemsAsync();
                var paths = new List<string>();

                foreach (var item in items)
                {
                    if (item is StorageFile file)
                    {
                        paths.Add(file.Path);
                    }
                    else if (item is StorageFolder folder)
                    {
                        var files = await folder.GetFilesAsync();
                        paths.AddRange(files.Select(f => f.Path));
                    }
                }

                if (paths.Count > 0)
                {
                    foreach (var path in paths)
                    {
                        if (!_files.Any(f => f.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase)))
                        {
                            var item = new FileQueueItem(path);
                            _files.Add(item);
                            StartFileAnalysis(item);
                        }
                    }
                    _logService.Info($"Добавлено исходных файлов через Drag & Drop карточки назначений: {paths.Count}", "StreamReplaceControl");
                }
            }
            catch (Exception ex)
            {
                _logService.Exception(ex, "Ошибка при обработке перетаскивания исходных файлов", "StreamReplaceControl");
            }
            finally
            {
                deferral.Complete();
            }
        }
    }

    #endregion
}


