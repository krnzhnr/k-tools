// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using KTools_App.Services.Contracts;
using KTools_App.Core;
using KTools_App.Infrastructure;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.Text;
using CommunityToolkit.Mvvm.ComponentModel;
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
/// Представляет один паттерн регулярного выражения для фильтрации текста.
/// </summary>
public sealed class PatternItemViewModel : ObservableObject
{
    private string _word = string.Empty;
    private bool _active = true;
    private int _deleteModeIndex = 0; // 0 = Удалять совпадения (only_part = true), 1 = Удалять строки с совпадениями (only_part = false)
    private string _sampleText = string.Empty;

    public string Word
    {
        get => _word;
        set
        {
            if (SetProperty(ref _word, value))
            {
                UpdateSampleText();
            }
        }
    }

    public bool Active
    {
        get => _active;
        set => SetProperty(ref _active, value);
    }

    public int DeleteModeIndex
    {
        get => _deleteModeIndex;
        set
        {
            if (SetProperty(ref _deleteModeIndex, value))
            {
                OnPropertyChanged(nameof(OnlyPart));
            }
        }
    }

    public bool OnlyPart
    {
        get => _deleteModeIndex == 0;
        set => DeleteModeIndex = value ? 0 : 1;
    }

    public string SampleText
    {
        get => _sampleText;
        private set => SetProperty(ref _sampleText, value);
    }

    public PatternItemViewModel(string word, bool active, bool onlyPart)
    {
        _word = word;
        _active = active;
        _deleteModeIndex = onlyPart ? 0 : 1;
        UpdateSampleText();
    }

    private void UpdateSampleText()
    {
        if (string.IsNullOrWhiteSpace(_word))
        {
            SampleText = string.Empty;
            return;
        }

        try
        {
            _ = new System.Text.RegularExpressions.Regex(_word);
        }
        catch (System.ArgumentException)
        {
            SampleText = "Некорректный regex";
            return;
        }

        try
        {
            string farePattern = EscapeFareSpecialChars(_word);
            var xeger = new Fare.Xeger(farePattern);
            string generated = xeger.Generate();
            
            if (string.IsNullOrEmpty(generated))
            {
                SampleText = "Пример: (пустая строка)";
            }
            else
            {
                SampleText = $"Пример совпадения: \"{generated}\"";
            }
        }
        catch (System.Exception)
        {
            SampleText = "Сложный regex (пример недоступен)";
        }
    }

    private static string EscapeFareSpecialChars(string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return pattern;

        var sb = new System.Text.StringBuilder();
        int backslashCount = 0;
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '\\')
            {
                backslashCount++;
            }
            else
            {
                if (c == '&' || c == '~')
                {
                    if (backslashCount % 2 == 0)
                    {
                        sb.Append('\\');
                    }
                }
                backslashCount = 0;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }
}

/// <summary>
/// Представляет модель строки субтитров для отображения в списке предпросмотра.
/// </summary>
public sealed class SubtitlePreviewLine : ObservableObject
{
    private readonly IAssParser _assParser;
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
        SubtitleFilterState filterState,
        IAssParser assParser)
    {
        _assParser = assParser ?? throw new ArgumentNullException(nameof(assParser));
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

        CleanText = _assParser.StripTags(OriginalText);
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

        // Последовательно применяем активные regex-паттерны
        bool isDeletedByRegex = false;
        foreach (var patternDict in _filterState.TextPatterns)
        {
            if (patternDict.TryGetValue("active", out var act) && SafeGetBool(act) &&
                patternDict.TryGetValue("word", out var p) && p?.ToString() is string pattern && !string.IsNullOrEmpty(pattern))
            {
                bool onlyPart = patternDict.TryGetValue("only_part", out var op) && SafeGetBool(op);
                try
                {
                    var regex = new System.Text.RegularExpressions.Regex(pattern);
                    if (regex.IsMatch(textAfterFilters))
                    {
                        if (onlyPart)
                        {
                            textAfterFilters = regex.Replace(textAfterFilters, string.Empty);
                        }
                        else
                        {
                            isDeletedByRegex = true;
                            break;
                        }
                    }
                }
                catch
                {
                    // Игнорируем некорректные regex в превью
                }
            }
        }

        if (_filterState.StripCaps)
        {
            if (_textWithoutCaps == null)
            {
                _textWithoutCaps = _assParser.StripCaps(textAfterFilters);
            }
            textAfterFilters = _textWithoutCaps;
        }

        if (_filterState.StripFormatting)
        {
            if (_textWithoutBoth == null)
            {
                _textWithoutBoth = _assParser.StripTags(textAfterFilters);
            }
            textAfterFilters = _textWithoutBoth;
        }

        bool isEmpty = isDeletedByRegex || string.IsNullOrWhiteSpace(_assParser.StripTags(textAfterFilters));
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
        // 2. Пустая после CAPS/тегов/regex и не включена вручную
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
                newStatus = isDeletedByRegex ? "Удалено (Regex)" : "Удалено (CAPS/Теги)";
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

    /// <summary>
    /// Сбросить кэшированные текстовые представления (при изменении динамических паттернов).
    /// </summary>
    public void ResetCache()
    {
        _textWithoutCaps = null;
        _textWithoutBoth = null;
    }

    private static bool SafeGetBool(object? obj)
    {
        if (obj == null) return false;
        if (obj is bool b) return b;
        if (obj is System.Text.Json.JsonElement elem)
        {
            if (elem.ValueKind == System.Text.Json.JsonValueKind.True) return true;
            if (elem.ValueKind == System.Text.Json.JsonValueKind.False) return false;
            if (elem.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return bool.TryParse(elem.GetString(), out var parsed) && parsed;
            }
        }
        return false;
    }
}

/// <summary>
/// Модель представления для окна предпросмотра субтитров.
/// </summary>
public sealed partial class SubtitlePreviewViewModel : ThreadSafeViewModel
{
    private readonly IAssParser _assParser;
    private readonly ISettingsManager _settingsManager;
    private readonly ILogService _logService;
    private readonly SubtitleFilterState _filterState;
    private readonly ObservableRangeCollection<SubtitlePreviewLine> _filteredLines = new();
    private bool _isBulkUpdating;
    private readonly string? _settingsGroupName;
    private readonly List<FilterItemViewModel> _allActors = new();
    private readonly List<FilterItemViewModel> _allStyles = new();
    private readonly List<FilterItemViewModel> _allEffects = new();

    /// <summary>
    /// Полный список строк субтитров.
    /// </summary>
    public ObservableRangeCollection<SubtitlePreviewLine> SubtitleLines { get; } = new();

    /// <summary>
    /// Список строк субтитров после фильтрации поисковым запросом.
    /// </summary>
    public ObservableRangeCollection<SubtitlePreviewLine> FilteredLines => _filteredLines;

    /// <summary>
    /// Список уникальных актеров.
    /// </summary>
    public ObservableRangeCollection<FilterItemViewModel> Actors { get; } = new();

    /// <summary>
    /// Список уникальных стилей.
    /// </summary>
    public ObservableRangeCollection<FilterItemViewModel> Styles { get; } = new();

    /// <summary>
    /// Список уникальных эффектов.
    /// </summary>
    public ObservableRangeCollection<FilterItemViewModel> Effects { get; } = new();

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
    /// Инициализирует новый экземпляр SubtitlePreviewViewModel с поддержкой DI.
    /// </summary>
    public SubtitlePreviewViewModel(
        SubtitleFilterState filterState, 
        string? settingsGroupName = null,
        IAssParser? assParser = null,
        ISettingsManager? settingsManager = null,
        ILogService? logService = null)
    {
        _assParser = assParser ?? App.Services.GetRequiredService<IAssParser>();
        _settingsManager = settingsManager ?? App.Services.GetRequiredService<ISettingsManager>();
        _logService = logService ?? App.Services.GetRequiredService<ILogService>();
        _filterState = filterState;
        _settingsGroupName = settingsGroupName;
        StripFormatting = _filterState.StripFormatting;
        StripCaps = _filterState.StripCaps;

        Patterns.CollectionChanged += OnPatternsCollectionChanged;
        LoadPatterns(_filterState.TextPatterns);

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
                    _settingsManager.SetSetting(_settingsGroupName, "strip_formatting", StripFormatting);
                }
                else if (e.PropertyName == nameof(StripCaps))
                {
                    _settingsManager.SetSetting(_settingsGroupName, "strip_caps", StripCaps);
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

    private void SyncCollection<T>(ObservableRangeCollection<T> collection, IList<T> targetList)
    {
        if (collection.Count == targetList.Count && collection.SequenceEqual(targetList))
        {
            return;
        }

        collection.ReplaceRange(targetList);
    }

    private void OnSubtitleLinePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isBulkUpdating) return;
        if (e.PropertyName == nameof(SubtitlePreviewLine.IsChecked))
        {
            OnPropertyChanged(nameof(SubtitleLines));
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
                    var assData = _assParser.Parse(path);
                    for (int i = 0; i < assData.Dialogues.Count; i++)
                    {
                        var dialogue = assData.Dialogues[i];
                        var line = new SubtitlePreviewLine(i, dialogue, path, _filterState, _assParser);
                        tempLines.Add(line);

                        if (!string.IsNullOrEmpty(dialogue.Actor)) uniqueActors.Add(dialogue.Actor);
                        if (!string.IsNullOrEmpty(dialogue.Style)) uniqueStyles.Add(dialogue.Style);
                        if (!string.IsNullOrEmpty(dialogue.Effect)) uniqueEffects.Add(dialogue.Effect);
                    }
                }
                catch (Exception ex)
                {
                    _logService.Exception(
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
            line.PropertyChanged += OnSubtitleLinePropertyChanged;
        }

        SubtitleLines.ReplaceRange(tempLines);

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
    /// Список паттернов регулярных выражений для очистки.
    /// </summary>
    public List<Dictionary<string, object>> TextPatterns => _filterState.TextPatterns;

    /// <summary>
    /// Коллекция моделей паттернов для привязки к ListView.
    /// </summary>
    public ObservableCollection<PatternItemViewModel> Patterns { get; } = new();

    private bool _isSuppressingSave = false;

    private void OnPatternsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_isSuppressingSave) return;

        if (e.OldItems != null)
        {
            foreach (PatternItemViewModel item in e.OldItems)
            {
                item.PropertyChanged -= OnPatternItemPropertyChanged;
            }
        }
        if (e.NewItems != null)
        {
            foreach (PatternItemViewModel item in e.NewItems)
            {
                item.PropertyChanged += OnPatternItemPropertyChanged;
            }
        }

        SavePatterns();
    }

    private void OnPatternItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isSuppressingSave) return;

        if (e.PropertyName == nameof(PatternItemViewModel.Word) ||
            e.PropertyName == nameof(PatternItemViewModel.Active) ||
            e.PropertyName == nameof(PatternItemViewModel.OnlyPart))
        {
            SavePatterns();
        }
    }

    public void LoadPatterns(List<Dictionary<string, object>> rawList)
    {
        _isSuppressingSave = true;

        foreach (var item in Patterns)
        {
            item.PropertyChanged -= OnPatternItemPropertyChanged;
        }
        Patterns.Clear();

        foreach (var item in rawList)
        {
            string word = item.TryGetValue("word", out var w) ? w?.ToString() ?? "" : "";
            bool active = !item.TryGetValue("active", out var act) || SafeGetBool(act);
            bool onlyPart = !item.TryGetValue("only_part", out var op) || SafeGetBool(op);

            var vm = new PatternItemViewModel(word, active, onlyPart);
            vm.PropertyChanged += OnPatternItemPropertyChanged;
            Patterns.Add(vm);
        }

        _isSuppressingSave = false;
        SavePatterns();
    }

    /// <summary>
    /// Сохранить список паттернов регулярных выражений в состоянии фильтра и настройках.
    /// </summary>
    public void SavePatterns()
    {
        var rawList = new List<Dictionary<string, object>>();
        foreach (var item in Patterns)
        {
            rawList.Add(new Dictionary<string, object>
            {
                { "word", item.Word },
                { "active", item.Active },
                { "only_part", item.OnlyPart }
            });
        }

        _filterState.TextPatterns.Clear();
        _filterState.TextPatterns.AddRange(rawList);

        if (!string.IsNullOrEmpty(_settingsGroupName))
        {
            _settingsManager.SetSetting(_settingsGroupName, "text_patterns", rawList);
        }

        ResetAllLinesCacheAndApply();
    }

    /// <summary>
    /// Сбросить кэш текстовых представлений у всех строк и переприменить фильтры.
    /// </summary>
    public void ResetAllLinesCacheAndApply()
    {
        foreach (var line in SubtitleLines)
        {
            line.ResetCache();
        }
        ApplyFilters();
    }

    /// <summary>
    /// Применить глобальные фильтры к репликам.
    /// </summary>
    public void ApplyFilters()
    {
        _isBulkUpdating = true;
        try
        {
            _filterState.StripFormatting = StripFormatting;
            _filterState.StripCaps = StripCaps;

            foreach (var line in SubtitleLines)
            {
                line.UpdateState(true);
            }
        }
        finally
        {
            _isBulkUpdating = false;
        }

        OnPropertyChanged(nameof(SubtitleLines));
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
    /// Пересчитать отфильтрованные строки по строке поиска и выбранному файлу с пакетным обновлением списка.
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

        _filteredLines.ReplaceRange(targetList);
    }

    private static bool SafeGetBool(object? obj)
    {
        if (obj == null) return false;
        if (obj is bool b) return b;
        if (obj is System.Text.Json.JsonElement elem)
        {
            if (elem.ValueKind == System.Text.Json.JsonValueKind.True) return true;
            if (elem.ValueKind == System.Text.Json.JsonValueKind.False) return false;
            if (elem.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return bool.TryParse(elem.GetString(), out var parsed) && parsed;
            }
        }
        return false;
    }
}
