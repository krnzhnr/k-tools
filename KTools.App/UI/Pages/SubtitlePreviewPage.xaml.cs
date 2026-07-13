// -*- coding: utf-8 -*-
using System;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.Text;
using KTools_App.Infrastructure;
using KTools_App.ViewModels;
using KTools_App.Services.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace KTools_App.UI.Pages;

/// <summary>
/// Страница предпросмотра субтитров и управления фильтрами.
/// </summary>
public sealed partial class SubtitlePreviewPage : Page
{
    private IAssParser _assParser => App.Services.GetRequiredService<IAssParser>();
    /// <summary>
    /// Предоставляет доступ к элементу заголовка окна для интеграции с TitleBar.
    /// </summary>
    public TitleBar TitleBarElement => AppTitleBar;

    /// <summary>
    /// Ссылка на родительское окно.
    /// </summary>
    public Window? OwnerWindow { get; set; }

    /// <summary>
    /// Модель представления для управления данными предпросмотра.
    /// </summary>
    public SubtitlePreviewViewModel ViewModel { get; }

    /// <summary>
    /// Инициализирует новый экземпляр класса SubtitlePreviewPage.
    /// </summary>
    public SubtitlePreviewPage(SubtitlePreviewViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();



        // Слушаем изменения коллекции строк для обновления статистики
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        // Заполняем боковое меню списком уникальных файлов
        var uniquePaths = ViewModel.SubtitleLines
            .Select(l => l.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        PopulateFilesMenu(uniquePaths);

        UpdateStatsText();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SubtitlePreviewViewModel.SearchText) ||
            e.PropertyName == nameof(SubtitlePreviewViewModel.SubtitleLines))
        {
            UpdateStatsText();
        }
    }

    /// <summary>
    /// Динамически заполняет подменю файлов в разделе «Предпросмотр» боковой панели с поддержкой переноса длинных имен.
    /// </summary>
    private void PopulateFilesMenu(System.Collections.Generic.IEnumerable<string> filePaths)
    {
        PreviewRootItem.MenuItems.Clear();

        // Добавляем пункт «Все файлы»
        var allFilesItem = new NavigationViewItem
        {
            Content = "Все файлы",
            Icon = new SymbolIcon(Symbol.Copy),
            Tag = "preview_all"
        };
        PreviewRootItem.MenuItems.Add(allFilesItem);

        // Добавляем каждый файл отдельно
        foreach (var path in filePaths)
        {
            var fileName = System.IO.Path.GetFileName(path);

            // Используем TextBlock для переноса длинных имен файлов
            var textBlock = new TextBlock
            {
                Text = fileName,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 3,
                Margin = new Thickness(0, 4, 0, 4),
                VerticalAlignment = VerticalAlignment.Center
            };

            var item = new NavigationViewItem
            {
                Content = textBlock,
                Icon = new SymbolIcon(Symbol.Page2),
                Tag = $"file:{path}"
            };

            // Добавляем подсказку с полным именем файла при наведении
            ToolTipService.SetToolTip(item, fileName);

            PreviewRootItem.MenuItems.Add(item);
        }

        // Раскрываем корневой элемент предпросмотра
        PreviewRootItem.IsExpanded = true;

        // Программный выбор первого элемента («Все файлы»)
        MainNavigation.SelectedItem = allFilesItem;
    }

    /// <summary>
    /// Переключает видимость контентных панелей в зависимости от выбранного пункта бокового меню.
    /// </summary>
    private void MainNavigation_SelectionChanged(
        NavigationView sender, 
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer is NavigationViewItem item)
        {
            string tag = item.Tag?.ToString() ?? string.Empty;

            if (tag == "preview_all" || tag.StartsWith("file:"))
            {
                PreviewGrid.Visibility = Visibility.Visible;
                ActorsGrid.Visibility = Visibility.Collapsed;
                StylesGrid.Visibility = Visibility.Collapsed;
                EffectsGrid.Visibility = Visibility.Collapsed;
                PatternsGrid.Visibility = Visibility.Collapsed;

                ViewModel.SelectedFilePath = tag == "preview_all" ? null : tag.Substring("file:".Length);
                UpdateStatsText();
            }
            else
            {
                PreviewGrid.Visibility = Visibility.Collapsed;
                ActorsGrid.Visibility = tag == "actors" ? Visibility.Visible : Visibility.Collapsed;
                StylesGrid.Visibility = tag == "styles" ? Visibility.Visible : Visibility.Collapsed;
                EffectsGrid.Visibility = tag == "effects" ? Visibility.Visible : Visibility.Collapsed;
                PatternsGrid.Visibility = tag == "patterns" ? Visibility.Visible : Visibility.Collapsed;

            }
        }
    }

    /// <summary>
    /// Выделить все чекбоксы в выбранной категории фильтров.
    /// </summary>
    private void SelectAllFilters_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string tag)
        {
            ViewModel.SetFiltersCheckedState(tag, true);
        }
    }

    /// <summary>
    /// Снять выделение со всех чекбоксов в выбранной категории фильтров.
    /// </summary>
    private void DeselectAllFilters_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string tag)
        {
            ViewModel.SetFiltersCheckedState(tag, false);
        }
    }

    /// <summary>
    /// Обработчик клика по чекбоксу фильтра (актеры, стили, эффекты).
    /// </summary>
    private void FilterCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkBox && checkBox.DataContext is FilterItemViewModel item)
        {
            item.IsChecked = checkBox.IsChecked ?? false;
        }
    }

    /// <summary>
    /// Обработчик клика по чекбоксу конкретной строки субтитров.
    /// </summary>
    private void SubtitleCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkBox && checkBox.DataContext is SubtitlePreviewLine line)
        {
            line.IsChecked = checkBox.IsChecked ?? false;
        }
    }


    /// <summary>
    /// Обновить текст статистики сверху с учетом выбранного файла.
    /// </summary>
    private void UpdateStatsText()
    {
        var lines = ViewModel.SubtitleLines.AsEnumerable();
        if (!string.IsNullOrEmpty(ViewModel.SelectedFilePath))
        {
            lines = lines.Where(l => l.FilePath.Equals(ViewModel.SelectedFilePath, StringComparison.OrdinalIgnoreCase));
        }

        var linesList = lines.ToList();
        int total = linesList.Count;
        int active = linesList.Count(l => l.IsChecked);
        int deleted = total - active;
        int filtered = ViewModel.FilteredLines.Count;

        if (string.IsNullOrEmpty(ViewModel.SearchText))
        {
            StatsTextBlock.Text = $"Всего реплик: {total} | Активных: {active} | Исключено: {deleted}";
        }
        else
        {
            StatsTextBlock.Text = $"Найдено по поиску: {filtered} из {total} | Активных: {active} | Исключено: {deleted}";
        }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<TextBlock, PropertyChangedEventHandler> _handlers = new();
    private readonly SolidColorBrush _purpleBrush = new(Color.FromArgb(255, 146, 84, 222));
    private readonly SolidColorBrush _redBrush = new(Color.FromArgb(255, 232, 17, 35));

    /// <summary>
    /// Обработчик изменения контекста данных для текстового блока субтитра.
    /// Выполняет динамическую подсветку и зачеркивание тегов и CAPS-реплик.
    /// </summary>
    private void SubtitleTextBlock_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender is not TextBlock textBlock) return;

        // Отписываемся от старой строки, если она была привязана
        if (_handlers.TryRemove(textBlock, out var oldHandler) && textBlock.Tag is SubtitlePreviewLine oldLine)
        {
            oldLine.PropertyChanged -= oldHandler;
        }

        if (args.NewValue is SubtitlePreviewLine line)
        {
            textBlock.Tag = line;
            PropertyChangedEventHandler newHandler = (s, e) =>
            {
                textBlock.DispatcherQueue.TryEnqueue(() => RenderSubtitleText(textBlock, line));
            };
            line.PropertyChanged += newHandler;
            _handlers[textBlock] = newHandler;

            RenderSubtitleText(textBlock, line);
        }
        else
        {
            textBlock.Tag = null;
            textBlock.Inlines.Clear();
        }
    }

    /// <summary>
    /// Отрисовывает текст субтитра с подсветкой тегов форматирования и CAPS-реплик.
    /// </summary>
    private void ApplyStandardStyles(Run run, bool isTag, bool shouldStripCaps, Brush primaryBrush)
    {
        if (isTag)
        {
            if (shouldStripCaps)
            {
                run.Foreground = _redBrush;
                run.TextDecorations = TextDecorations.Strikethrough;
            }
            else if (ViewModel.StripFormatting)
            {
                run.Foreground = _redBrush;
                run.TextDecorations = TextDecorations.Strikethrough;
            }
            else
            {
                run.Foreground = _purpleBrush;
            }
        }
        else
        {
            if (shouldStripCaps)
            {
                run.Foreground = _redBrush;
                run.TextDecorations = TextDecorations.Strikethrough;
            }
            else
            {
                run.Foreground = primaryBrush;
            }
        }
    }

    private void RenderPart(TextBlock textBlock, string part, int startOriginalIndex, bool[] isDeletedByRegex, bool isTag, bool isTransfer, bool shouldStripCaps, Brush primaryBrush)
    {
        int i = 0;
        while (i < part.Length)
        {
            bool isDeleted = isDeletedByRegex[startOriginalIndex + i];
            int runStart = i;
            while (i < part.Length && isDeletedByRegex[startOriginalIndex + i] == isDeleted)
            {
                i++;
            }
            
            string runText = part.Substring(runStart, i - runStart);
            var run = new Run { Text = runText };
            if (isDeleted)
            {
                run.Foreground = _redBrush;
                run.TextDecorations = TextDecorations.Strikethrough;
            }
            else
            {
                if (isTransfer)
                {
                    if (ViewModel.StripFormatting)
                    {
                        run.Foreground = _redBrush;
                        run.TextDecorations = TextDecorations.Strikethrough;
                    }
                    else
                    {
                        run.Foreground = _purpleBrush;
                    }
                }
                else
                {
                    ApplyStandardStyles(run, isTag, shouldStripCaps, primaryBrush);
                }
            }
            textBlock.Inlines.Add(run);
        }
    }

    /// <summary>
    /// Отрисовывает текст субтитра с подсветкой тегов форматирования, CAPS-реплик и частей, удаляемых регулярными выражениями.
    /// </summary>
    private void RenderSubtitleText(TextBlock textBlock, SubtitlePreviewLine line)
    {
        textBlock.Inlines.Clear();

        string text = line.OriginalText;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var primaryBrush = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];

        // Если строка удалена целиком (ручным чекбоксом или общим фильтром)
        if (line.IsTextStrikethrough)
        {
            var run = new Run { Text = text };
            textBlock.TextDecorations = TextDecorations.Strikethrough;
            textBlock.Foreground = _redBrush;
            textBlock.Inlines.Add(run);
            return;
        }

        textBlock.TextDecorations = TextDecorations.None;
        textBlock.Foreground = primaryBrush;

        // Построение карты удаления символов по регулярным выражениям
        bool[] isDeletedByRegex = new bool[text.Length];
        var nonDeletedChars = new List<(char Char, int OriginalIndex)>();
        for (int i = 0; i < text.Length; i++)
        {
            nonDeletedChars.Add((text[i], i));
        }

        if (ViewModel.TextPatterns != null)
        {
            foreach (var patternDict in ViewModel.TextPatterns)
            {
                if (patternDict.TryGetValue("active", out var act) && SafeGetBool(patternDict, "active") &&
                    patternDict.TryGetValue("only_part", out var op) && SafeGetBool(patternDict, "only_part") &&
                    patternDict.TryGetValue("word", out var p) && p?.ToString() is string pattern && !string.IsNullOrEmpty(pattern))
                {
                    try
                    {
                        var regex = new System.Text.RegularExpressions.Regex(pattern);
                        string activeString = new string(nonDeletedChars.Select(x => x.Char).ToArray());
                        var matches = regex.Matches(activeString);
                        var indicesToRemove = new HashSet<int>();
                        foreach (System.Text.RegularExpressions.Match match in matches)
                        {
                            for (int m = 0; m < match.Length; m++)
                            {
                                indicesToRemove.Add(match.Index + m);
                            }
                        }

                        foreach (int idx in indicesToRemove)
                        {
                            int originalIndex = nonDeletedChars[idx].OriginalIndex;
                            isDeletedByRegex[originalIndex] = true;
                        }

                        nonDeletedChars = nonDeletedChars.Where((x, idx) => !indicesToRemove.Contains(idx)).ToList();
                    }
                    catch
                    {
                        // Игнорируем некорректные regex
                    }
                }
            }
        }

        // Разделяем строку по переносам \N и \n с сохранением разделителей
        string[] lineParts = System.Text.RegularExpressions.Regex.Split(text, @"(\\N|\\n)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        int currentOriginalIndex = 0;

        for (int i = 0; i < lineParts.Length; i++)
        {
            string part = lineParts[i];
            if (string.IsNullOrEmpty(part)) continue;

            if (i % 2 == 1)
            {
                // Это перенос строки (\N или \n)
                RenderPart(textBlock, part, currentOriginalIndex, isDeletedByRegex, false, true, false, primaryBrush);
                currentOriginalIndex += part.Length;
            }
            else
            {
                // Проверяем, является ли часть строки полным CAPS LOCK
                bool isCaps = _assParser.IsFullCaps(part);
                bool shouldStripCaps = isCaps && ViewModel.StripCaps;

                // Разделяем на теги форматирования и обычный текст
                var tagRegex = new System.Text.RegularExpressions.Regex(@"(\{[^}]*\}|</?[a-z][a-z0-9]*(?:\s+[^>]*?)?>)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                string[] subParts = tagRegex.Split(part);

                for (int j = 0; j < subParts.Length; j++)
                {
                    string subPart = subParts[j];
                    if (string.IsNullOrEmpty(subPart)) continue;

                    RenderPart(textBlock, subPart, currentOriginalIndex, isDeletedByRegex, j % 2 == 1, false, shouldStripCaps, primaryBrush);
                    currentOriginalIndex += subPart.Length;
                }
            }
        }
    }

    /// <summary>
    /// Обработчик клика по кнопке "Готово". Закрывает окно предпросмотра.
    /// </summary>
    private void DoneButton_Click(object sender, RoutedEventArgs e)
    {
        OwnerWindow?.Close();
    }

    /// <summary>
    /// Ищет файл иконки сначала в корне каталога приложения (установленная версия),
    /// затем в подпапке Assets (dev-сборка). Возвращает абсолютный путь или null.
    /// </summary>
    private static string? ResolveIconPath(string baseDirectory, string fileName)
    {
        string rootPath = System.IO.Path.Combine(baseDirectory, fileName);
        if (System.IO.File.Exists(rootPath))
        {
            return rootPath;
        }

        string assetsPath = System.IO.Path.Combine(baseDirectory, "Assets", fileName);
        if (System.IO.File.Exists(assetsPath))
        {
            return assetsPath;
        }

        return null;
    }

    private void AddPatternButton_Click(object sender, RoutedEventArgs e)
    {
        var vm = new PatternItemViewModel(string.Empty, true, true);
        ViewModel.Patterns.Add(vm);
    }

    private void DeletePatternButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is PatternItemViewModel item)
        {
            ViewModel.Patterns.Remove(item);
        }
    }

    private static bool SafeGetBool(System.Collections.Generic.Dictionary<string, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out var val)) return false;
        return SafeGetBool(val);
    }

    private static bool SafeGetBool(object? val)
    {
        if (val == null) return false;
        if (val is bool b) return b;
        if (val is System.Text.Json.JsonElement elem)
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

    private void DragIcon_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is UIElement element)
        {
            var cursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Hand);
            typeof(UIElement).GetProperty("ProtectedCursor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                ?.SetValue(element, cursor);
        }
    }
}
