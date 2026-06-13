// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using CommunityToolkit.WinUI.Controls;
using KTools_App.Core;

namespace KTools_App.UI.Controls;

/// <summary>
/// Класс автогенерации элементов управления параметрами скриптов на основе декларативной схемы настроек.
/// Полностью динамически выстраивает Fluent-интерфейс параметров и связывает их с SettingsManager.
/// </summary>
public sealed partial class ScriptSettingsControl : UserControl
{
    private readonly List<(SettingField Field, FrameworkElement Element)> _generatedElements = new();

    private class GroupVisual
    {
        public StackPanel GroupPanel { get; set; } = null!;
        public Border CardBorder { get; set; } = null!;
        public List<FrameworkElement> Elements { get; set; } = new();
    }
    private readonly List<GroupVisual> _groups = new();
    private bool _isInternalCheckBoxUpdate;

    public ScriptSettingsControl()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Генерирует пользовательский интерфейс параметров для указанного скрипта.
    /// </summary>
    public void GenerateSettingsUI(AbstractScript script)
    {
        SettingsContainer.Children.Clear();
        _generatedElements.Clear();
        _groups.Clear();

        if (script.SettingsSchema == null ||
            script.SettingsSchema.Count == 0)
        {
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

        string settingsGroup = SettingsManager.Instance
            .GetSafeGroupName(script.Name);

        // Группируем настройки по имени группы (например, "Кодирование")
        var groupedFields = script.SettingsSchema
            .GroupBy(f => f.Group.Split(':')[0])
            .ToList();

        foreach (var group in groupedFields)
        {
            // Создаем визуальный контейнер для группы настроек
            var groupPanel = new StackPanel
            {
                Spacing = 6,
                Margin = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            // Заголовок группы
            var groupTitle = new TextBlock
            {
                Text = group.Key,
                FontSize = 16,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = (Brush)Application.Current.Resources[
                    "TextFillColorPrimaryBrush"],
                Margin = new Thickness(0)
            };
            groupPanel.Children.Add(groupTitle);

            // Создаем единую нативную карточку-контейнер для всей группы параметров
            var cardBorder = new Border
            {
                Background = (Brush)Application.Current.Resources[
                    "CardBackgroundFillColorDefaultBrush"],
                BorderBrush = (Brush)Application.Current.Resources[
                    "CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 16, 16, 16),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0)
            };

            var cardContentStack = new StackPanel
            {
                Spacing = 16,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            cardBorder.Child = cardContentStack;

            var groupVisual = new GroupVisual
            {
                GroupPanel = groupPanel,
                CardBorder = cardBorder
            };

            // Наполняем карточку параметрами группы
            foreach (var field in group)
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
                        IsChecked = SettingsManager.Instance.GetSetting(
                            settingsGroup,
                            field.Key,
                            field.DefaultValue is bool b && b),
                        VerticalAlignment = VerticalAlignment.Center
                    };

                    checkBox.Checked += (s, e) =>
                    {
                        if (_isInternalCheckBoxUpdate) return;
                        SettingsManager.Instance.SetSetting(
                            settingsGroup, field.Key, true);
                        UpdateVisibility(settingsGroup);
                    };

                    checkBox.Unchecked += async (s, e) =>
                    {
                        if (_isInternalCheckBoxUpdate) return;

                        if (field.RequiresWarning)
                        {
                            var xamlRoot = this.XamlRoot ?? checkBox.XamlRoot;
                            if (xamlRoot != null)
                            {
                                var dialog = new ContentDialog
                                {
                                    Title = field.WarningTitle ?? "Предупреждение",
                                    Content = field.WarningText ?? "Вы действительно хотите отключить этот параметр?",
                                    PrimaryButtonText = "Я понимаю",
                                    CloseButtonText = "Отмена",
                                    XamlRoot = xamlRoot
                                };

                                var result = await dialog.ShowAsync();
                                if (result != ContentDialogResult.Primary)
                                {
                                    _isInternalCheckBoxUpdate = true;
                                    checkBox.IsChecked = true;
                                    _isInternalCheckBoxUpdate = false;
                                    return;
                                }
                            }
                        }

                        SettingsManager.Instance.SetSetting(
                            settingsGroup, field.Key, false);
                        UpdateVisibility(settingsGroup);
                    };

                    cardContentStack.Children.Add(checkBox);
                    _generatedElements.Add((field, checkBox));
                    groupVisual.Elements.Add(checkBox);
                    continue;
                }

                // Для остальных типов (Text, Int, Combo) создаем строку Grid
                var rowGrid = new Grid
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Center
                };
                
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition 
                    { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition 
                    { Width = GridLength.Auto });

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
                            Text = SettingsManager.Instance.GetSetting(
                                settingsGroup,
                                field.Key,
                                field.DefaultValue?.ToString() ?? string.Empty),
                            Width = 200,
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        textBox.LostFocus += (s, e) =>
                        {
                            SettingsManager.Instance.SetSetting(
                                settingsGroup, field.Key, textBox.Text);
                            UpdateVisibility(settingsGroup);
                        };
                        inputControl = textBox;
                        break;

                    case SettingType.Int:
                        var numberBox = new NumberBox
                        {
                            Value = SettingsManager.Instance.GetSetting(
                                settingsGroup,
                                field.Key,
                                field.DefaultValue is int vInt ? vInt : 0),
                            Width = 120,
                            SpinButtonPlacementMode = 
                                NumberBoxSpinButtonPlacementMode.Compact,
                            SmallChange = 1,
                            LargeChange = 5,
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        numberBox.ValueChanged += (s, e) =>
                        {
                            if (!double.IsNaN(numberBox.Value))
                            {
                                SettingsManager.Instance.SetSetting(
                                    settingsGroup,
                                    field.Key,
                                    (int)numberBox.Value);
                                UpdateVisibility(settingsGroup);
                            }
                        };
                        inputControl = numberBox;
                        break;

                    case SettingType.Combo:
                        var comboBox = new ComboBox
                        {
                            Width = 160,
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        foreach (var opt in field.Options)
                        {
                            comboBox.Items.Add(opt);
                        }
                        
                        string defaultValStr = field.DefaultValue?
                            .ToString() ?? string.Empty;
                        string currentSelection = SettingsManager.Instance
                            .GetSetting(
                                settingsGroup,
                                field.Key,
                                defaultValStr);
                        
                        // Подписываемся на изменение выбора до инициализации
                        // значения для корректной записи на диск
                        // автоисправленного регистра настройки.
                        comboBox.SelectionChanged += (s, e) =>
                        {
                            if (comboBox.SelectedItem != null)
                            {
                                SettingsManager.Instance.SetSetting(
                                    settingsGroup,
                                    field.Key,
                                    comboBox.SelectedItem.ToString());
                                UpdateVisibility(settingsGroup);
                            }
                        };

                        // Выполняем регистронезависимое сопоставление
                        // сохраненного значения со схемой опций скрипта.
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

            groupPanel.Children.Add(cardBorder);
            SettingsContainer.Children.Add(groupPanel);
            _groups.Add(groupVisual);
        }

        UpdateVisibility(settingsGroup);
    }

    /// <summary>
    /// Обновляет видимость полей настроек и групп на основе управляющих условий VisibleIf.
    /// </summary>
    private void UpdateVisibility(string settingsGroup)
    {
        // 1. Обновляем видимость отдельных элементов настроек
        foreach (var item in _generatedElements)
        {
            if (string.IsNullOrEmpty(item.Field.VisibleIfKey) || item.Field.VisibleIfValues == null)
            {
                item.Element.Visibility = Visibility.Visible;
                continue;
            }

            string controlValue = SettingsManager.Instance.GetSetting(
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

        // 2. Обновляем видимость целых групп настроек (StackPanel)
        foreach (var group in _groups)
        {
            bool hasVisibleElements = group.Elements.Any(e => e.Visibility == Visibility.Visible);
            group.GroupPanel.Visibility = hasVisibleElements ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
