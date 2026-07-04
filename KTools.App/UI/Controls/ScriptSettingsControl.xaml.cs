// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using CommunityToolkit.WinUI.Controls;
using KTools_App.Core;

using KTools_App.Services.Contracts;
using KTools_App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace KTools_App.UI.Controls;

/// <summary>
/// Класс автогенерации элементов управления параметрами скриптов на основе декларативной схемы настроек.
/// Полностью динамически выстраивает Fluent-интерфейс параметров и связывает их с SettingsManager.
/// Поддерживает горизонтальные вкладки (NavigationView) и раздельные карточки для подгрупп параметров.
/// </summary>
public sealed partial class ScriptSettingsControl : UserControl
{
    private readonly List<(SettingField Field, FrameworkElement Element)> _generatedElements = new();
    private readonly Dictionary<string, FrameworkElement> _groupContainers = new();
    private AbstractScript? _activeScript;
    private StackPanel? _previewPanel;
    private bool _isPreviewExpanded;

    public ScriptSettingsViewModel ViewModel { get; } = App.Services.GetRequiredService<ScriptSettingsViewModel>();
    private ISettingsManager _settingsManager => App.Services.GetRequiredService<ISettingsManager>();
    private IDialogService _dialogService => App.Services.GetRequiredService<IDialogService>();

    private class GroupVisual
    {
        public StackPanel? GroupPanel { get; set; }
        public Border CardBorder { get; set; } = null!;
        public List<FrameworkElement> Elements { get; set; } = new();
    }
    private readonly List<GroupVisual> _groups = new();
    private bool _isInternalCheckBoxUpdate;

    public ScriptSettingsControl()
    {
        InitializeComponent();

        // Обеспечивает автоматический сброс фокуса с полей ввода при клике на свободную область формы
        this.PointerPressed += (s, e) =>
        {
            this.IsTabStop = true;
            this.Focus(FocusState.Programmatic);
            this.IsTabStop = false;
        };

        this.Unloaded += (s, e) =>
        {
            if (_activeScript != null)
            {
                _activeScript.FilesQueue.CollectionChanged -= OnFilesQueueCollectionChanged;
            }
        };
    }

    /// <summary>
    /// Генерирует пользовательский интерфейс параметров для указанного скрипта.
    /// </summary>
    public void GenerateSettingsUI(AbstractScript script)
    {
        if (_activeScript != null)
        {
            _activeScript.FilesQueue.CollectionChanged -= OnFilesQueueCollectionChanged;
        }
        _activeScript = script;
        ViewModel.InitializeScript(script);
        _activeScript.FilesQueue.CollectionChanged += OnFilesQueueCollectionChanged;
        _isPreviewExpanded = false;

        SettingsContainer.Children.Clear();
        _generatedElements.Clear();
        _groups.Clear();
        _groupContainers.Clear();
        SettingsNavigationView.MenuItems.Clear();

        var fullSchema = script.GetFullSettingsSchema();

        if (fullSchema == null ||
            fullSchema.Count == 0)
        {
            SettingsNavigationView.Visibility = Visibility.Collapsed;
            var noSettingsText = new TextBlock
            {
                Text = "У этого скрипта нет настраиваемых параметров.",
                FontSize = 14,
                Foreground = (Brush)Application.Current.Resources[
                    "TextFillColorSecondaryBrush"],
                Margin = new Thickness(0, 24, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            SettingsContainer.Children.Add(noSettingsText);
            return;
        }

        string settingsGroup = _settingsManager
            .GetSafeGroupName(script.Name);

        // Группируем поля по иерархии: { "ГлавнаяГруппа": { "Подгруппа": [Поля] } }
        var hierarchicalGroups = new Dictionary<string, Dictionary<string, List<SettingField>>>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in fullSchema)
        {
            var parts = field.Group.Split(':', 2);
            string mainGroup = parts[0];
            string subGroup = parts.Length > 1 ? parts[1] : "";

            if (!hierarchicalGroups.ContainsKey(mainGroup))
            {
                hierarchicalGroups[mainGroup] = new Dictionary<string, List<SettingField>>(StringComparer.OrdinalIgnoreCase);
            }
            if (!hierarchicalGroups[mainGroup].ContainsKey(subGroup))
            {
                hierarchicalGroups[mainGroup][subGroup] = new List<SettingField>();
            }
            hierarchicalGroups[mainGroup][subGroup].Add(field);
        }

        if (hierarchicalGroups.Count <= 1)
        {
            // Скрываем вкладки, если основная группа всего одна
            SettingsNavigationView.Visibility = Visibility.Collapsed;

            string mainName = hierarchicalGroups.Keys.FirstOrDefault() ?? "Настройки";
            var subs = hierarchicalGroups[mainName];

            foreach (var subPair in subs)
            {
                string cardTitle = string.IsNullOrEmpty(subPair.Key) ? mainName : subPair.Key;
                var card = CreateSettingsGroupCard(cardTitle, subPair.Value, settingsGroup);
                SettingsContainer.Children.Add(card);
            }
        }
        else
        {
            // Показываем вкладки при наличии нескольких основных групп
            SettingsNavigationView.Visibility = Visibility.Visible;

            foreach (var mainGroupPair in hierarchicalGroups)
            {
                string mainGroup = mainGroupPair.Key;
                var subs = mainGroupPair.Value;

                // Создаем StackPanel в качестве контейнера для карточек текущей основной группы
                var mainGroupPanel = new StackPanel
                {
                    Spacing = 12,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Visibility = Visibility.Collapsed // По умолчанию скрываем
                };

                foreach (var subPair in subs)
                {
                    string cardTitle = string.IsNullOrEmpty(subPair.Key) ? mainGroup : subPair.Key;
                    var card = CreateSettingsGroupCard(cardTitle, subPair.Value, settingsGroup);
                    mainGroupPanel.Children.Add(card);
                }

                SettingsContainer.Children.Add(mainGroupPanel);
                _groupContainers[mainGroup] = mainGroupPanel;

                // Создаем элемент вкладки NavigationViewItem
                var navItem = new NavigationViewItem
                {
                    Content = mainGroup,
                    Tag = mainGroup
                };

                // Присваиваем нативные иконки в соответствии с Microsoft гайдлайнами
                string mainGroupLower = mainGroup.ToLowerInvariant();
                if (mainGroupLower == "видео") navItem.Icon = new SymbolIcon(Symbol.Play);
                else if (mainGroupLower == "аудио") navItem.Icon = new SymbolIcon(Symbol.Audio);
                else if (mainGroupLower == "субтитры") navItem.Icon = new SymbolIcon(Symbol.Message);
                else if (mainGroupLower == "общие") navItem.Icon = new SymbolIcon(Symbol.Setting);
                else navItem.Icon = new SymbolIcon(Symbol.Folder);

                SettingsNavigationView.MenuItems.Add(navItem);
            }

            // Выбираем первую вкладку по умолчанию
            if (SettingsNavigationView.MenuItems.Count > 0)
            {
                var firstItem = (NavigationViewItem)SettingsNavigationView.MenuItems[0];
                SettingsNavigationView.SelectedItem = firstItem;
                string firstTag = firstItem.Tag?.ToString() ?? "";
                if (_groupContainers.TryGetValue(firstTag, out var firstPanel))
                {
                    firstPanel.Visibility = Visibility.Visible;
                }
            }
        }

        UpdateVisibility(settingsGroup);

        bool isLossless = _settingsManager.GetSetting(settingsGroup, "lossless", false);
        HandleLosslessChange(settingsGroup, isLossless);

        string rcMode = _settingsManager.GetSetting(settingsGroup, "nvenc_rc", "vbr_hq");
        HandleRcChange(settingsGroup, rcMode);
    }

    /// <summary>
    /// Переключает режим выполнения, блокируя редактирование параметров при сохранении навигации.
    /// </summary>
    /// <param name="isProcessing">Указывает, запущена ли обработка в данный момент.</param>
    public void SetProcessingMode(bool isProcessing)
    {
        SettingsContentControl.IsEnabled = !isProcessing;
    }

    /// <summary>
    /// Обработчик переключения горизонтальных вкладок настроек.
    /// </summary>
    private void SettingsNavigationView_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer is NavigationViewItem selectedItem)
        {
            string tag = selectedItem.Tag?.ToString() ?? string.Empty;

            foreach (var pair in _groupContainers)
            {
                pair.Value.Visibility = pair.Key.Equals(tag, StringComparison.OrdinalIgnoreCase)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }
    }

    /// <summary>
    /// Создает нативную карточку параметров подгруппы с вертикальной структурой полей.
    /// </summary>
    private Border CreateSettingsGroupCard(string title, List<SettingField> fields, string settingsGroup)
    {
        var cardBorder = new Border
        {
            Background = (Brush)Application.Current.Resources[
                "CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources[
                "CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0)
        };

        var cardContentStack = new StackPanel
        {
            Spacing = 16,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        cardBorder.Child = cardContentStack;

        // Заголовок карточки
        var cardTitle = new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources[
                "TextFillColorPrimaryBrush"],
            Margin = new Thickness(0, 0, 0, 4)
        };
        cardContentStack.Children.Add(cardTitle);

        var groupVisual = new GroupVisual
        {
            CardBorder = cardBorder
        };

        foreach (var field in fields)
        {
            if (field.Type == SettingType.Subtitle)
            {
                var subtitle = new TextBlock
                {
                    Text = field.Label,
                    FontSize = 13,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = (Brush)Application.Current.Resources[
                        "TextFillColorSecondaryBrush"],
                    Margin = new Thickness(0, 8, 0, 4)
                };
                cardContentStack.Children.Add(subtitle);
                continue;
            }

            if (field.Type == SettingType.KeywordList)
            {
                var keywordListContainer = CreateKeywordListContainer(settingsGroup, field);
                cardContentStack.Children.Add(keywordListContainer);
                _generatedElements.Add((field, keywordListContainer));
                groupVisual.Elements.Add(keywordListContainer);
                continue;
            }

            if (field.Type == SettingType.Checkbox)
            {
                var checkContent = new StackPanel { Spacing = 2 };
                
                checkContent.Children.Add(new TextBlock
                {
                    Text = field.Label,
                    FontSize = 14,
                    Foreground = (Brush)Application.Current.Resources[
                        "TextFillColorPrimaryBrush"]
                });

                if (!string.IsNullOrEmpty(field.Comment))
                {
                    checkContent.Children.Add(new TextBlock
                    {
                        Text = field.Comment,
                        FontSize = 12,
                        Foreground = (Brush)Application.Current.Resources[
                            "TextFillColorSecondaryBrush"]
                    });
                }

                var checkBox = new CheckBox
                {
                    Content = checkContent,
                    IsChecked = _settingsManager.GetSetting(
                        settingsGroup,
                        field.Key,
                        field.DefaultValue is bool b && b),
                    VerticalAlignment = VerticalAlignment.Center
                };

                checkBox.Checked += (s, e) =>
                {
                    if (_isInternalCheckBoxUpdate) return;
                    _settingsManager.SetSetting(
                        settingsGroup, field.Key, true);
                    UpdateVisibility(settingsGroup);

                    UpdatePreview();

                    if (field.Key == "lossless")
                    {
                        HandleLosslessChange(settingsGroup, true);
                    }
                    else if (field.Key == "auto_bitrate")
                    {
                        HandleAutoBitrateChange(settingsGroup);
                    }
                };

                checkBox.Unchecked += async (s, e) =>
                {
                    if (_isInternalCheckBoxUpdate) return;

                    if (field.RequiresWarning)
                    {
                        var confirmed = await _dialogService.ShowConfirmationAsync(
                            field.WarningTitle ?? "Предупреждение",
                            field.WarningText ?? "Вы действительно хотите отключить этот параметр?",
                            "Я понимаю",
                            "Отмена");

                        if (!confirmed)
                        {
                            _isInternalCheckBoxUpdate = true;
                            checkBox.IsChecked = true;
                            _isInternalCheckBoxUpdate = false;
                            return;
                        }
                    }

                    _settingsManager.SetSetting(
                        settingsGroup, field.Key, false);
                    UpdateVisibility(settingsGroup);

                    UpdatePreview();

                    if (field.Key == "lossless")
                    {
                        HandleLosslessChange(settingsGroup, false);
                    }
                    else if (field.Key == "auto_bitrate")
                    {
                        HandleAutoBitrateChange(settingsGroup);
                    }
                };

                cardContentStack.Children.Add(checkBox);
                _generatedElements.Add((field, checkBox));
                groupVisual.Elements.Add(checkBox);
                continue;
            }

            // Нативная строка параметра (метка слева, контрол справа)
            var rowGrid = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center
            };
            
            bool isRenameField = field.Key == "LocalRenameSearch" || field.Key == "LocalRenameReplace";

            rowGrid.ColumnDefinitions.Add(new ColumnDefinition 
                { Width = isRenameField ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition 
                { Width = isRenameField ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });

            var textStack = new StackPanel 
            { 
                Spacing = 2, 
                Margin = new Thickness(0, 0, 16, 0),
                VerticalAlignment = VerticalAlignment.Center 
            };

            textStack.Children.Add(new TextBlock
            {
                Text = field.Label,
                FontSize = 14,
                Foreground = (Brush)Application.Current.Resources[
                    "TextFillColorPrimaryBrush"],
                VerticalAlignment = VerticalAlignment.Center
            });

            if (!string.IsNullOrEmpty(field.Comment))
            {
                textStack.Children.Add(new TextBlock
                {
                    Text = field.Comment,
                    FontSize = 12,
                    Foreground = (Brush)Application.Current.Resources[
                        "TextFillColorSecondaryBrush"]
                });
            }
            
            Grid.SetColumn(textStack, 0);
            rowGrid.Children.Add(textStack);

            FrameworkElement? inputControl = null;

            switch (field.Type)
            {
                case SettingType.Text:
                    var textBox = new TextBox
                    {
                        Text = _settingsManager.GetSetting(
                            settingsGroup,
                            field.Key,
                            field.DefaultValue?.ToString() ?? string.Empty),
                        PlaceholderText = field.PlaceholderText ?? string.Empty,
                        Width = 250,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    textBox.TextChanged += (s, e) =>
                    {
                        _settingsManager.SetSetting(
                            settingsGroup, field.Key, textBox.Text);
                        if (isRenameField)
                        {
                            UpdatePreview();
                        }
                    };
                    textBox.LostFocus += (s, e) =>
                    {
                        _settingsManager.SetSetting(
                            settingsGroup, field.Key, textBox.Text);
                        UpdateVisibility(settingsGroup);
                        if (isRenameField)
                        {
                            UpdatePreview();
                        }
                    };
                    textBox.KeyDown += (s, e) =>
                    {
                        if (e.Key == Windows.System.VirtualKey.Enter)
                        {
                            this.IsTabStop = true;
                            this.Focus(FocusState.Programmatic);
                            this.IsTabStop = false;
                            e.Handled = true;
                        }
                    };

                    if (isRenameField)
                    {
                        var containerGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Right };
                        containerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        containerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                        Grid.SetColumn(textBox, 0);
                        containerGrid.Children.Add(textBox);

                        var helpBtn = new Button
                        {
                            Content = "\uE946",
                            FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
                            Margin = new Thickness(8, 0, 0, 0),
                            Width = 32,
                            Height = 32,
                            Padding = new Thickness(0),
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        Grid.SetColumn(helpBtn, 1);
                        containerGrid.Children.Add(helpBtn);

                        if (field.Key == "LocalRenameSearch")
                        {
                            AttachSearchHelpFlyout(helpBtn, textBox);
                        }
                        else
                        {
                            AttachReplaceHelpFlyout(helpBtn, textBox);
                        }

                        inputControl = containerGrid;
                    }
                    else
                    {
                        inputControl = textBox;
                    }
                    break;

                case SettingType.Int:
                    var numberBox = new NumberBox
                    {
                        Value = _settingsManager.GetSetting(
                            settingsGroup,
                            field.Key,
                            field.DefaultValue is int vInt ? vInt : 0),
                        Width = 160,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        SpinButtonPlacementMode = 
                            NumberBoxSpinButtonPlacementMode.Inline,
                        SmallChange = 1,
                        LargeChange = 5,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    numberBox.ValueChanged += (s, e) =>
                    {
                        if (!double.IsNaN(numberBox.Value))
                        {
                            _settingsManager.SetSetting(
                                settingsGroup,
                                field.Key,
                                (int)numberBox.Value);
                            UpdateVisibility(settingsGroup);
                            HandleIntSettingChanged(settingsGroup, field.Key, (int)numberBox.Value);
                        }
                    };
                    numberBox.KeyDown += (s, e) =>
                    {
                        if (e.Key == Windows.System.VirtualKey.Enter)
                        {
                            this.IsTabStop = true;
                            this.Focus(FocusState.Programmatic);
                            this.IsTabStop = false;
                            e.Handled = true;
                        }
                    };
                    inputControl = numberBox;
                    break;

                case SettingType.Combo:
                    var comboBox = new ComboBox
                    {
                        Width = 160,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    foreach (var opt in field.Options)
                    {
                        comboBox.Items.Add(opt);
                    }
                    
                    string defaultValStr = field.DefaultValue?
                        .ToString() ?? string.Empty;
                    string currentSelection = _settingsManager
                        .GetSetting(
                            settingsGroup,
                            field.Key,
                            defaultValStr);
                    
                    comboBox.SelectionChanged += (s, e) =>
                    {
                        if (comboBox.SelectedItem != null)
                        {
                            string selectedVal = comboBox.SelectedItem.ToString() ?? string.Empty;
                            _settingsManager.SetSetting(
                                settingsGroup,
                                field.Key,
                                selectedVal);
                            UpdateVisibility(settingsGroup);

                            if (field.Key == "nvenc_rc")
                            {
                                HandleRcChange(settingsGroup, selectedVal);
                                HandleAutoBitrateChange(settingsGroup);
                            }
                            
                            UpdatePreview();
                        }
                    };

                    string matchedOption = field.Options
                        .FirstOrDefault(opt => opt.Equals(
                            currentSelection,
                            StringComparison.OrdinalIgnoreCase))
                        ?? field.Options.FirstOrDefault()
                        ?? defaultValStr;

                    comboBox.SelectedItem = matchedOption;
                    
                    if (comboBox.SelectedIndex == -1 && 
                        comboBox.Items.Count > 0)
                    {
                        comboBox.SelectedIndex = 0;
                    }
                    inputControl = comboBox;
                    break;
            }

            if (inputControl != null)
            {
                Grid.SetColumn(inputControl, 1);
                rowGrid.Children.Add(inputControl);
                cardContentStack.Children.Add(rowGrid);
                _generatedElements.Add((field, rowGrid));
                groupVisual.Elements.Add(rowGrid);
            }
        }

        if (title.Equals("Переименование", StringComparison.OrdinalIgnoreCase))
        {
            var separator = new MenuFlyoutSeparator { Margin = new Thickness(0, 8, 0, 8) };
            cardContentStack.Children.Add(separator);

            var previewTitle = new TextBlock
            {
                Text = "Предпросмотр переименования",
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
                Margin = new Thickness(0, 0, 0, 4)
            };
            cardContentStack.Children.Add(previewTitle);

            _previewPanel = new StackPanel
            {
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            cardContentStack.Children.Add(_previewPanel);

            UpdatePreview();
        }

        _groups.Add(groupVisual);
        return cardBorder;
    }

    /// <summary>
    /// Обновляет видимость полей настроек и групп на основе управляющих условий VisibleIf.
    /// </summary>
    private void UpdateVisibility(string settingsGroup)
    {
        // 1. Обновляем видимость отдельных элементов настроек
        foreach (var item in _generatedElements)
        {
            if (item.Field.VisibilityConditions != null && item.Field.VisibilityConditions.Count > 0)
            {
                bool isCondVisible = true;
                foreach (var cond in item.Field.VisibilityConditions)
                {
                    string condValue = _settingsManager.GetSetting(
                        settingsGroup,
                        cond.Key,
                        string.Empty);

                    bool matches = false;
                    foreach (var val in cond.Values)
                    {
                        if (val.Equals(condValue, StringComparison.OrdinalIgnoreCase))
                        {
                            matches = true;
                            break;
                        }
                    }

                    if (cond.Negate)
                    {
                        matches = !matches;
                    }

                    if (!matches)
                    {
                        isCondVisible = false;
                        break;
                    }
                }

                item.Element.Visibility = isCondVisible ? Visibility.Visible : Visibility.Collapsed;
                continue;
            }

            if (string.IsNullOrEmpty(item.Field.VisibleIfKey) || item.Field.VisibleIfValues == null)
            {
                item.Element.Visibility = Visibility.Visible;
                continue;
            }

            string controlValue = _settingsManager.GetSetting(
                settingsGroup,
                item.Field.VisibleIfKey,
                string.Empty);

            bool isVisible = false;
            foreach (var val in item.Field.VisibleIfValues)
            {
                if (val.Equals(controlValue, StringComparison.OrdinalIgnoreCase))
                {
                    isVisible = true;
                    break;
                }
            }

            item.Element.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        // 2. Обновляем видимость целых групп настроек (StackPanel) и карточек
        foreach (var group in _groups)
        {
            bool hasVisibleElements = group.Elements.Any(e => e.Visibility == Visibility.Visible);
            
            if (group.GroupPanel != null)
            {
                group.GroupPanel.Visibility = hasVisibleElements ? Visibility.Visible : Visibility.Collapsed;
            }
            
            if (group.CardBorder != null)
            {
                group.CardBorder.Visibility = hasVisibleElements ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    /// <summary>
    /// Создает визуальный контейнер для редактирования списков ключевых слов (KeywordList).
    /// Предоставляет пользователю полноширинную область со списком элементов, чекбоксами активности,
    /// текстовыми полями для ключевых слов и кнопками удаления, а также кнопкой добавления новых элементов.
    /// Все изменения синхронизируются в реальном времени с SettingsManager.
    /// </summary>
    private FrameworkElement CreateKeywordListContainer(string settingsGroup, SettingField field)
    {
        var mainStack = new StackPanel
        {
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 8)
        };

        // Заголовок и всплывающий комментарий
        var labelStack = new StackPanel { Spacing = 2 };
        labelStack.Children.Add(new TextBlock
        {
            Text = field.Label,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]
        });

        if (!string.IsNullOrEmpty(field.Comment))
        {
            labelStack.Children.Add(new TextBlock
            {
                Text = field.Comment,
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });
        }
        mainStack.Children.Add(labelStack);

        // Контейнер для списка строк ключевых слов
        var itemsStack = new StackPanel
        {
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        mainStack.Children.Add(itemsStack);

        // Кнопка для добавления новой строки в список ключевых слов
        var addButton = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = {
                    new FontIcon { Glyph = "\uE710", FontSize = 12 }, // Иконка "+"
                    new TextBlock { Text = "Добавить слово", FontSize = 12 }
                }
            },
            Style = (Style)Application.Current.Resources["DefaultButtonStyle"],
            Margin = new Thickness(0, 4, 0, 0)
        };
        mainStack.Children.Add(addButton);

        // Извлекаем текущий список из кэша настроек или берем значение по умолчанию
        var defaultList = field.DefaultValue as List<Dictionary<string, object>> ?? new List<Dictionary<string, object>>();
        var currentList = _settingsManager.GetSetting(settingsGroup, field.Key, defaultList);

        // Локальная функция для сохранения списка в SettingsManager
        void SaveList()
        {
            var listToSave = new List<Dictionary<string, object>>();
            foreach (Grid row in itemsStack.Children.OfType<Grid>())
            {
                var checkBox = row.Children.OfType<CheckBox>().FirstOrDefault();
                var textBox = row.Children.OfType<TextBox>().FirstOrDefault();
                if (checkBox != null && textBox != null)
                {
                    listToSave.Add(new Dictionary<string, object>
                    {
                        { "word", textBox.Text },
                        { "active", checkBox.IsChecked == true }
                    });
                }
            }
            _settingsManager.SetSetting(settingsGroup, field.Key, listToSave);
        }

        // Вспомогательная локальная функция добавления строки ключевого слова в UI
        void AddKeywordRow(string word, bool isActive)
        {
            var rowGrid = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 2, 0, 2)
            };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) }); // Галочка
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Поле ввода
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Кнопка удаления

            var checkBox = new CheckBox
            {
                IsChecked = isActive,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0),
                Padding = new Thickness(0),
                MinWidth = 0,
                MinHeight = 0
            };
            checkBox.Checked += (s, e) => SaveList();
            checkBox.Unchecked += (s, e) => SaveList();
            Grid.SetColumn(checkBox, 0);
            rowGrid.Children.Add(checkBox);

            var textBox = new TextBox
            {
                Text = word,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                Height = 32,
                Margin = new Thickness(0, 0, 8, 0)
            };
            textBox.TextChanged += (s, e) => SaveList();
            Grid.SetColumn(textBox, 1);
            rowGrid.Children.Add(textBox);

            var deleteButton = new Button
            {
                Content = new FontIcon { Glyph = "\uE74D", FontSize = 12 }, // Иконка корзины
                Style = (Style)Application.Current.Resources["DefaultButtonStyle"],
                VerticalAlignment = VerticalAlignment.Center,
                Width = 32,
                Height = 32,
                Padding = new Thickness(0)
            };
            deleteButton.Click += (s, e) =>
            {
                itemsStack.Children.Remove(rowGrid);
                SaveList();
            };
            Grid.SetColumn(deleteButton, 2);
            rowGrid.Children.Add(deleteButton);

            itemsStack.Children.Add(rowGrid);
        }

        // Отрисовка сохраненных элементов
        if (currentList != null)
        {
            foreach (var item in currentList)
            {
                string word = item.TryGetValue("word", out var w) ? w?.ToString() ?? "" : "";
                bool isActive = item.TryGetValue("active", out var act) && SafeGetBool(act);
                AddKeywordRow(word, isActive);
            }
        }

        // Действие при клике на кнопку добавления
        addButton.Click += (s, e) =>
        {
            AddKeywordRow("", true);
            SaveList();
        };

        return mainStack;
    }

    /// <summary>
    /// Безопасно извлекает булево значение из различных типов JsonElement и других объектов.
    /// </summary>
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
            if (elem.ValueKind == System.Text.Json.JsonValueKind.Number)
            {
                return elem.TryGetInt32(out var val) && val != 0;
            }
        }
        try
        {
            return Convert.ToBoolean(obj);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Обрабатывает изменение режима Lossless. 
    /// Если режим включен, пресет nvenc_preset переключается на "p1" и блокируется.
    /// Если выключен, пресет nvenc_preset разблокируется.
    /// </summary>
    private void HandleLosslessChange(string settingsGroup, bool isLossless)
    {
        var presetTuple = _generatedElements.FirstOrDefault(x => x.Field.Key == "nvenc_preset");
        if (presetTuple.Element is Grid grid)
        {
            var comboBox = grid.Children.OfType<ComboBox>().FirstOrDefault();
            if (comboBox != null)
            {
                if (isLossless)
                {
                    comboBox.SelectedItem = "p1";
                    comboBox.IsEnabled = false;
                    _settingsManager.SetSetting(settingsGroup, "nvenc_preset", "p1");
                }
                else
                {
                    comboBox.IsEnabled = true;
                }
            }
        }
    }

    /// <summary>
    /// Обрабатывает изменение режима управления битрейтом (nvenc_rc).
    /// Если выбран режим CBR, авторасчет битрейта форсируется в true и блокируется,
    /// так как в CBR минимальный и максимальный битрейты должны быть равны целевому.
    /// В остальных режимах выбор авторасчета разблокируется.
    /// </summary>
    private void HandleRcChange(string settingsGroup, string rcMode)
    {
        var autoBitrateTuple = _generatedElements.FirstOrDefault(x => x.Field.Key == "auto_bitrate");
        if (autoBitrateTuple.Element is CheckBox checkBox)
        {
            if (rcMode.Equals("cbr", StringComparison.OrdinalIgnoreCase))
            {
                _isInternalCheckBoxUpdate = true;
                checkBox.IsChecked = true;
                _isInternalCheckBoxUpdate = false;

                _settingsManager.SetSetting(settingsGroup, "auto_bitrate", true);
                checkBox.IsEnabled = false;
            }
            else
            {
                checkBox.IsEnabled = true;
            }
        }
    }

    /// <summary>
    /// Обрабатывает изменение параметра авторасчета битрейта (auto_bitrate).
    /// Блокирует или разблокирует поля min_bitrate, max_bitrate и bufsize.
    /// </summary>
    private void HandleAutoBitrateChange(string settingsGroup)
    {
        bool isAuto = _settingsManager.GetSetting(settingsGroup, "auto_bitrate", true);
        string[] dependentKeys = { "min_bitrate", "max_bitrate", "bufsize" };

        foreach (var key in dependentKeys)
        {
            var tuple = _generatedElements.FirstOrDefault(x => x.Field.Key == key);
            if (tuple.Element is Grid grid)
            {
                var numberBox = grid.Children.OfType<NumberBox>().FirstOrDefault();
                if (numberBox != null)
                {
                    numberBox.IsEnabled = !isAuto;
                }
            }
        }

        // Если включен авторасчет, производим перерасчет на базе текущего v_bitrate
        if (isAuto)
        {
            RecalculateBitrates(settingsGroup);
        }
    }

    /// <summary>
    /// Производит автоматический расчет битрейтов по формулам:
    /// min = target, max = target * 2, buf = max * 2.
    /// Результаты сохраняются в конфигурации и обновляются в UI.
    /// </summary>
    private void RecalculateBitrates(string settingsGroup, int? targetBitrate = null)
    {
        int vBr = targetBitrate ?? _settingsManager.GetSetting(settingsGroup, "v_bitrate", 4000);
        string rc = _settingsManager.GetSetting(settingsGroup, "nvenc_rc", "vbr_hq");

        int minBr;
        int maxBr;
        int bufSize;

        if (rc.Equals("cbr", StringComparison.OrdinalIgnoreCase))
        {
            minBr = vBr;
            maxBr = vBr;
            bufSize = vBr * 2;
        }
        else
        {
            minBr = vBr;
            maxBr = vBr * 2;
            bufSize = maxBr * 2;
        }

        // Сохраняем значения в SettingsManager
        _settingsManager.SetSetting(settingsGroup, "min_bitrate", minBr);
        _settingsManager.SetSetting(settingsGroup, "max_bitrate", maxBr);
        _settingsManager.SetSetting(settingsGroup, "bufsize", bufSize);

        // Обновляем визуальные значения в полях NumberBox на форме
        UpdateNumberBoxValue("min_bitrate", minBr);
        UpdateNumberBoxValue("max_bitrate", maxBr);
        UpdateNumberBoxValue("bufsize", bufSize);
    }

    /// <summary>
    /// Вспомогательный метод для программного обновления значения NumberBox в UI.
    /// </summary>
    private void UpdateNumberBoxValue(string key, int value)
    {
        var tuple = _generatedElements.FirstOrDefault(x => x.Field.Key == key);
        if (tuple.Element is Grid grid)
        {
            var numberBox = grid.Children.OfType<NumberBox>().FirstOrDefault();
            if (numberBox != null)
            {
                numberBox.Value = value;
            }
        }
    }

    /// <summary>
    /// Вызывается при изменении целочисленного параметра в интерфейсе.
    /// Если изменился v_bitrate при включенном авторасчете, запускается перерасчет.
    /// </summary>
    private void HandleIntSettingChanged(string settingsGroup, string key, int newValue)
    {
        if (key == "v_bitrate")
        {
            bool isAuto = _settingsManager.GetSetting(settingsGroup, "auto_bitrate", true);
            if (isAuto)
            {
                RecalculateBitrates(settingsGroup, newValue);
            }
            else
            {
                // При ручном вводе корректируем min и max, если они вышли за новые границы целевого битрейта
                int minBr = _settingsManager.GetSetting(settingsGroup, "min_bitrate", newValue);
                int maxBr = _settingsManager.GetSetting(settingsGroup, "max_bitrate", newValue);

                if (minBr > newValue)
                {
                    _settingsManager.SetSetting(settingsGroup, "min_bitrate", newValue);
                    UpdateNumberBoxValue("min_bitrate", newValue);
                }
                if (maxBr < newValue)
                {
                    _settingsManager.SetSetting(settingsGroup, "max_bitrate", newValue);
                    UpdateNumberBoxValue("max_bitrate", newValue);
                }
            }
        }
        else if (key == "min_bitrate")
        {
            bool isAuto = _settingsManager.GetSetting(settingsGroup, "auto_bitrate", true);
            if (!isAuto)
            {
                int vBr = _settingsManager.GetSetting(settingsGroup, "v_bitrate", 4000);
                if (newValue > vBr)
                {
                    // Минимальный битрейт не может быть больше целевого
                    _settingsManager.SetSetting(settingsGroup, "min_bitrate", vBr);
                    UpdateNumberBoxValue("min_bitrate", vBr);
                }
            }
        }
        else if (key == "max_bitrate")
        {
            bool isAuto = _settingsManager.GetSetting(settingsGroup, "auto_bitrate", true);
            if (!isAuto)
            {
                int vBr = _settingsManager.GetSetting(settingsGroup, "v_bitrate", 4000);
                if (newValue < vBr)
                {
                    // Максимальный битрейт не может быть меньше целевого
                    _settingsManager.SetSetting(settingsGroup, "max_bitrate", vBr);
                    UpdateNumberBoxValue("max_bitrate", vBr);
                }
            }
        }
    }

    private void AttachSearchHelpFlyout(Button button, TextBox textBox)
    {
        var flyout = new Flyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight };
        
        var mainStack = new StackPanel { Spacing = 6, Width = 280 };
        
        var title = new TextBlock 
        { 
            Text = "Шаблоны поиска", 
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, 
            FontSize = 14, 
            Margin = new Thickness(0, 0, 0, 4) 
        };
        var desc = new TextBlock 
        { 
            Text = "Нажмите на шаблон, чтобы скопировать:", 
            FontSize = 12, 
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], 
            Margin = new Thickness(0, 0, 0, 8) 
        };
        
        mainStack.Children.Add(title);
        mainStack.Children.Add(desc);
        
        var variables = _settingsManager.SearchTemplates;
        
        foreach (var item in variables)
        {
            if (string.IsNullOrEmpty(item.Pattern)) continue;

            var rowGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var insertBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uE710", FontSize = 10 },
                Style = (Style)Application.Current.Resources["DefaultButtonStyle"],
                Width = 32,
                Height = 32,
                Margin = new Thickness(0, 0, 6, 0),
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center
            };
            insertBtn.Click += (s, e) =>
            {
                textBox.Text += item.Pattern;
                textBox.Focus(FocusState.Programmatic);
            };
            Grid.SetColumn(insertBtn, 0);
            rowGrid.Children.Add(insertBtn);

            var btn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Height = 32,
                VerticalAlignment = VerticalAlignment.Center
            };
            
            var btnGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
            var rowStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            rowStack.Children.Add(new TextBlock 
            { 
                Text = item.Pattern, 
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, 
                Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"] 
            });
            rowStack.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(item.Description) ? "" : $"— {CleanDescription(item.Description)}", FontSize = 12 });
            
            var copiedText = new TextBlock
            {
                Text = "Скопировано!",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            
            btnGrid.Children.Add(rowStack);
            btnGrid.Children.Add(copiedText);
            btn.Content = btnGrid;
            
            btn.Click += (s, e) =>
            {
                var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dataPackage.SetText(item.Pattern);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
                
                rowStack.Opacity = 0;
                copiedText.Visibility = Visibility.Visible;
                
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                timer.Tick += (s2, ev) =>
                {
                    copiedText.Visibility = Visibility.Collapsed;
                    rowStack.Opacity = 1;
                    timer.Stop();
                };
                timer.Start();
            };
            
            Grid.SetColumn(btn, 1);
            rowGrid.Children.Add(btn);

            mainStack.Children.Add(rowGrid);
        }
        
        flyout.Content = mainStack;
        button.Flyout = flyout;
    }

    /// <summary>
    /// Создает и привязывает всплывающее меню (Flyout) с шаблонами автозамены, копируемыми при клике.
    /// </summary>
    private void AttachReplaceHelpFlyout(Button button, TextBox textBox)
    {
        var flyout = new Flyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight };
        
        var mainStack = new StackPanel { Spacing = 6, Width = 280 };
        
        var title = new TextBlock 
        { 
            Text = "Переменные замены", 
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, 
            FontSize = 14, 
            Margin = new Thickness(0, 0, 0, 4) 
        };
        var desc = new TextBlock 
        { 
            Text = "Нажмите на шаблон, чтобы скопировать:", 
            FontSize = 12, 
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], 
            Margin = new Thickness(0, 0, 0, 8) 
        };
        
        mainStack.Children.Add(title);
        mainStack.Children.Add(desc);
        
        var variables = _settingsManager.ReplaceTemplates;
        
        foreach (var item in variables)
        {
            if (string.IsNullOrEmpty(item.Pattern)) continue;

            var rowGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var insertBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uE710", FontSize = 10 },
                Style = (Style)Application.Current.Resources["DefaultButtonStyle"],
                Width = 32,
                Height = 32,
                Margin = new Thickness(0, 0, 6, 0),
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center
            };
            insertBtn.Click += (s, e) =>
            {
                textBox.Text += item.Pattern;
                textBox.Focus(FocusState.Programmatic);
            };
            Grid.SetColumn(insertBtn, 0);
            rowGrid.Children.Add(insertBtn);

            var btn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Height = 32,
                VerticalAlignment = VerticalAlignment.Center
            };
            
            var btnGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
            var rowStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            rowStack.Children.Add(new TextBlock 
            { 
                Text = item.Pattern, 
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, 
                Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"] 
            });
            rowStack.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(item.Description) ? "" : $"— {CleanDescription(item.Description)}", FontSize = 12 });
            
            var copiedText = new TextBlock
            {
                Text = "Скопировано!",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            
            btnGrid.Children.Add(rowStack);
            btnGrid.Children.Add(copiedText);
            btn.Content = btnGrid;
            
            btn.Click += (s, e) =>
            {
                var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dataPackage.SetText(item.Pattern);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
                
                rowStack.Opacity = 0;
                copiedText.Visibility = Visibility.Visible;
                
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                timer.Tick += (s2, ev) =>
                {
                    copiedText.Visibility = Visibility.Collapsed;
                    rowStack.Opacity = 1;
                    timer.Stop();
                };
                timer.Start();
            };
            
            Grid.SetColumn(btn, 1);
            rowGrid.Children.Add(btn);

            mainStack.Children.Add(rowGrid);
        }
        
        flyout.Content = mainStack;
        button.Flyout = flyout;
    }

    private static string CleanDescription(string desc)
    {
        if (string.IsNullOrEmpty(desc)) return string.Empty;
        var result = desc.Trim();
        if (result.StartsWith("—"))
        {
            result = result.Substring(1).Trim();
        }
        else if (result.StartsWith("-"))
        {
            result = result.Substring(1).Trim();
        }
        return result;
    }

    private void OnFilesQueueCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        App.CurrentMainWindow?.DispatcherQueue?.TryEnqueue(() =>
        {
            UpdatePreview();
        });
    }

    private void UpdatePreview()
    {
        if (_previewPanel == null || _activeScript == null) return;

        _previewPanel.Children.Clear();

        var files = _activeScript.FilesQueue;
        if (files == null || files.Count == 0)
        {
            _previewPanel.Children.Add(new TextBlock
            {
                Text = "Добавьте файлы в очередь, чтобы увидеть предпросмотр переименования",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                FontStyle = Windows.UI.Text.FontStyle.Italic,
                Margin = new Thickness(0, 4, 0, 0)
            });
            return;
        }

        string settingsGroup = _settingsManager.GetSafeGroupName(_activeScript.Name);
        bool renameEnabled = false;
        string pattern = "";

        bool localOverride = _settingsManager.GetSetting(settingsGroup, "LocalRenameOverride", false);
        if (localOverride)
        {
            pattern = _settingsManager.GetSetting(settingsGroup, "LocalRenameSearch", string.Empty);
            renameEnabled = !string.IsNullOrEmpty(pattern);
        }
        else
        {
            renameEnabled = _settingsManager.RenameEnableRegex;
            pattern = _settingsManager.RenameRegexSearch;
        }

        if (!renameEnabled || string.IsNullOrEmpty(pattern))
        {
            _previewPanel.Children.Add(new TextBlock
            {
                Text = "Переименование выключено или шаблон поиска пуст",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                FontStyle = Windows.UI.Text.FontStyle.Italic,
                Margin = new Thickness(0, 4, 0, 0)
            });
            return;
        }

        int maxFiles = _isPreviewExpanded ? files.Count : 5;
        var previewStack = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Stretch };

        for (int i = 0; i < Math.Min(files.Count, maxFiles); i++)
        {
            var file = files[i];
            string previewOutPath = _activeScript.GetPreviewOutputPath(file.FilePath, file.FilePath, i + 1);
            string newName = Path.GetFileName(previewOutPath);

            var rowGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var oldText = new TextBlock
            {
                Text = file.FileName,
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            Grid.SetColumn(oldText, 0);
            rowGrid.Children.Add(oldText);

            var arrow = new TextBlock
            {
                Text = " ➜ ",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                Margin = new Thickness(8, 0, 8, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            Grid.SetColumn(arrow, 1);
            rowGrid.Children.Add(arrow);

            var newText = new TextBlock
            {
                Text = newName,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Left
            };

            if (newName != file.FileName)
            {
                if (Application.Current.Resources.TryGetValue("SuccessStrokeBrush", out var brushObj) && brushObj is Brush successBrush)
                {
                    newText.Foreground = successBrush;
                }
                else
                {
                    newText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 107, 194, 133));
                }
                newText.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            }
            else
            {
                newText.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
            }

            Grid.SetColumn(newText, 2);
            rowGrid.Children.Add(newText);

            previewStack.Children.Add(rowGrid);
        }

        if (files.Count > 5 && !_isPreviewExpanded)
        {
            // Кнопка-ссылка для разворачивания всего списка
            var expandBtn = new HyperlinkButton
            {
                Content = $"... и ещё {files.Count - 5} файлов",
                FontSize = 11,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 2, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            expandBtn.Click += (s, e) =>
            {
                _isPreviewExpanded = true;
                UpdatePreview();
            };
            previewStack.Children.Add(expandBtn);
        }
        else if (_isPreviewExpanded && files.Count > 5)
        {
            // Кнопка сворачивания обратно
            var collapseBtn = new HyperlinkButton
            {
                Content = "Свернуть",
                FontSize = 11,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 2, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            collapseBtn.Click += (s, e) =>
            {
                _isPreviewExpanded = false;
                UpdatePreview();
            };
            previewStack.Children.Add(collapseBtn);
        }

        _previewPanel.Children.Add(previewStack);
    }
}
