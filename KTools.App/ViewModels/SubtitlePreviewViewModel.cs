// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using KTools_App.Core;
using KTools_App.Infrastructure;
using KTools_App.Models;

namespace KTools_App.ViewModels;

/// <summary>
/// Представляет один элемент списка фильтрации (чекбокс) в боковом меню.
/// </summary>
public sealed class FilterItemViewModel : ObservableObject
{
    private bool _isChecked;

    /// <summary>
    /// Имя элемента (например, имя актера или название стиля).
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Состояние выбора (включен ли данный элемент в фильтр).
    /// </summary>
    public bool IsChecked
    {
        get => _isChecked;
        set => SetProperty(ref _isChecked, value);
    }

    /// <summary>
    /// Инициализирует новый экземпляр FilterItemViewModel.
    /// </summary>
    public FilterItemViewModel(string name, bool isChecked)
    {
        Name = name;
        _isChecked = isChecked;
    }
}

/// <summary>
/// Представляет модель строки субтитров для отображения в списке предпросмотра.
/// </summary>
public sealed class SubtitlePreviewLine : ObservableObject
{
    private readonly SubtitleFilterState _filterState;
    private bool _isChecked;
    private string _status = "ОК";
    private string _statusColor = "#22b473";
    private bool _isTextStrikethrough;
    private string _finalText = string.Empty;
    private string? _textWithoutCaps;
    private string? _textWithoutBoth;
    private bool _isFirstLineInFile;

    /// <summary>
    /// Глобальный индекс строки во всем списке субтитров.
    /// </summary>
    public int GlobalIndex { get; set; }

    /// <summary>
    /// Указывает, является ли эта строка первой отображаемой строкой для своего файла.
    /// </summary>
    public bool IsFirstLineInFile
    {
        get => _isFirstLineInFile;
        set => SetProperty(ref _isFirstLineInFile, value);
    }

    /// <summary>
    /// Индекс строки диалога в исходном файле (0-based).
    /// </summary>
    public int Index { get; }

    /// <summary>
    /// Время начала реплики.
    /// </summary>
    public string Start { get; }

    /// <summary>
    /// Время окончания реплики.
    /// </summary>
    public string End { get; }

    /// <summary>
    /// Название стиля реплики.
    /// </summary>
    public string Style { get; }

    /// <summary>
    /// Имя актера реплики.
    /// </summary>
    public string Actor { get; }

    /// <summary>
    /// Спецэффект реплики.
    /// </summary>
    public string Effect { get; }

    /// <summary>
    /// Абсолютный путь к исходному файлу субтитров.
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    /// Имя исходного файла субтитров.
    /// </summary>
    public string FileName { get; }

    /// <summary>
    /// Оригинальный текст реплики (со всеми тегами).
    /// </summary>
    public string OriginalText { get; }

    /// <summary>
    /// Очищенный от тегов текст реплики.
    /// </summary>
    public string CleanText { get; }

    /// <summary>
    /// Указывает, была ли реплика изначально пустой.
    /// </summary>
    public bool IsOriginallyEmpty { get; }

    /// <summary>
    /// Указывает, пуста ли реплика после применения фильтров очистки (капс, теги).
    /// </summary>
    public bool IsEmptyAfterFilters { get; set; }

    /// <summary>
    /// Конструктор модели строки предпросмотра субтитров.
    /// </summary>
    public SubtitlePreviewLine(
        int index,
        AssDialogue dialogue,
        string filePath,
        SubtitleFilterState filterState)
    {
        Index = index;
        Start = dialogue.Start;
        End = dialogue.End;
        Style = dialogue.Style ?? string.Empty;
        Actor = dialogue.Actor ?? string.Empty;
        Effect = dialogue.Effect ?? string.Empty;
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        OriginalText = dialogue.Text ?? string.Empty;
        _filterState = filterState;

        CleanText = AssParser.Instance.StripTags(OriginalText);
        IsOriginallyEmpty = string.IsNullOrWhiteSpace(CleanText);

        UpdateState(false);
    }

    /// <summary>
    /// Статус включения строки в результирующий экспорт (активный чекбокс).
    /// </summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value))
            {
                OnUserCheckChanged(value);
            }
        }
    }

    /// <summary>
    /// Текстовый статус реплики для вывода.
    /// </summary>
    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    /// <summary>
    /// Цвет статуса реплики в формате Hex.
    /// </summary>
    public string StatusColor
    {
        get => _statusColor;
        set => SetProperty(ref _statusColor, value);
    }

    /// <summary>
    /// Флаг зачеркивания текста реплики (если строка удалена).
    /// </summary>
    public bool IsTextStrikethrough
    {
        get => _isTextStrikethrough;
        set => SetProperty(ref _isTextStrikethrough, value);
    }

    /// <summary>
    /// Финальный отформатированный текст реплики (без тегов, с переносами строк).
    /// </summary>
    public string FinalText
    {
        get => _finalText;
        set => SetProperty(ref _finalText, value);
    }

    /// <summary>
    /// Объединенная строка Актёра и Стиля.
    /// </summary>
    public string ActorAndStyle =>
        string.IsNullOrEmpty(Actor) ? Style : $"{Actor} / {Style}";

    /// <summary>
    /// Отформатированный интервал времени реплики.
    /// </summary>
    public string Timing => $"{Start} --> {End}";

    /// <summary>
    /// Значение перечисления TextDecorations для привязки к текстовому блоку.
    /// </summary>
    public TextDecorations TextDecorations =>
        IsTextStrikethrough ? TextDecorations.Strikethrough : TextDecorations.None;

    /// <summary>
    /// Кисть для цвета текста реплики в списке (приглушает удаленные строки).
    /// </summary>
    public Brush TextBrush =>
        (Brush)Application.Current.Resources[IsTextStrikethrough ? "TextFillColorTertiaryBrush" : "TextFillColorPrimaryBrush"];

    /// <summary>
    /// Кисть для цвета статуса реплики.
    /// </summary>
    public Brush StatusBrush
    {
        get
        {
            try
            {
                if (string.IsNullOrEmpty(StatusColor))
                {
                    return (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
                }

                string hex = StatusColor.Replace("#", "");
                if (hex.Length == 6)
                {
                    byte r = byte.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
                    byte g = byte.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
                    byte b = byte.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);
                    return new SolidColorBrush(Color.FromArgb(255, r, g, b));
                }
                else if (hex.Length == 8)
                {
                    byte a = byte.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
                    byte r = byte.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
                    byte g = byte.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);
                    byte b = byte.Parse(hex.Substring(6, 2), System.Globalization.NumberStyles.HexNumber);
                    return new SolidColorBrush(Color.FromArgb(a, r, g, b));
                }

                return (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
            }
            catch
            {
                return (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
            }
        }
    }

    /// <summary>
    /// Обновить визуальное состояние строки на основе текущих фильтров.
    /// </summary>
    public void UpdateState(bool notify = true)
    {
        if (IsOriginallyEmpty)
        {
            if (_isChecked != false || _status != "Пустая" || _statusColor != "#ff85c0" || _isTextStrikethrough != true || _finalText != string.Empty)
            {
                _isChecked = false;
                _status = "Пустая";
                _statusColor = "#ff85c0"; // Розовый
                _isTextStrikethrough = true;
                _finalText = string.Empty;
                if (notify)
                {
                    OnPropertyChanged(nameof(IsChecked));
                    OnPropertyChanged(nameof(Status));
                    OnPropertyChanged(nameof(StatusColor));
                    OnPropertyChanged(nameof(IsTextStrikethrough));
                    OnPropertyChanged(nameof(FinalText));
                }
            }
            return;
        }

        // Вычисляем состояние пустоты после фильтров динамически с использованием кэширования
        string textAfterFilters = OriginalText;
        if (_filterState.StripCaps)
        {
            if (_textWithoutCaps == null)
            {
                _textWithoutCaps = AssParser.Instance.StripCaps(OriginalText);
            }
            textAfterFilters = _textWithoutCaps;
        }

        if (_filterState.StripFormatting)
        {
            if (_filterState.StripCaps)
            {
                if (_textWithoutBoth == null)
                {
                    _textWithoutBoth = AssParser.Instance.StripTags(textAfterFilters);
                }
                textAfterFilters = _textWithoutBoth;
            }
            else
            {
                textAfterFilters = CleanText;
            }
        }

        bool isEmpty;
        if (_filterState.StripFormatting)
        {
            isEmpty = string.IsNullOrWhiteSpace(textAfterFilters);
        }
        else
        {
            if (_filterState.StripCaps)
            {
                if (_textWithoutBoth == null)
                {
                    _textWithoutBoth = AssParser.Instance.StripTags(textAfterFilters);
                }
                isEmpty = string.IsNullOrWhiteSpace(_textWithoutBoth);
            }
            else
            {
                isEmpty = IsOriginallyEmpty;
            }
        }
        IsEmptyAfterFilters = isEmpty;

        // Вычисляем финальный вид текста реплики с переносами
        string final = textAfterFilters.Replace("\\N", Environment.NewLine, StringComparison.OrdinalIgnoreCase)
                                       .Replace("\\n", Environment.NewLine, StringComparison.OrdinalIgnoreCase);

        // Проверяем фильтрацию по актеру, стилю, эффекту
        bool isFiltered =
            (!string.IsNullOrEmpty(Actor) && _filterState.ExcludedActors.Contains(Actor)) ||
            (!string.IsNullOrEmpty(Style) && _filterState.ExcludedStyles.Contains(Style)) ||
            (!string.IsNullOrEmpty(Effect) && _filterState.ExcludedEffects.Contains(Effect));

        // Проверяем ручные переопределения
        bool isManuallyIncluded = _filterState.ManualInclusions.TryGetValue(FilePath, out var incSet) && incSet.Contains(Index);
        bool isManuallyExcluded = _filterState.ManualExclusions.TryGetValue(FilePath, out var excSet) && excSet.Contains(Index);

        // Строка считается удаленной, если:
        // 1. Исключена вручную
        // 2. Пустая после CAPS/тегов и не включена вручную
        // 3. Отфильтрована по актеру/стилю/эффекту и не включена вручную
        bool newIsDeleted = isManuallyExcluded ||
                            (isEmpty && !isManuallyIncluded) ||
                            (isFiltered && !isManuallyIncluded);

        bool newIsChecked = !newIsDeleted;
        string newStatus;
        string newStatusColor;

        if (newIsDeleted)
        {
            if (isManuallyExcluded)
            {
                newStatus = "Искл. вручную";
                newStatusColor = "#e81123"; // Красный
            }
            else if (isEmpty)
            {
                newStatus = "Удалено (CAPS/Теги)";
                newStatusColor = "#ffa940"; // Оранжевый
            }
            else
            {
                newStatus = "Удалено (Фильтр)";
                newStatusColor = "#e81123"; // Красный
            }
        }
        else
        {
            if (isManuallyIncluded)
            {
                newStatus = "Вкл. вручную";
                newStatusColor = "#faad14"; // Золотой
            }
            else
            {
                newStatus = "ОК";
                newStatusColor = "#22b473"; // Зеленый
            }
        }

        bool hasChanges = _isChecked != newIsChecked ||
                          _status != newStatus ||
                          _statusColor != newStatusColor ||
                          _isTextStrikethrough != newIsDeleted ||
                          _finalText != final;

        if (hasChanges)
        {
            _isChecked = newIsChecked;
            _status = newStatus;
            _statusColor = newStatusColor;
            _isTextStrikethrough = newIsDeleted;
            _finalText = final;

            if (notify)
            {
                OnPropertyChanged(nameof(IsChecked));
                OnPropertyChanged(nameof(Status));
                OnPropertyChanged(nameof(StatusColor));
                OnPropertyChanged(nameof(IsTextStrikethrough));
                OnPropertyChanged(nameof(TextDecorations));
                OnPropertyChanged(nameof(TextBrush));
                OnPropertyChanged(nameof(StatusBrush));
                OnPropertyChanged(nameof(FinalText));
            }
        }
    }

    private void OnUserCheckChanged(bool checkedState)
    {
        if (IsOriginallyEmpty) return;

        bool isFiltered =
            (!string.IsNullOrEmpty(Actor) && _filterState.ExcludedActors.Contains(Actor)) ||
            (!string.IsNullOrEmpty(Style) && _filterState.ExcludedStyles.Contains(Style)) ||
            (!string.IsNullOrEmpty(Effect) && _filterState.ExcludedEffects.Contains(Effect));

        bool shouldBeManual = isFiltered || IsEmptyAfterFilters;

        if (checkedState)
        {
            // Пользователь включает реплику
            if (_filterState.ManualExclusions.TryGetValue(FilePath, out var excSet))
            {
                excSet.Remove(Index);
            }

            if (shouldBeManual)
            {
                if (!_filterState.ManualInclusions.TryGetValue(FilePath, out var incSet))
                {
                    incSet = new HashSet<int>();
                    _filterState.ManualInclusions[FilePath] = incSet;
                }
                incSet.Add(Index);
            }
        }
        else
        {
            // Пользователь выключает реплику
            if (_filterState.ManualInclusions.TryGetValue(FilePath, out var incSet))
            {
                incSet.Remove(Index);
            }

            if (!shouldBeManual)
            {
                if (!_filterState.ManualExclusions.TryGetValue(FilePath, out var excSet))
                {
                    excSet = new HashSet<int>();
                    _filterState.ManualExclusions[FilePath] = excSet;
                }
                excSet.Add(Index);
            }
        }

        UpdateState(true);
    }
}

/// <summary>
/// Модель представления для окна предпросмотра субтитров.
/// </summary>
public sealed partial class SubtitlePreviewViewModel : ObservableObject
{
    private readonly SubtitleFilterState _filterState;
    private readonly ObservableCollection<SubtitlePreviewLine> _filteredLines = new();
    private bool _isBulkUpdating;
    private readonly string? _settingsGroupName;
    private readonly List<FilterItemViewModel> _allActors = new();
    private readonly List<FilterItemViewModel> _allStyles = new();
    private readonly List<FilterItemViewModel> _allEffects = new();

    /// <summary>
    /// Полный список строк субтитров.
    /// </summary>
    public ObservableCollection<SubtitlePreviewLine> SubtitleLines { get; } = new();

    /// <summary>
    /// Список строк субтитров после фильтрации поисковым запросом.
    /// </summary>
    public ObservableCollection<SubtitlePreviewLine> FilteredLines => _filteredLines;

    /// <summary>
    /// Список уникальных актеров.
    /// </summary>
    public ObservableCollection<FilterItemViewModel> Actors { get; } = new();

    /// <summary>
    /// Список уникальных стилей.
    /// </summary>
    public ObservableCollection<FilterItemViewModel> Styles { get; } = new();

    /// <summary>
    /// Список уникальных эффектов.
    /// </summary>
    public ObservableCollection<FilterItemViewModel> Effects { get; } = new();

    /// <summary>
    /// Удалять теги форматирования.
    /// </summary>
    [ObservableProperty]
    public partial bool StripFormatting { get; set; }

    /// <summary>
    /// Удалять текст в верхнем регистре (CAPS LOCK).
    /// </summary>
    [ObservableProperty]
    public partial bool StripCaps { get; set; }

    /// <summary>
    /// Путь к выбранному файлу субтитров для фильтрации предпросмотра (null для всех файлов).
    /// </summary>
    [ObservableProperty]
    public partial string? SelectedFilePath { get; set; }

    partial void OnSelectedFilePathChanged(string? value)
    {
        UpdateFilteredLines();
    }

    /// <summary>
    /// Текст для поиска по репликам.
    /// </summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>
    /// Текст поиска для вкладки актеров.
    /// </summary>
    [ObservableProperty]
    public partial string ActorsSearchText { get; set; } = string.Empty;

    /// <summary>
    /// Текст поиска для вкладки стилей.
    /// </summary>
    [ObservableProperty]
    public partial string StylesSearchText { get; set; } = string.Empty;

    /// <summary>
    /// Текст поиска для вкладки эффектов.
    /// </summary>
    [ObservableProperty]
    public partial string EffectsSearchText { get; set; } = string.Empty;

    /// <summary>
    /// Инициализирует новый экземпляр SubtitlePreviewViewModel.
    /// </summary>
    public SubtitlePreviewViewModel(SubtitleFilterState filterState, string? settingsGroupName = null)
    {
        _filterState = filterState;
        _settingsGroupName = settingsGroupName;
        StripFormatting = _filterState.StripFormatting;
        StripCaps = _filterState.StripCaps;

        PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StripFormatting) || e.PropertyName == nameof(StripCaps))
        {
            ApplyFilters();
            if (!string.IsNullOrEmpty(_settingsGroupName))
            {
                if (e.PropertyName == nameof(StripFormatting))
                {
                    SettingsManager.Instance.SetSetting(_settingsGroupName, "strip_formatting", StripFormatting);
                }
                else if (e.PropertyName == nameof(StripCaps))
                {
                    SettingsManager.Instance.SetSetting(_settingsGroupName, "strip_caps", StripCaps);
                }
            }
        }
    }

    /// <summary>
    /// Вызывает метод фильтрации при изменении текста поиска.
    /// </summary>
    partial void OnSearchTextChanged(string value)
    {
        UpdateFilteredLines();
    }

    partial void OnActorsSearchTextChanged(string value)
    {
        UpdateFilteredActors();
    }

    partial void OnStylesSearchTextChanged(string value)
    {
        UpdateFilteredStyles();
    }

    partial void OnEffectsSearchTextChanged(string value)
    {
        UpdateFilteredEffects();
    }

    /// <summary>
    /// Обновить отфильтрованный список актеров на основе поискового запроса.
    /// </summary>
    public void UpdateFilteredActors()
    {
        var query = ActorsSearchText?.Trim();
        var targetList = string.IsNullOrEmpty(query)
            ? _allActors
            : _allActors.Where(a => a.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        SyncCollection(Actors, targetList);
    }

    /// <summary>
    /// Обновить отфильтрованный список стилей на основе поискового запроса.
    /// </summary>
    public void UpdateFilteredStyles()
    {
        var query = StylesSearchText?.Trim();
        var targetList = string.IsNullOrEmpty(query)
            ? _allStyles
            : _allStyles.Where(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        SyncCollection(Styles, targetList);
    }

    /// <summary>
    /// Обновить отфильтрованный список эффектов на основе поискового запроса.
    /// </summary>
    public void UpdateFilteredEffects()
    {
        var query = EffectsSearchText?.Trim();
        var targetList = string.IsNullOrEmpty(query)
            ? _allEffects
            : _allEffects.Where(e => e.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        SyncCollection(Effects, targetList);
    }

    private void SyncCollection<T>(ObservableCollection<T> collection, IList<T> targetList)
    {
        if (collection.Count == targetList.Count && collection.SequenceEqual(targetList))
        {
            return;
        }

        collection.Clear();
        foreach (var item in targetList)
        {
            collection.Add(item);
        }
    }

    /// <summary>
    /// Асинхронно загрузить данные из файлов субтитров в модель представления.
    /// </summary>
    public async Task LoadDataAsync(IEnumerable<string> filePaths)
    {
        SubtitleLines.Clear();
        _allActors.Clear();
        _allStyles.Clear();
        _allEffects.Clear();
        Actors.Clear();
        Styles.Clear();
        Effects.Clear();

        var tempLines = new List<SubtitlePreviewLine>();
        var uniqueActors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uniqueStyles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uniqueEffects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await Task.Run(() =>
        {
            foreach (var path in filePaths)
            {
                if (!File.Exists(path)) continue;
                try
                {
                    var assData = AssParser.Instance.Parse(path);
                    for (int i = 0; i < assData.Dialogues.Count; i++)
                    {
                        var dialogue = assData.Dialogues[i];
                        var line = new SubtitlePreviewLine(i, dialogue, path, _filterState);
                        tempLines.Add(line);

                        if (!string.IsNullOrEmpty(dialogue.Actor)) uniqueActors.Add(dialogue.Actor);
                        if (!string.IsNullOrEmpty(dialogue.Style)) uniqueStyles.Add(dialogue.Style);
                        if (!string.IsNullOrEmpty(dialogue.Effect)) uniqueEffects.Add(dialogue.Effect);
                    }
                }
                catch (Exception ex)
                {
                    LogService.Instance.Exception(
                        ex,
                        $"Ошибка парсинга файла при подготовке предпросмотра: '{path}'",
                        "SubtitlePreviewViewModel");
                }
            }
        });

        int globalIdx = 0;
        foreach (var line in tempLines)
        {
            line.GlobalIndex = globalIdx++;
            line.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(SubtitlePreviewLine.IsChecked))
                {
                    OnPropertyChanged(nameof(SubtitleLines));
                }
            };
            SubtitleLines.Add(line);
        }

        foreach (var actor in uniqueActors.OrderBy(a => a))
        {
            var isExcluded = _filterState.ExcludedActors.Contains(actor);
            var item = new FilterItemViewModel(actor, !isExcluded);
            item.PropertyChanged += (s, e) => {
                if (e.PropertyName == nameof(FilterItemViewModel.IsChecked))
                {
                    if (!_isBulkUpdating)
                    {
                        if (item.IsChecked) _filterState.ExcludedActors.Remove(item.Name);
                        else _filterState.ExcludedActors.Add(item.Name);
                        ApplyFilters();
                    }
                }
            };
            _allActors.Add(item);
        }

        foreach (var style in uniqueStyles.OrderBy(s => s))
        {
            var isExcluded = _filterState.ExcludedStyles.Contains(style);
            var item = new FilterItemViewModel(style, !isExcluded);
            item.PropertyChanged += (s, e) => {
                if (e.PropertyName == nameof(FilterItemViewModel.IsChecked))
                {
                    if (!_isBulkUpdating)
                    {
                        if (item.IsChecked) _filterState.ExcludedStyles.Remove(item.Name);
                        else _filterState.ExcludedStyles.Add(item.Name);
                        ApplyFilters();
                    }
                }
            };
            _allStyles.Add(item);
        }

        foreach (var effect in uniqueEffects.OrderBy(e => e))
        {
            var isExcluded = _filterState.ExcludedEffects.Contains(effect);
            var item = new FilterItemViewModel(effect, !isExcluded);
            item.PropertyChanged += (s, e) => {
                if (e.PropertyName == nameof(FilterItemViewModel.IsChecked))
                {
                    if (!_isBulkUpdating)
                    {
                        if (item.IsChecked) _filterState.ExcludedEffects.Remove(item.Name);
                        else _filterState.ExcludedEffects.Add(item.Name);
                        ApplyFilters();
                    }
                }
            };
            _allEffects.Add(item);
        }

        UpdateFilteredActors();
        UpdateFilteredStyles();
        UpdateFilteredEffects();
        UpdateFilteredLines();
    }

    /// <summary>
    /// Применить глобальные фильтры к репликам.
    /// </summary>
    public void ApplyFilters()
    {
        _filterState.StripFormatting = StripFormatting;
        _filterState.StripCaps = StripCaps;

        foreach (var line in SubtitleLines)
        {
            line.UpdateState(true);
        }

        UpdateFilteredLines();
    }

    /// <summary>
    /// Установить состояние выбора для всех фильтров в указанной категории пакетно.
    /// </summary>
    /// <param name="category">Категория фильтров ("actors", "styles", "effects").</param>
    /// <param name="isChecked">Состояние выбора.</param>
    public void SetFiltersCheckedState(string category, bool isChecked)
    {
        _isBulkUpdating = true;
        try
        {
            if (category == "actors")
            {
                foreach (var actor in Actors)
                {
                    actor.IsChecked = isChecked;
                    if (isChecked) _filterState.ExcludedActors.Remove(actor.Name);
                    else _filterState.ExcludedActors.Add(actor.Name);
                }
            }
            else if (category == "styles")
            {
                foreach (var style in Styles)
                {
                    style.IsChecked = isChecked;
                    if (isChecked) _filterState.ExcludedStyles.Remove(style.Name);
                    else _filterState.ExcludedStyles.Add(style.Name);
                }
            }
            else if (category == "effects")
            {
                foreach (var effect in Effects)
                {
                    effect.IsChecked = isChecked;
                    if (isChecked) _filterState.ExcludedEffects.Remove(effect.Name);
                    else _filterState.ExcludedEffects.Add(effect.Name);
                }
            }
        }
        finally
        {
            _isBulkUpdating = false;
            ApplyFilters();
        }
    }

    /// <summary>
    /// Пересчитать отфильтрованные строки по строке поиска с сохранением положения скролла (инкрементальное обновление).
    /// </summary>
    public void UpdateFilteredLines()
    {
        var query = SearchText?.Trim();

        IEnumerable<SubtitlePreviewLine> items = SubtitleLines;

        if (!string.IsNullOrEmpty(SelectedFilePath))
        {
            items = items.Where(l => l.FilePath.Equals(SelectedFilePath, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrEmpty(query))
        {
            items = items.Where(l =>
                l.OriginalText.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                l.FileName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                l.Actor.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                l.Style.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        var targetList = items.ToList();

        // Проставляем флаг первой строки в файле
        string? currentFile = null;
        foreach (var line in targetList)
        {
            if (line.FileName != currentFile)
            {
                line.IsFirstLineInFile = true;
                currentFile = line.FileName;
            }
            else
            {
                line.IsFirstLineInFile = false;
            }
        }

        // Быстрая проверка: если количество и порядок совпадают
        bool matches = _filteredLines.Count == targetList.Count;
        if (matches)
        {
            for (int i = 0; i < targetList.Count; i++)
            {
                if (_filteredLines[i] != targetList[i])
                {
                    matches = false;
                    break;
                }
            }
        }

        if (matches)
        {
            // Состав не изменился, ничего делать не нужно!
            return;
        }

        // Если список полностью пустой в результате
        if (targetList.Count == 0)
        {
            _filteredLines.Clear();
            return;
        }

        // Если коллекция была пустой
        if (_filteredLines.Count == 0)
        {
            foreach (var item in targetList)
            {
                _filteredLines.Add(item);
            }
            return;
        }

        // Так как взаимный порядок элементов в targetList и _filteredLines всегда одинаков (по индексу строки),
        // мы можем синхронизировать коллекцию за один проход с помощью указателей.
        int targetIdx = 0;
        int filteredIdx = 0;

        while (targetIdx < targetList.Count || filteredIdx < _filteredLines.Count)
        {
            if (targetIdx < targetList.Count && filteredIdx < _filteredLines.Count)
            {
                var targetItem = targetList[targetIdx];
                var filteredItem = _filteredLines[filteredIdx];

                if (targetItem == filteredItem)
                {
                    // Элементы совпадают, просто идем дальше
                    targetIdx++;
                    filteredIdx++;
                }
                else
                {
                    // Сравниваем их глобальные индексы
                    if (targetItem.GlobalIndex < filteredItem.GlobalIndex)
                    {
                        // Элемент targetItem должен быть вставлен перед filteredItem
                        _filteredLines.Insert(filteredIdx, targetItem);
                        targetIdx++;
                        filteredIdx++;
                    }
                    else
                    {
                        // Элемент filteredItem отсутствует в новом списке, удаляем его
                        _filteredLines.RemoveAt(filteredIdx);
                    }
                }
            }
            else if (targetIdx < targetList.Count)
            {
                // В _filteredLines больше нет элементов, добавляем оставшиеся из targetList
                _filteredLines.Add(targetList[targetIdx]);
                targetIdx++;
            }
            else
            {
                // В targetList больше нет элементов, удаляем оставшиеся из _filteredLines
                _filteredLines.RemoveAt(filteredIdx);
            }
        }
    }
}
