// -*- coding: utf-8 -*-
using System;
using KTools_App.Services.Contracts;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Text;
using Windows.UI.Text;

using KTools_App.Core;
using KTools_App.Scripts;
using KTools_App.ViewModels;
using CommunityToolkit.WinUI.Controls;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace KTools_App.UI.Controls;

/// <summary>
/// Представляет отдельный логический узел внутри дерева дорожек.
/// Используется в качестве контекста данных для элементов TreeViewNode.
/// Все свойства и комментарии выполнены на русском языке.
/// </summary>
public sealed class TrackNodeItem
{
    /// <summary>
    /// Текст для отображения в дереве.
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Глиф иконки из шрифта Segoe MDL2 Assets.
    /// </summary>
    public string IconGlyph { get; set; } = string.Empty;

    /// <summary>
    /// Абсолютный путь к файлу на диске.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Идентификатор дорожки (null для файлов и шрифтов).
    /// </summary>
    public int? TrackId { get; set; }

    /// <summary>
    /// Идентификатор встроенного вложения (null для файлов и дорожек).
    /// </summary>
    public int? AttachmentId { get; set; }

    /// <summary>
    /// Определяет, является ли данный узел встроенным шрифтом.
    /// </summary>
    public bool IsFont { get; set; }

    /// <summary>
    /// Определяет, является ли данный узел корневым элементом файла.
    /// </summary>
    public bool IsFile { get; set; }

    /// <summary>
    /// Данные видео-, аудио- или субтитров (null для файлов и шрифтов).
    /// </summary>
    public MediaTrack? Track { get; set; }

    /// <summary>
    /// Данные встроенного вложения (null для файлов и дорожек).
    /// </summary>
    public MediaAttachment? Attachment { get; set; }

    public FontWeight Weight => IsFile ? FontWeights.SemiBold : FontWeights.Normal;

    /// <summary>
    /// Возвращает отрицательный отступ слева для дочерних узлов (дорожек),
    /// чтобы скрыть пустое пространство, зарезервированное под стрелку раскрытия.
    /// </summary>
    public Thickness NodeMargin => IsFile 
        ? new Thickness(0, 0, 0, 0) 
        : new Thickness(-24, 0, 0, 0);
}

/// <summary>
/// Пользовательский элемент управления для наглядного выбора извлекаемых дорожек
/// и шрифтов на базе WinUI 3 TreeView в режиме множественного выбора.
/// Все комментарии и логирование выполнены исключительно на русском языке.
/// </summary>
public sealed partial class TrackSelectionControl : UserControl
{
    public TrackSelectionViewModel ViewModel { get; } = App.Services.GetRequiredService<TrackSelectionViewModel>();

    private ILogService _logService => App.Services.GetRequiredService<ILogService>();

    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue = 
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
    private ObservableCollection<FileQueueItem>? _files;
    private readonly HashSet<FileQueueItem> _subscribedItems = new();

    private AbstractScript? _activeScript;

    /// <summary>
    /// Текущий активный скрипт обработки для сохранения выбора.
    /// </summary>
    public AbstractScript? ActiveScript
    {
        get => _activeScript;
        set
        {
            if (_activeScript != value)
            {
                _activeScript = value;
                ViewModel.ActiveScript = value;
                UpdateHeaderAndDescription();
            }
        }
    }

    private bool _isUpdatingSelectAll;
    private bool _isResettingFilters;
    private bool _isUpdatingSelection;
    private bool _hasShownScrollTip;
    private bool _isUnloaded;

    public TrackSelectionControl()
    {
        InitializeComponent();
        Loaded += TrackSelectionControl_Loaded;
        Unloaded += TrackSelectionControl_Unloaded;
    }

    /// <summary>
    /// Динамически обновляет заголовок и описание панели выбора дорожек в зависимости от активного скрипта.
    /// </summary>
    private void UpdateHeaderAndDescription()
    {
        if (TitleTextBlock == null || DescriptionTextBlock == null)
        {
            return;
        }

        if (_activeScript is StreamManagementScript)
        {
            TitleTextBlock.Text = "Управление потоками медиа";
            DescriptionTextBlock.Text = "Отметьте галочками аудио, видео или субтитры, которые вы хотите сохранить или удалить.";
        }
        else
        {
            // По умолчанию (для скрипта "Разборка контейнера" и др.)
            TitleTextBlock.Text = "Выбор дорожек и встроенных шрифтов";
            DescriptionTextBlock.Text = "Отметьте галочками аудио, видео, субтитры или шрифты, которые вы хотите извлечь из контейнеров.";
        }
    }

    /// <summary>
    /// Восстанавливает подписки на события при загрузке элемента управления в визуальное дерево
    /// и запускает перестроение дерева дорожек для отражения актуального состояния файлов.
    /// </summary>
    private void TrackSelectionControl_Loaded(object sender, RoutedEventArgs e)
    {
        _isUnloaded = false;
        UpdateHeaderAndDescription();
        _logService.Info(
            "Загрузка виджета выбора дорожек: " +
            "восстановление зарегистрированных подписок и перестроение дерева",
            "TrackSelectionControl");

        if (_files != null)
        {
            _files.CollectionChanged -= OnFilesCollectionChanged;
            _files.CollectionChanged += OnFilesCollectionChanged;
            ViewModel.Files = _files;
            
            // Восстанавливаем индивидуальные подписки на файлы
            UnsubscribeFromItems();
            SubscribeToItems();

            RebuildTree();
        }
    }

    /// <summary>
    /// Привязать виджет к коллекции импортированных файлов.
    /// Подписывается на события добавления, удаления и фонового анализа файлов.
    /// </summary>
    /// <param name="files">Наблюдаемая коллекция импортированных файлов.</param>
    public void Populate(ObservableCollection<FileQueueItem> files)
    {
        _logService.Info("Инициализация привязки очереди файлов в виджете дорожек", "TrackSelectionControl");
        if (_files != null)
        {
            _files.CollectionChanged -= OnFilesCollectionChanged;
            UnsubscribeFromItems();
        }
        _files = files;
        ViewModel.Files = files;
        if (_files != null)
        {
            _files.CollectionChanged += OnFilesCollectionChanged;
            SubscribeToItems();
        }
        RebuildTree();
    }

    /// <summary>
    /// Собрать результаты выбора дорожек и вложений пользователем.
    /// </summary>
    /// <param name="selectedTracks">Словарь (путь к файлу -> список ID выбранных дорожек).</param>
    /// <param name="selectedAttachments">Словарь (путь к файлу -> список ID выбранных шрифтов).</param>
    public void GetSelectedTracksAndAttachments(
        out Dictionary<string, List<int>> selectedTracks,
        out Dictionary<string, List<int>> selectedAttachments)
    {
        selectedTracks = new Dictionary<string, List<int>>();
        selectedAttachments = new Dictionary<string, List<int>>();

        try
        {
            _logService.Info("Сбор выбранных элементов из дерева TreeView через обход RootNodes", "TrackSelectionControl");

            var selectedNodes = TracksTreeView.SelectedNodes;

            foreach (var fileNode in TracksTreeView.RootNodes)
            {
                foreach (var trackNode in fileNode.Children)
                {
                    if (selectedNodes.Contains(trackNode) && trackNode.Content is TrackNodeItem item)
                    {
                        if (item.TrackId.HasValue)
                        {
                            if (!selectedTracks.TryGetValue(item.FilePath, out var list))
                            {
                                list = new List<int>();
                                selectedTracks[item.FilePath] = list;
                            }
                            if (!list.Contains(item.TrackId.Value))
                            {
                                list.Add(item.TrackId.Value);
                            }
                        }
                        else if (item.AttachmentId.HasValue && item.IsFont)
                        {
                            if (!selectedAttachments.TryGetValue(item.FilePath, out var list))
                            {
                                list = new List<int>();
                                selectedAttachments[item.FilePath] = list;
                            }
                            if (!list.Contains(item.AttachmentId.Value))
                            {
                                list.Add(item.AttachmentId.Value);
                            }
                        }
                    }
                }
            }

            int tracksCount = selectedTracks.Values.Sum(l => l.Count);
            int attachCount = selectedAttachments.Values.Sum(l => l.Count);
            _logService.Info($"Сбор завершен. Выбрано дорожек: {tracksCount}, шрифтов: {attachCount}", "TrackSelectionControl");
        }
        catch (Exception ex)
        {
            _logService.Exception(ex, "Критическая ошибка при сборе выбранных дорожек из дерева TreeView", "TrackSelectionControl");
        }
    }

    /// <summary>
    /// Подписаться на события PropertyChanged элементов очереди файлов.
    /// </summary>
    private void SubscribeToItems()
    {
        if (_files == null) return;

        foreach (var item in _files)
        {
            if (_subscribedItems.Add(item))
            {
                item.PropertyChanged += OnItemPropertyChanged;
            }
        }
    }

    /// <summary>
    /// Отписаться от событий PropertyChanged элементов очереди файлов.
    /// </summary>
    private void UnsubscribeFromItems()
    {
        foreach (var item in _subscribedItems)
        {
            item.PropertyChanged -= OnItemPropertyChanged;
        }
        _subscribedItems.Clear();
    }

    /// <summary>
    /// Обработчик добавления/удаления файлов на первой вкладке.
    /// </summary>
    private void OnFilesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            _logService.DebugLog(
                "Синхронизация списка файлов в дереве выбора дорожек", 
                "TrackSelectionControl");
            
            // Обновляем подписки на элементы
            UnsubscribeFromItems();
            SubscribeToItems();

            RebuildTree();
        });
    }

    /// <summary>
    /// Обработчик изменения свойств файлов (в частности, завершения фонового анализа структуры).
    /// </summary>
    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileQueueItem.MediaInfo))
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                if (sender is FileQueueItem item)
                {
                    _logService.Info(
                        $"Получено уведомление о завершении анализа для " +
                        $"файла: {item.FileName}. Перестраиваем дерево.", 
                        "TrackSelectionControl");
                    RebuildTree();
                }
            });
        }
    }

    /// <summary>
    /// Асинхронно перестраивает дерево дорожек на основе текущего списка файлов.
    /// Гарантирует сохранение пользовательского выбора при асинхронных обновлениях.
    /// </summary>
    private void RebuildTree()
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                // 1. Сохраняем текущий выбор пользователя перед очисткой дерева
                var savedTracks = new Dictionary<string, List<int>>();
                var savedAttachments = new Dictionary<string, List<int>>();

                if (TracksTreeView.RootNodes.Count > 0)
                {
                    var selectedNodes = TracksTreeView.SelectedNodes;
                    foreach (var fileNode in TracksTreeView.RootNodes)
                    {
                        foreach (var trackNode in fileNode.Children)
                        {
                            if (selectedNodes.Contains(trackNode) && 
                                trackNode.Content is TrackNodeItem item)
                            {
                                if (item.TrackId.HasValue)
                                {
                                    if (!savedTracks.TryGetValue(
                                        item.FilePath, 
                                        out var list))
                                    {
                                        list = new List<int>();
                                        savedTracks[item.FilePath] = list;
                                    }
                                    list.Add(item.TrackId.Value);
                                }
                                else if (item.AttachmentId.HasValue && 
                                         item.IsFont)
                                {
                                    if (!savedAttachments.TryGetValue(
                                        item.FilePath, 
                                        out var list))
                                    {
                                        list = new List<int>();
                                        savedAttachments[item.FilePath] = list;
                                    }
                                    list.Add(item.AttachmentId.Value);
                                }
                            }
                        }
                    }
                }
                else if (ActiveScript != null)
                {
                    // Если дерево пустое, восстанавливаем выбор из скрипта
                    foreach (var kvp in ActiveScript.SelectedTrackIds)
                    {
                        savedTracks[kvp.Key] = new List<int>(kvp.Value);
                    }
                    foreach (var kvp in ActiveScript.SelectedAttachmentIds)
                    {
                        savedAttachments[kvp.Key] = new List<int>(kvp.Value);
                    }
                }

                // Принудительно очищаем выбор, дерево и сбрасываем панель фильтров
                TracksTreeView.SelectedNodes.Clear();
                TracksTreeView.RootNodes.Clear();
                FiltersBorder.Visibility = Visibility.Collapsed;

                if (_files == null || _files.Count == 0)
                {
                    // Показываем пустое состояние
                    EmptyStatePanel.Visibility = Visibility.Visible;
                    TracksTreeView.Visibility = Visibility.Collapsed;

                    // Отключаем кольцо прогресса и меняем текст
                    if (EmptyStatePanel.Children.FirstOrDefault(c => c is ProgressRing) is ProgressRing ring)
                    {
                        ring.IsActive = false;
                        ring.Visibility = Visibility.Collapsed;
                    }
                    if (EmptyStatePanel.Children.FirstOrDefault(c => c is TextBlock) is TextBlock textBlock)
                    {
                        textBlock.Text = "Очередь файлов пуста. Пожалуйста, добавьте медиафайлы на вкладке «Файлы».";
                    }

                    _logService.Info("Дерево дорожек очищено, так как очередь файлов пуста", "TrackSelectionControl");
                    return;
                }

                // Включаем отображение загрузки, если идет анализ
                bool isAnyAnalyzing = _files.Any(f => f.MediaInfo == null);
                EmptyStatePanel.Visibility = isAnyAnalyzing ? Visibility.Visible : Visibility.Collapsed;
                
                if (isAnyAnalyzing)
                {
                    if (EmptyStatePanel.Children.FirstOrDefault(c => c is ProgressRing) is ProgressRing ring)
                    {
                        ring.IsActive = true;
                        ring.Visibility = Visibility.Visible;
                    }
                    if (EmptyStatePanel.Children.FirstOrDefault(c => c is TextBlock) is TextBlock textBlock)
                    {
                        textBlock.Text = "Выполняется фоновый анализ технических свойств медиафайлов...";
                    }
                    return;
                }

                TracksTreeView.Visibility = Visibility.Visible;
                var nodesToSelect = new List<TreeViewNode>();

                foreach (var fileItem in _files)
                {
                    // 1. Создаем корневой узел для файла
                    var fileNodeItem = new TrackNodeItem
                    {
                        Text = $"{fileItem.FileName} ({fileItem.FileSizeStr})",
                        IconGlyph = "\uE7C3", // Иконка документа/видеоролика
                        FilePath = fileItem.FilePath,
                        IsFile = true
                    };

                    var fileNode = new TreeViewNode 
                    { 
                        Content = fileNodeItem,
                        IsExpanded = true 
                    };

                    var structure = fileItem.MediaInfo!;
                    
                    // Определяем, есть ли уже сохраненный выбор для ДАННОГО конкретного файла
                    bool hasSavedForThisFile = savedTracks.ContainsKey(fileItem.FilePath) || savedAttachments.ContainsKey(fileItem.FilePath);

                    // 2. Видеодорожки
                    var videoTracks = structure.GetVideoTracks();
                    foreach (var track in videoTracks)
                    {
                        var trackItem = new TrackNodeItem
                        {
                            Text = $"Видео #{track.TrackId} • {track.Codec.ToUpperInvariant()} • {track.Resolution}{(!string.IsNullOrEmpty(track.Name) ? $" • \"{track.Name}\"" : "")}",
                            IconGlyph = "\uE714", // Иконка видеопленки
                            FilePath = fileItem.FilePath,
                            TrackId = track.TrackId,
                            Track = track
                        };
                        var trackNode = new TreeViewNode { Content = trackItem };
                        fileNode.Children.Add(trackNode);
                        
                        // Выбираем только если узел был выбран пользователем ранее
                        if (savedTracks.TryGetValue(
                            fileItem.FilePath,
                            out var list) &&
                            list.Contains(track.TrackId))
                        {
                            nodesToSelect.Add(trackNode);
                        }
                    }

                    // 3. Аудиодорожки
                    var audioTracks = structure.GetAudioTracks();
                    foreach (var track in audioTracks)
                    {
                        string langStr = !string.IsNullOrEmpty(track.Language) && track.Language != "und" ? track.Language.ToUpperInvariant() : "UND";
                        var trackItem = new TrackNodeItem
                        {
                            Text = $"Аудио #{track.TrackId} • {track.Codec.ToUpperInvariant()} • {langStr} • {track.Channels} ch{(!string.IsNullOrEmpty(track.Name) ? $" • \"{track.Name}\"" : "")}",
                            IconGlyph = "\uE7F6", // Иконка динамика/аудио
                            FilePath = fileItem.FilePath,
                            TrackId = track.TrackId,
                            Track = track
                        };
                        var trackNode = new TreeViewNode { Content = trackItem };
                        fileNode.Children.Add(trackNode);
                        
                        if (savedTracks.TryGetValue(
                            fileItem.FilePath,
                            out var list) &&
                            list.Contains(track.TrackId))
                        {
                            nodesToSelect.Add(trackNode);
                        }
                    }

                    // 4. Дорожки субтитров
                    var subtitleTracks = structure.GetSubtitleTracks();
                    foreach (var track in subtitleTracks)
                    {
                        string langStr = !string.IsNullOrEmpty(track.Language) && track.Language != "und" ? track.Language.ToUpperInvariant() : "UND";
                        var trackItem = new TrackNodeItem
                        {
                            Text = $"Субтитры #{track.TrackId} • {track.Codec.ToUpperInvariant()} • {langStr}{(!string.IsNullOrEmpty(track.Name) ? $" • \"{track.Name}\"" : "")}",
                            IconGlyph = "\uE8D2", // Иконка субтитров/текста
                            FilePath = fileItem.FilePath,
                            TrackId = track.TrackId,
                            Track = track
                        };
                        var trackNode = new TreeViewNode { Content = trackItem };
                        fileNode.Children.Add(trackNode);
                        
                        if (savedTracks.TryGetValue(
                            fileItem.FilePath,
                            out var list) &&
                            list.Contains(track.TrackId))
                        {
                            nodesToSelect.Add(trackNode);
                        }
                    }

                    // 5. Встроенные вложения (шрифты)
                    var fonts = structure.GetFontAttachments();
                    foreach (var font in fonts)
                    {
                        var trackItem = new TrackNodeItem
                        {
                            Text = $"Шрифт • {font.FileName} • {font.Size / 1024.0:F1} КБ",
                            IconGlyph = "\uE723", // Иконка шрифта/символа A
                            FilePath = fileItem.FilePath,
                            AttachmentId = font.AttachmentId,
                            IsFont = true,
                            Attachment = font
                        };
                        var trackNode = new TreeViewNode { Content = trackItem };
                        fileNode.Children.Add(trackNode);
                        
                        if (savedAttachments.TryGetValue(
                            fileItem.FilePath,
                            out var list) &&
                            list.Contains(font.AttachmentId))
                        {
                            nodesToSelect.Add(trackNode);
                        }
                    }

                    if (videoTracks.Count == 0 && audioTracks.Count == 0 && subtitleTracks.Count == 0 && fonts.Count == 0)
                    {
                        var emptyNode = new TreeViewNode
                        {
                            Content = new TrackNodeItem
                            {
                                Text = "Дорожки и шрифты не обнаружены",
                                IconGlyph = "\uE7BA", // Иконка предупреждения
                                FilePath = fileItem.FilePath
                            }
                        };
                        fileNode.Children.Add(emptyNode);
                    }

                    TracksTreeView.RootNodes.Add(fileNode);
                }

                // Выполняем автовыбор узлов по умолчанию
                _isUpdatingSelection = true;
                foreach (var node in nodesToSelect)
                {
                    TracksTreeView.SelectedNodes.Add(node);
                }
                _isUpdatingSelection = false;

                // Собираем динамические опции для фильтрации и строим UI фильтров
                ViewModel.CollectDynamicOptions();

                // Показываем панель фильтров
                FiltersBorder.Visibility = Visibility.Visible;

                // Устанавливаем активную вкладку по умолчанию
                if (FilterNavigationView.SelectedItem == null)
                {
                    FilterNavigationView.SelectedItem = VideoFilterTab;
                }
                else
                {
                    string? currentCategory = GetCurrentCategory();
                    if (currentCategory != null)
                    {
                        UpdateFiltersPanel(currentCategory);
                        UpdateButtonLabels(currentCategory);
                    }
                }

                UpdateSelectAllCheckBoxState();
                UpdateTabCounts();

                _logService.Info($"Дерево дорожек успешно перестроено для {_files.Count} файлов (Выбрано по умолчанию/восстановлено: {nodesToSelect.Count})", "TrackSelectionControl");
            }
            catch (Exception ex)
            {
                _logService.Exception(ex, "Критическая ошибка во время перестроения дерева дорожек в UI", "TrackSelectionControl");
            }
        });
    }



    /// <summary>
    /// Обновляет видимость кнопок фильтров и заполняет их Flyouts
    /// уникальными параметрами для выбранной категории.
    /// </summary>
    /// <param name="category">Текущая активная категория.</param>
    private void UpdateFiltersPanel(string category)
    {
        try
        {
            _logService.Info(
                $"Обновление панели фильтров для категории: {category}",
                "TrackSelectionControl");

            // Для аудио/видео настраиваем отображение специальной кнопки деталей
            if (category == "video")
            {
                DetailFilterText.Text = "Разрешение ▼";
            }
            else if (category == "audio")
            {
                DetailFilterText.Text = "Каналы ▼";
            }
            else if (category == "attachments")
            {
                DetailFilterText.Text = "Расширение ▼";
                NameFilterText.Text = "Имя файла ▼";
            }
            else
            {
                NameFilterText.Text = "Название ▼";
            }

            // Наполняем группы фильтров
            if (category == "attachments")
            {
                LanguageFilterButton.Visibility = Visibility.Collapsed;
                CodecFilterButton.Visibility = Visibility.Collapsed;

                PopulateFilterGroup(
                    category,
                    "extension",
                    DetailFilterContainer,
                    DetailFilterButton);

                PopulateFilterGroup(
                    category,
                    "name",
                    NameFilterContainer,
                    NameFilterButton);
            }
            else
            {
                PopulateFilterGroup(
                    category,
                    "language",
                    LanguageFilterContainer,
                    LanguageFilterButton);

                PopulateFilterGroup(
                    category,
                    "codec",
                    CodecFilterContainer,
                    CodecFilterButton);

                if (category == "subtitles")
                {
                    DetailFilterButton.Visibility = Visibility.Collapsed;
                }
                else
                {
                    string propKey = category == "video"
                        ? "resolution"
                        : "channels";

                    PopulateFilterGroup(
                        category,
                        propKey,
                        DetailFilterContainer,
                        DetailFilterButton);
                }

                PopulateFilterGroup(
                    category,
                    "name",
                    NameFilterContainer,
                    NameFilterButton);
            }
        }
        catch (Exception ex)
        {
            _logService.Exception(
                ex,
                "Исключение при обновлении панели фильтров",
                "TrackSelectionControl");
        }
    }

    /// <summary>
    /// Заполняет контейнер Flyout-а чекбоксами.
    /// Если уникальных параметров нет, кнопка-фильтр скрывается.
    /// </summary>
    /// <param name="category">Категория дорожек.</param>
    /// <param name="propKey">Идентификатор свойства.</param>
    /// <param name="container">Контейнер StackPanel внутри Flyout.</param>
    /// <param name="parentButton">Родительская кнопка фильтра.</param>
    private void PopulateFilterGroup(
        string category,
        string propKey,
        StackPanel container,
        Button parentButton)
    {
        container.Children.Clear();

        if (!ViewModel.DynamicOptions.TryGetValue(category, out var catDict) ||
            !catDict.TryGetValue(propKey, out var values) ||
            values.Count == 0)
        {
            parentButton.Visibility = Visibility.Collapsed;
            _logService.DebugLog(
                $"Свойства {propKey} для категории {category} " +
                "отсутствуют. Кнопка скрыта.",
                "TrackSelectionControl");
            return;
        }

        parentButton.Visibility = Visibility.Visible;
        var sortedValues = values.OrderBy(v => v).ToList();

        foreach (var val in sortedValues)
        {
            var cb = new CheckBox
            {
                Content = val,
                Margin = new Thickness(4, 2, 4, 2),
                HorizontalAlignment = HorizontalAlignment.Left
            };

            // Восстанавливаем состояние выбора из активных правил
            if (ViewModel.ActiveRules.TryGetValue(category, out var activeCat) &&
                activeCat.TryGetValue(propKey, out var activeVals) &&
                activeVals.Contains(val))
            {
                cb.IsChecked = true;
            }

            cb.Checked += (s, e) =>
                OnRuleCheckboxChanged(category, propKey, val, true);
            cb.Unchecked += (s, e) =>
                OnRuleCheckboxChanged(category, propKey, val, false);

            container.Children.Add(cb);
        }

        _logService.DebugLog(
            $"Заполнена группа фильтров [{category}] {propKey}. " +
            $"Добавлено чекбоксов: {sortedValues.Count}",
            "TrackSelectionControl");
    }

    /// <summary>
    /// Обновляет текстовые метки кнопок, отображая выбранные правила.
    /// </summary>
    /// <param name="category">Текущая активная категория.</param>
    private void UpdateButtonLabels(string category)
    {
        try
        {
            if (!ViewModel.ActiveRules.TryGetValue(category, out var rules)) return;

            // 1. Фильтр по языку
            if (rules.TryGetValue("language", out var langRules) &&
                langRules.Count > 0)
            {
                // Фиксируем текст кнопки и отображаем количество выбранных языков в бейдже
                LanguageFilterText.Text = "Язык ▼";
                LanguageInfoBadge.Value = langRules.Count;
                LanguageInfoBadge.Visibility = Visibility.Visible;
                _logService.DebugLog($"Включен бейдж языка: {langRules.Count} правил", "TrackSelectionControl");
            }
            else
            {
                // Скрываем бейдж, так как правила фильтрации по языку не выбраны
                LanguageFilterText.Text = "Язык ▼";
                LanguageInfoBadge.Visibility = Visibility.Collapsed;
            }

            // 2. Фильтр по кодеку
            if (rules.TryGetValue("codec", out var codecRules) &&
                codecRules.Count > 0)
            {
                // Фиксируем текст кнопки и отображаем количество выбранных кодеков в бейдже
                CodecFilterText.Text = "Кодек ▼";
                CodecInfoBadge.Value = codecRules.Count;
                CodecInfoBadge.Visibility = Visibility.Visible;
                _logService.DebugLog($"Включен бейдж кодека: {codecRules.Count} правил", "TrackSelectionControl");
            }
            else
            {
                // Скрываем бейдж, так как правила фильтрации по кодекам не выбраны
                CodecFilterText.Text = "Кодек ▼";
                CodecInfoBadge.Visibility = Visibility.Collapsed;
            }

            // 3. Детали (Разрешение, Каналы или Расширение)
            string detailLabelBase = category switch
            {
                "video" => "Разрешение",
                "audio" => "Каналы",
                "attachments" => "Расширение",
                _ => "Детали"
            };

            string detailPropKey = category switch
            {
                "video" => "resolution",
                "audio" => "channels",
                "attachments" => "extension",
                _ => "detail"
            };

            if (rules.TryGetValue(detailPropKey, out var detailRules) &&
                detailRules.Count > 0)
            {
                // Устанавливаем базовое название параметра и отображаем количество выбранных деталей в бейдже
                DetailFilterText.Text = $"{detailLabelBase} ▼";
                DetailInfoBadge.Value = detailRules.Count;
                DetailInfoBadge.Visibility = Visibility.Visible;
                _logService.DebugLog($"Включен бейдж деталей ({detailLabelBase}): {detailRules.Count} правил", "TrackSelectionControl");
            }
            else
            {
                // Скрываем бейдж, так как правила фильтрации деталей не выбраны
                DetailFilterText.Text = $"{detailLabelBase} ▼";
                DetailInfoBadge.Visibility = Visibility.Collapsed;
            }

            // 4. Название (Имя файла)
            string nameLabelBase = category == "attachments"
                ? "Имя файла"
                : "Название";

            if (rules.TryGetValue("name", out var nameRules) &&
                nameRules.Count > 0)
            {
                // Устанавливаем базовое название и отображаем количество выбранных названий в бейдже
                NameFilterText.Text = $"{nameLabelBase} ▼";
                NameInfoBadge.Value = nameRules.Count;
                NameInfoBadge.Visibility = Visibility.Visible;
                _logService.DebugLog($"Включен бейдж названия ({nameLabelBase}): {nameRules.Count} правил", "TrackSelectionControl");
            }
            else
            {
                // Скрываем бейдж, так как правила фильтрации названий не выбраны
                NameFilterText.Text = $"{nameLabelBase} ▼";
                NameInfoBadge.Visibility = Visibility.Collapsed;
            }

            _logService.DebugLog(
                $"Обновлены текстовые метки кнопок и бейджи для категории: {category}",
                "TrackSelectionControl");
        }
        catch (Exception ex)
        {
            _logService.Exception(
                ex,
                "Ошибка при обновлении меток кнопок фильтров и бейджей",
                "TrackSelectionControl");
        }
    }

    /// <summary>
    /// Обработчик клика по чекбоксу конкретного правила фильтрации.
    /// </summary>
    private void OnRuleCheckboxChanged(
        string category,
        string propKey,
        string value,
        bool isChecked)
    {
        if (_isResettingFilters) return;

        try
        {
            if (ViewModel.ActiveRules.TryGetValue(category, out var catDict) &&
                catDict.TryGetValue(propKey, out var activeVals))
            {
                if (isChecked)
                {
                    activeVals.Add(value);
                }
                else
                {
                    activeVals.Remove(value);
                }
            }

            _logService.Info(
                $"Изменено правило фильтрации [{category}] {propKey}: " +
                $"{value} -> {isChecked}",
                "TrackSelectionControl");

            // Сбрасываем чекбокс "Выбрать все" для текущей категории
            _isUpdatingSelectAll = true;
            SelectAllCheckBox.IsChecked = false;
            _isUpdatingSelectAll = false;

            ApplyRules(category);
            UpdateButtonLabels(category);
        }
        catch (Exception ex)
        {
            _logService.Exception(
                ex,
                "Ошибка при обработке изменения чекбокса правила",
                "TrackSelectionControl");
        }
    }

    /// <summary>
    /// Снимает выделение со всех чекбоксов во всплывающих меню (Flyouts).
    /// </summary>
    /// <param name="category">Категория дорожек.</param>
    private void ResetCategoryFilterCheckboxes(string category)
    {
        _isResettingFilters = true;
        try
        {
            _logService.Info(
                $"Сброс чекбоксов в Flyout-фильтрах для категории: {category}",
                "TrackSelectionControl");

            var containers = new List<StackPanel>
            {
                LanguageFilterContainer,
                CodecFilterContainer,
                DetailFilterContainer,
                NameFilterContainer
            };

            foreach (var container in containers)
            {
                if (container == null) continue;

                foreach (var cb in container.Children.OfType<CheckBox>())
                {
                    cb.IsChecked = false;
                }
            }
        }
        catch (Exception ex)
        {
            _logService.Exception(
                ex,
                "Ошибка при сбросе чекбоксов фильтров",
                "TrackSelectionControl");
        }
        finally
        {
            _isResettingFilters = false;
        }
    }

    /// <summary>
    /// Применяет все активные правила фильтрации для выбранной категории к дереву дорожек.
    /// </summary>
    private void ApplyRules(string? targetCategory = null)
    {
        try
        {
            _logService.Info($"Применение правил фильтрации для категории: {targetCategory ?? "все"}", "TrackSelectionControl");

            // Копируем текущий набор выбранных узлов
            var selectedNodes = new HashSet<TreeViewNode>(TracksTreeView.SelectedNodes);

            foreach (var fileNode in TracksTreeView.RootNodes)
            {
                foreach (var trackNode in fileNode.Children)
                {
                    if (trackNode.Content is not TrackNodeItem item || item.IsFile)
                    {
                        continue;
                    }

                    string category = string.Empty;
                    if (item.IsFont)
                    {
                        category = "attachments";
                    }
                    else if (item.Track != null)
                    {
                        category = item.Track.TrackType.ToLowerInvariant();
                    }

                    // Обновляем только указанную категорию, если задано
                    if (targetCategory != null && category != targetCategory)
                    {
                        continue;
                    }

                    bool matches = ViewModel.MatchesFilterRules(item);

                    if (matches)
                    {
                        selectedNodes.Add(trackNode);
                    }
                    else
                    {
                        selectedNodes.Remove(trackNode);
                    }
                }
            }

            // Корректируем состояние родительских узлов в selectedNodes перед записью в дерево
            foreach (var fileNode in TracksTreeView.RootNodes)
            {
                int totalChildren = fileNode.Children.Count;
                int selectedChildren = fileNode.Children.Count(
                    c => selectedNodes.Contains(c));

                if (totalChildren > 0)
                {
                    if (selectedChildren == totalChildren)
                    {
                        selectedNodes.Add(fileNode);
                    }
                    else
                    {
                        selectedNodes.Remove(fileNode);
                    }
                }
            }

            _isUpdatingSelection = true;
            TracksTreeView.SelectedNodes.Clear();
            foreach (var node in selectedNodes)
            {
                TracksTreeView.SelectedNodes.Add(node);
            }
            _isUpdatingSelection = false;

            // Явно сохраняем измененное состояние выбора в ActiveScript и рассылаем сообщение
            SyncActiveScriptSelectionAndNotify();

            UpdateSelectAllCheckBoxState();
            UpdateTabCounts();
        }
        catch (Exception ex)
        {
            _logService.Exception(ex, "Ошибка во время применения правил фильтрации к дереву дорожек", "TrackSelectionControl");
        }
    }



    /// <summary>
    /// Возвращает текстовый идентификатор текущей активной вкладки категорий.
    /// </summary>
    private string? GetCurrentCategory()
    {
        if (FilterNavigationView.SelectedItem is NavigationViewItem selectedItem &&
            selectedItem.Tag is string tag)
        {
            return tag;
        }
        return null;
    }

    /// <summary>
    /// Обновляет состояние чекбокса "Выбрать все" на основе текущего выделения.
    /// </summary>
    private void UpdateSelectAllCheckBoxState()
    {
        try
        {
            string? currentCategory = GetCurrentCategory();
            if (currentCategory == null) return;

            int total = 0;
            int selected = 0;

            var selectedNodes = TracksTreeView.SelectedNodes;

            foreach (var fileNode in TracksTreeView.RootNodes)
            {
                foreach (var trackNode in fileNode.Children)
                {
                    if (trackNode.Content is TrackNodeItem item && !item.IsFile)
                    {
                        string category = string.Empty;
                        if (item.IsFont)
                        {
                            category = "attachments";
                        }
                        else if (item.Track != null)
                        {
                            category = item.Track.TrackType.ToLowerInvariant();
                        }

                        if (category == currentCategory)
                        {
                            total++;
                            if (selectedNodes.Contains(trackNode))
                            {
                                selected++;
                            }
                        }
                    }
                }
            }

            _isUpdatingSelectAll = true;
            if (total > 0 && selected == total)
            {
                SelectAllCheckBox.IsChecked = true;
            }
            else if (selected > 0 && selected < total)
            {
                SelectAllCheckBox.IsChecked = null; // Частично выбран
            }
            else
            {
                SelectAllCheckBox.IsChecked = false;
            }
            _isUpdatingSelectAll = false;
        }
        catch (Exception ex)
        {
            _logService.Exception(ex, "Ошибка при обновлении состояния чекбокса Выбрать все", "TrackSelectionControl");
        }
    }

    /// <summary>
    /// Обновляет текстовые названия вкладок с отображением количества выделенных элементов.
    /// </summary>
    private void UpdateTabCounts()
    {
        try
        {
            int videoCount = 0;
            int audioCount = 0;
            int subtitleCount = 0;
            int attachmentCount = 0;

            var selectedNodes = TracksTreeView.SelectedNodes;

            foreach (var fileNode in TracksTreeView.RootNodes)
            {
                foreach (var trackNode in fileNode.Children)
                {
                    if (selectedNodes.Contains(trackNode) && trackNode.Content is TrackNodeItem item && !item.IsFile)
                    {
                        if (item.IsFont)
                        {
                            attachmentCount++;
                        }
                        else if (item.Track != null)
                        {
                            string category = item.Track.TrackType.ToLowerInvariant();
                            if (category == "video") videoCount++;
                            else if (category == "audio") audioCount++;
                            else if (category == "subtitles") subtitleCount++;
                        }
                    }
                }
            }

            // Обновление бейджей для NavigationViewItem
            VideoInfoBadge.Value = videoCount;
            VideoInfoBadge.Visibility = videoCount > 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            AudioInfoBadge.Value = audioCount;
            AudioInfoBadge.Visibility = audioCount > 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            SubtitleInfoBadge.Value = subtitleCount;
            SubtitleInfoBadge.Visibility = subtitleCount > 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            AttachmentInfoBadge.Value = attachmentCount;
            AttachmentInfoBadge.Visibility = attachmentCount > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            _logService.Exception(ex, "Ошибка при обновлении счетчиков вкладок фильтрации", "TrackSelectionControl");
        }
    }

    /// <summary>
    /// Обработчик переключения вкладок фильтрации.
    /// </summary>
    private void FilterNavigationView_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        try
        {
            if (args.SelectedItemContainer is NavigationViewItem selectedItem &&
                selectedItem.Tag is string tag)
            {
                _logService.DebugLog(
                    $"Вкладка фильтра изменена на: {tag}",
                    "TrackSelectionControl");

                UpdateFiltersPanel(tag);
                UpdateButtonLabels(tag);
                UpdateSelectAllCheckBoxState();
            }
        }
        catch (Exception ex)
        {
            _logService.Exception(
                ex,
                "Ошибка при смене вкладки фильтрации",
                "TrackSelectionControl");
        }
    }

    /// <summary>
    /// Обработчик изменения выбора в дереве TreeView (для синхронизации чекбокса "Выбрать все" и счетчиков).
    /// </summary>
    private void TracksTreeView_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (_isUpdatingSelection || _isUnloaded || !IsLoaded) return;

        _isUpdatingSelection = true;
        try
        {
            // Каскадное выделение детей при выборе родителя
            foreach (var node in args.AddedItems.Cast<TreeViewNode>())
            {
                if (node.Content is TrackNodeItem item && item.IsFile)
                {
                    foreach (var child in node.Children)
                    {
                        if (!sender.SelectedNodes.Contains(child))
                        {
                            sender.SelectedNodes.Add(child);
                        }
                    }
                }
            }

            // Каскадное снятие выделения при снятии с родителя
            foreach (var node in args.RemovedItems.Cast<TreeViewNode>())
            {
                if (node.Content is TrackNodeItem item && item.IsFile)
                {
                    int totalChildren = node.Children.Count;
                    int selectedChildren = node.Children.Count(
                        c => sender.SelectedNodes.Contains(c));

                    // Снимаем выбор с детей только если родитель был снят
                    // вручную (т.е. на момент снятия все его дети выделены)
                    if (selectedChildren == totalChildren)
                    {
                        foreach (var child in node.Children)
                        {
                            if (sender.SelectedNodes.Contains(child))
                            {
                                sender.SelectedNodes.Remove(child);
                            }
                        }
                    }
                }
            }

            // Корректировка выбора родителя на основе детей
            foreach (var fileNode in sender.RootNodes)
            {
                int totalChildren = fileNode.Children.Count;
                int selectedChildren = fileNode.Children.Count(c => sender.SelectedNodes.Contains(c));

                if (totalChildren > 0)
                {
                    if (selectedChildren == totalChildren)
                    {
                        if (!sender.SelectedNodes.Contains(fileNode))
                        {
                            sender.SelectedNodes.Add(fileNode);
                        }
                    }
                    else
                    {
                        if (sender.SelectedNodes.Contains(fileNode))
                        {
                            sender.SelectedNodes.Remove(fileNode);
                        }
                    }
                }
            }

            UpdateSelectAllCheckBoxState();
            UpdateTabCounts();

            SyncActiveScriptSelectionAndNotify();
        }
        catch (Exception ex)
        {
            _logService.Exception(
                ex, 
                "Ошибка при синхронизации изменения выделения в дереве", 
                "TrackSelectionControl");
        }
        finally
        {
            _isUpdatingSelection = false;
        }
    }

    /// <summary>
    /// Синхронизирует текущее выделение дерева дорожек с ActiveScript и рассылает сообщение об изменении выбора.
    /// </summary>
    private void SyncActiveScriptSelectionAndNotify()
    {
        try
        {
            if (ActiveScript != null)
            {
                _logService.Info("Запуск синхронизации выделенных в UI дорожек с моделью скрипта", "TrackSelectionControl");
                GetSelectedTracksAndAttachments(
                    out var currentTracks, 
                    out var currentAttachments);

                ActiveScript.SelectedTrackIds.Clear();
                foreach (var kvp in currentTracks)
                {
                    ActiveScript.SelectedTrackIds[kvp.Key] = kvp.Value;
                }

                ActiveScript.SelectedAttachmentIds.Clear();
                foreach (var kvp in currentAttachments)
                {
                    ActiveScript.SelectedAttachmentIds[kvp.Key] = kvp.Value;
                }

                _logService.DebugLog(
                    $"Синхронизировано дорожек: {currentTracks.Values.Sum(v => v.Count)}, вложений: {currentAttachments.Values.Sum(v => v.Count)}. Рассылка сообщения TrackSelectedMessage.",
                    "TrackSelectionControl");

                CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(
                    new KTools_App.ViewModels.Messages.TrackSelectedMessage(currentTracks, currentAttachments));
            }
        }
        catch (Exception ex)
        {
            _logService.Exception(
                ex,
                "Ошибка во время синхронизации выделения и рассылки сообщения",
                "TrackSelectionControl");
        }
    }

    /// <summary>
    /// Обработчик клика по чекбоксу "Выбрать все".
    /// </summary>
    private void SelectAllCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingSelectAll) return;

        try
        {
            string? currentCategory = GetCurrentCategory();
            if (currentCategory == null) return;

            bool isChecked = SelectAllCheckBox.IsChecked == true;
            _logService.Info(
                $"Клик по чекбоксу Выбрать все [{currentCategory}]: " +
                $"{isChecked}",
                "TrackSelectionControl");

            ViewModel.ClearRules(currentCategory);

            ResetCategoryFilterCheckboxes(currentCategory);

            var selectedNodes = new HashSet<TreeViewNode>(TracksTreeView.SelectedNodes);

            foreach (var fileNode in TracksTreeView.RootNodes)
            {
                foreach (var trackNode in fileNode.Children)
                {
                    if (trackNode.Content is TrackNodeItem item && !item.IsFile)
                    {
                        string category = string.Empty;
                        if (item.IsFont)
                        {
                            category = "attachments";
                        }
                        else if (item.Track != null)
                        {
                            category = item.Track.TrackType.ToLowerInvariant();
                        }

                        if (category == currentCategory)
                        {
                            if (isChecked)
                            {
                                selectedNodes.Add(trackNode);
                            }
                            else
                            {
                                selectedNodes.Remove(trackNode);
                            }
                        }
                    }
                }
            }

            // Корректируем состояние родительских узлов в selectedNodes перед записью в дерево
            foreach (var fileNode in TracksTreeView.RootNodes)
            {
                int totalChildren = fileNode.Children.Count;
                int selectedChildren = fileNode.Children.Count(
                    c => selectedNodes.Contains(c));

                if (totalChildren > 0)
                {
                    if (selectedChildren == totalChildren)
                    {
                        selectedNodes.Add(fileNode);
                    }
                    else
                    {
                        selectedNodes.Remove(fileNode);
                    }
                }
            }

            _isUpdatingSelection = true;
            TracksTreeView.SelectedNodes.Clear();
            foreach (var node in selectedNodes)
            {
                TracksTreeView.SelectedNodes.Add(node);
            }
            _isUpdatingSelection = false;

            SyncActiveScriptSelectionAndNotify();

            UpdateSelectAllCheckBoxState();
            UpdateTabCounts();
        }
        catch (Exception ex)
        {
            _logService.Exception(
                ex,
                "Ошибка при клике по чекбоксу Выбрать все",
                "TrackSelectionControl");
        }
    }

    /// <summary>
    /// Обработчик изменения размера панели кнопок-фильтров.
    /// </summary>
    private void FilterButtonsStackPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        CheckForFilterOverflow();
    }

    /// <summary>
    /// Обработчик изменения размера скроллера кнопок-фильтров.
    /// </summary>
    private void FilterScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        CheckForFilterOverflow();
    }

    /// <summary>
    /// Проверяет наличие переполнения панели фильтров и при необходимости показывает подсказку.
    /// </summary>
    private void CheckForFilterOverflow()
    {
        try
        {
            if (_hasShownScrollTip) return;

            // Если панель видима и ширина кнопок превышает видимую ширину скроллера
            if (FiltersBorder.Visibility == Visibility.Visible &&
                FilterButtonsStackPanel.ActualWidth > FilterScrollViewer.ActualWidth &&
                FilterScrollViewer.ActualWidth > 0)
            {
                _hasShownScrollTip = true;
                FilterScrollTeachingTip.IsOpen = true;
                _logService.Info(
                    "Отображена подсказка о горизонтальном скроллинге фильтров",
                    "TrackSelectionControl");
            }
        }
        catch (Exception ex)
        {
            _logService.Exception(
                ex,
                "Ошибка при проверке переполнения панели фильтров для подсказки",
                "TrackSelectionControl");
        }
    }

    /// <summary>
    /// Освобождает ресурсы при выгрузке элемента управления из дерева.
    /// </summary>
    private void TrackSelectionControl_Unloaded(
        object sender, 
        RoutedEventArgs e)
    {
        _isUnloaded = true;
        _logService.Info(
            "Выгрузка виджета выбора дорожек: " +
            "освобождение зарегистрированных подписок",
            "TrackSelectionControl");

        if (_files != null)
        {
            _files.CollectionChanged -= OnFilesCollectionChanged;
        }
        UnsubscribeFromItems();
    }
}
