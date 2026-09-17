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

    public ScriptSettingsViewModel ViewModel { get; }
    private readonly ISettingsManager _settingsManager;
    private readonly IDialogService _dialogService;
    private readonly IWhisperModelManager? _whisperModelManager;

    private class GroupVisual
    {
        public StackPanel? GroupPanel { get; set; }
        public Border CardBorder { get; set; } = null!;
        public List<FrameworkElement> Elements { get; set; } = new();
    }
    private readonly List<GroupVisual> _groups = new();
    private bool _isInternalCheckBoxUpdate;
    private bool _isInternalNumberBoxUpdate;
    private static readonly Dictionary<string, string> _lastActiveTabs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (Button ActionBtn, ProgressRing Ring, TextBlock PText, Button CancelBtn)> _whisperActionControls = new(StringComparer.OrdinalIgnoreCase);

    public ScriptSettingsControl()
    {
        ViewModel = App.Services.GetRequiredService<ScriptSettingsViewModel>();
        _settingsManager = App.Services.GetRequiredService<ISettingsManager>();
        _dialogService = App.Services.GetRequiredService<IDialogService>();
        _whisperModelManager = App.Services.GetService<IWhisperModelManager>();

        InitializeComponent();

        if (_whisperModelManager != null)
        {
            _whisperModelManager.DownloadProgressChanged += OnWhisperDownloadProgressChanged;
            _whisperModelManager.DownloadCompleted += OnWhisperDownloadCompleted;
        }

        // Обеспечивает автоматический сброс фокуса с полей ввода при клике на свободную область формы
        this.PointerPressed += (s, e) =>
        {
            this.IsTabStop = true;
            this.Focus(FocusState.Programmatic);
        };

        this.Unloaded += (s, e) =>
        {
            if (_activeScript != null)
            {
                _activeScript.FilesQueue.CollectionChanged -= OnFilesQueueCollectionChanged;
            }

            if (_whisperModelManager != null)
            {
                _whisperModelManager.DownloadProgressChanged -= OnWhisperDownloadProgressChanged;
                _whisperModelManager.DownloadCompleted -= OnWhisperDownloadCompleted;
            }

            _whisperActionControls.Clear();
        };
    }

    private void OnWhisperDownloadProgressChanged(string modelKey, int percent)
    {
        App.CurrentMainWindow?.DispatcherQueue?.TryEnqueue(() =>
        {
            if (_whisperActionControls.TryGetValue(modelKey, out var ctrl))
            {
                ctrl.ActionBtn.IsEnabled = false;
                ctrl.Ring.Visibility = Visibility.Visible;
                ctrl.Ring.IsActive = true;
                ctrl.PText.Visibility = Visibility.Visible;
                ctrl.PText.Text = $"{percent}%";
                ctrl.CancelBtn.Visibility = Visibility.Visible;
            }
        });
    }

    private void OnWhisperDownloadCompleted(string modelKey, bool isSuccess, string? failureReason)
    {
        App.CurrentMainWindow?.DispatcherQueue?.TryEnqueue(() =>
        {
            if (_whisperActionControls.TryGetValue(modelKey, out var ctrl))
            {
                ctrl.ActionBtn.IsEnabled = true;
                ctrl.Ring.IsActive = false;
                ctrl.Ring.Visibility = Visibility.Collapsed;
                ctrl.PText.Visibility = Visibility.Collapsed;
                ctrl.CancelBtn.Visibility = Visibility.Collapsed;

                bool downloaded = _whisperModelManager?.IsModelDownloaded(modelKey) ?? false;
                UpdateWhisperButtonVisuals(ctrl.ActionBtn, downloaded);
            }

            if (_activeScript != null)
            {
                string settingsGroup = _settingsManager.GetSafeGroupName(_activeScript.Name);
                if (isSuccess)
                {
                    string currentModel = _settingsManager.GetSetting(settingsGroup, "whisper_model", string.Empty);
                    if (string.IsNullOrWhiteSpace(currentModel) || currentModel.Contains("Нет загруженных", StringComparison.OrdinalIgnoreCase))
                    {
                        _settingsManager.SetSetting(settingsGroup, "whisper_model", modelKey);
                    }
                }
                UpdateVisibility(settingsGroup);
            }
        });
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
                Style = (Style)Application.Current.Resources["SettingsSecondaryTextBlockStyle"],
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

            // Восстанавливаем ранее активную вкладку для этого скрипта или выбираем первую по умолчанию
            if (SettingsNavigationView.MenuItems.Count > 0)
            {
                NavigationViewItem? targetItem = null;
                if (_lastActiveTabs.TryGetValue(script.Name, out string? lastTab) && !string.IsNullOrEmpty(lastTab))
                {
                    targetItem = SettingsNavigationView.MenuItems
                        .OfType<NavigationViewItem>()
                        .FirstOrDefault(i => string.Equals(i.Tag?.ToString(), lastTab, StringComparison.OrdinalIgnoreCase));
                }

                targetItem ??= (NavigationViewItem)SettingsNavigationView.MenuItems[0];
                SettingsNavigationView.SelectedItem = targetItem;
                string activeTag = targetItem.Tag?.ToString() ?? "";
                foreach (var pair in _groupContainers)
                {
                    pair.Value.Visibility = pair.Key.Equals(activeTag, StringComparison.OrdinalIgnoreCase)
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                }
            }
        }

        UpdateVisibility(settingsGroup);

        if (_settingsManager.GetSetting(settingsGroup, "lossless", false))
        {
            ApplyFastestPresetOnLossless(settingsGroup);
        }
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

            if (_activeScript != null && !string.IsNullOrEmpty(tag))
            {
                _lastActiveTabs[_activeScript.Name] = tag;
            }

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
            Style = (Style)Application.Current.Resources["SettingsGroupCardBorderStyle"],
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
            Style = (Style)Application.Current.Resources["SettingsPrimaryTextBlockStyle"],
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
                    Style = (Style)Application.Current.Resources["SettingsSecondaryTextBlockStyle"],
                    Margin = new Thickness(0, 8, 0, 4)
                };
                cardContentStack.Children.Add(subtitle);
                continue;
            }

            if (field.Key == "name_format")
            {
                var nameFormatContainer = CreateNameFormatDesignerContainer(settingsGroup, field);
                cardContentStack.Children.Add(nameFormatContainer);
                _generatedElements.Add((field, nameFormatContainer));
                groupVisual.Elements.Add(nameFormatContainer);
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
                    Style = (Style)Application.Current.Resources["SettingsPrimaryTextBlockStyle"],
                    TextWrapping = TextWrapping.Wrap
                });

                checkContent.Children.Add(new TextBlock
                {
                    Tag = "FieldComment",
                    Text = field.Comment ?? string.Empty,
                    FontSize = 12,
                    Style = (Style)Application.Current.Resources["SettingsSecondaryTextBlockStyle"],
                    TextWrapping = TextWrapping.Wrap,
                    Visibility = string.IsNullOrEmpty(field.Comment) ? Visibility.Collapsed : Visibility.Visible
                });

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
                    if (field.Key == "auto_bitrate")
                    {
                        RecalculateAutoBitrate(settingsGroup);
                    }
                    else if (field.Key == "lossless")
                    {
                        ApplyFastestPresetOnLossless(settingsGroup);
                    }
                    UpdatePreview();
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
                };

                cardContentStack.Children.Add(checkBox);
                _generatedElements.Add((field, checkBox));
                groupVisual.Elements.Add(checkBox);
                continue;
            }

            if (field.Type == SettingType.Expander)
            {
                var expander = new SettingsExpander
                {
                    Header = field.Label,
                    Description = field.Comment ?? string.Empty,
                    IsExpanded = false,
                    Padding = new Thickness(16, 12, 16, 12),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Margin = new Thickness(0)
                };

                if (!string.IsNullOrEmpty(field.HeaderIconGlyph))
                {
                    expander.HeaderIcon = new FontIcon { Glyph = field.HeaderIconGlyph };
                }

                bool hasToggleSwitch = field.HasToggleSwitch;
                bool isExpanderOn = hasToggleSwitch
                    ? _settingsManager.GetSetting(settingsGroup, field.Key, field.DefaultValue is bool b && b)
                    : true;

                var childCards = new List<SettingsCard>();

                if (hasToggleSwitch)
                {
                    var toggleSwitch = new ToggleSwitch
                    {
                        OffContent = "Выкл",
                        OnContent = "Вкл",
                        IsOn = isExpanderOn,
                        VerticalAlignment = VerticalAlignment.Center
                    };

                    expander.Content = toggleSwitch;

                    toggleSwitch.Toggled += (s, e) =>
                    {
                        bool isOn = toggleSwitch.IsOn;
                        _settingsManager.SetSetting(settingsGroup, field.Key, isOn);
                        foreach (var card in childCards)
                        {
                            card.IsEnabled = isOn;
                        }
                        UpdateVisibility(settingsGroup);
                        UpdatePreview();
                    };
                }

                foreach (var childField in field.ChildFields)
                {
                    var childInputControl = CreateSettingInputControl(childField, settingsGroup);

                    var childCard = new SettingsCard
                    {
                        Header = childField.Label,
                        Description = childField.Comment ?? string.Empty,
                        IsEnabled = isExpanderOn,
                        Padding = new Thickness(16, 12, 16, 12),
                        Content = childInputControl
                    };

                    expander.Items.Add(childCard);
                    childCards.Add(childCard);
                    _generatedElements.Add((childField, childCard));
                    groupVisual.Elements.Add(childCard);
                }

                cardContentStack.Children.Add(expander);
                _generatedElements.Add((field, expander));
                groupVisual.Elements.Add(expander);
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
                Style = (Style)Application.Current.Resources["SettingsPrimaryTextBlockStyle"],
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            });

            textStack.Children.Add(new TextBlock
            {
                Tag = "FieldComment",
                Text = field.Comment ?? string.Empty,
                FontSize = 12,
                Style = (Style)Application.Current.Resources["SettingsSecondaryTextBlockStyle"],
                TextWrapping = TextWrapping.Wrap,
                Visibility = string.IsNullOrEmpty(field.Comment) ? Visibility.Collapsed : Visibility.Visible
            });
            
            Grid.SetColumn(textStack, 0);
            rowGrid.Children.Add(textStack);

            FrameworkElement? inputControl = CreateSettingInputControl(field, settingsGroup);

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
                Style = (Style)Application.Current.Resources["SettingsPrimaryTextBlockStyle"],
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
    /// Создает и возвращает соответствующий элемент ввода для заданного поля настройки (TextBox, NumberBox, ComboBox, ToggleSwitch).
    /// </summary>
    private FrameworkElement? CreateSettingInputControl(SettingField field, string settingsGroup)
    {
        bool isRenameField = field.Key == "LocalRenameSearch" || field.Key == "LocalRenameReplace";
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
            case SettingType.Float:
                bool isFloat = field.Type == SettingType.Float;
                double defaultNum = 0;
                if (field.DefaultValue is int dInt) defaultNum = dInt;
                else if (field.DefaultValue is float dF) defaultNum = dF;
                else if (field.DefaultValue is double dD) defaultNum = dD;
                else double.TryParse(field.DefaultValue?.ToString(), out defaultNum);

                double initialVal = isFloat
                    ? _settingsManager.GetSetting(settingsGroup, field.Key, defaultNum)
                    : _settingsManager.GetSetting(settingsGroup, field.Key, (int)defaultNum);

                var numberBox = new NumberBox
                {
                    Value = Math.Round(initialVal, isFloat ? 2 : 0),
                    Width = 160,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
                    SmallChange = isFloat ? 0.05 : 1,
                    LargeChange = isFloat ? 0.1 : 5,
                    VerticalAlignment = VerticalAlignment.Center
                };
                if (isFloat)
                {
                    var formatter = new Windows.Globalization.NumberFormatting.DecimalFormatter
                    {
                        FractionDigits = 2
                    };
                    numberBox.NumberFormatter = formatter;
                }
                if (field.Minimum.HasValue)
                {
                    numberBox.Minimum = Math.Round(field.Minimum.Value, 4);
                }
                if (field.Maximum.HasValue)
                {
                    numberBox.Maximum = Math.Round(field.Maximum.Value, 4);
                }
                numberBox.ValueChanged += (s, e) =>
                {
                    if (!double.IsNaN(numberBox.Value))
                    {
                        if (isFloat)
                        {
                            double roundedVal = Math.Round(numberBox.Value, 2);
                            _settingsManager.SetSetting(settingsGroup, field.Key, roundedVal);
                            UpdateVisibility(settingsGroup);
                            HandleIntSettingChanged(settingsGroup, field.Key, (int)roundedVal);
                        }
                        else
                        {
                            int valInt = (int)numberBox.Value;
                            _settingsManager.SetSetting(settingsGroup, field.Key, valInt);
                            UpdateVisibility(settingsGroup);
                            HandleIntSettingChanged(settingsGroup, field.Key, valInt);
                        }
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
                if (field.Key == "nvenc_preset" && _settingsManager.GetSetting(settingsGroup, "lossless", false))
                {
                    defaultValStr = "p1";
                }
                else if (field.Key == "x265_preset" && _settingsManager.GetSetting(settingsGroup, "lossless", false))
                {
                    defaultValStr = "ultrafast";
                }

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
                        if (field.Key == "encoder" && _settingsManager.GetSetting(settingsGroup, "lossless", false))
                        {
                            ApplyFastestPresetOnLossless(settingsGroup);
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

            case SettingType.Checkbox:
                var toggle = new ToggleSwitch
                {
                    OffContent = "Выкл",
                    OnContent = "Вкл",
                    IsOn = _settingsManager.GetSetting(
                        settingsGroup,
                        field.Key,
                        field.DefaultValue is bool b && b),
                    VerticalAlignment = VerticalAlignment.Center
                };
                toggle.Toggled += (s, e) =>
                {
                    _settingsManager.SetSetting(settingsGroup, field.Key, toggle.IsOn);
                    UpdateVisibility(settingsGroup);
                    UpdatePreview();
                };
                inputControl = toggle;
                break;

            case SettingType.WhisperModelAction:
                inputControl = CreateWhisperModelActionControl(field, settingsGroup);
                break;
        }

        return inputControl;
    }

    /// <summary>
    /// Обновляет видимость полей настроек и групп на основе управляющих условий VisibleIf.
    /// </summary>
    private void UpdateVisibility(string settingsGroup)
    {
        // 0. Динамическое обновление вариантов ComboBox, подсказок (Comment) и диапазонов от активного контекста энкодера
        if (_activeScript != null)
        {
            var currentSettings = new Dictionary<string, object>();
            foreach (var item in _generatedElements)
            {
                currentSettings[item.Field.Key] = _settingsManager.GetSetting(settingsGroup, item.Field.Key, item.Field.DefaultValue ?? string.Empty);
            }
            var dynamicSchema = _activeScript.GetFullSettingsSchema(currentSettings);
            if (dynamicSchema != null)
            {
                foreach (var item in _generatedElements)
                {
                    var dynamicField = dynamicSchema.FirstOrDefault(f => f.Key == item.Field.Key);
                    if (dynamicField != null)
                    {
                        if (item.Element is FrameworkElement container)
                        {
                            // А. Динамическое обновление текста подсказки (Comment)
                            var commentBlock = FindChildElement<TextBlock>(container, tb => (string)tb.Tag == "FieldComment");
                            if (commentBlock != null)
                            {
                                commentBlock.Text = dynamicField.Comment ?? string.Empty;
                                commentBlock.Visibility = string.IsNullOrEmpty(dynamicField.Comment) ? Visibility.Collapsed : Visibility.Visible;
                            }
                            else if (container is SettingsCard sc)
                            {
                                sc.Header = dynamicField.Label;
                                sc.Description = dynamicField.Comment ?? string.Empty;
                            }
                            else if (container is SettingsExpander se)
                            {
                                se.Header = dynamicField.Label;
                                se.Description = dynamicField.Comment ?? string.Empty;
                            }

                            // Б. Динамическое обновление вариантов ComboBox
                            if (dynamicField.Type == SettingType.Combo && dynamicField.Options != null && dynamicField.Options.Count > 0)
                            {
                                var combo = FindChildElement<ComboBox>(container);
                                if (combo != null)
                                {
                                    var currentItems = combo.Items.Cast<object>().Select(o => o.ToString() ?? "").ToList();
                                    if (!currentItems.SequenceEqual(dynamicField.Options))
                                    {
                                        string currSelected = combo.SelectedItem?.ToString() ?? "";
                                        string savedValue = _settingsManager.GetSetting(settingsGroup, dynamicField.Key, currSelected);
                                        combo.Items.Clear();
                                        foreach (var opt in dynamicField.Options)
                                        {
                                            combo.Items.Add(opt);
                                        }

                                        if (dynamicField.Options.Contains(savedValue, StringComparer.OrdinalIgnoreCase))
                                        {
                                            combo.SelectedItem = dynamicField.Options.First(o => o.Equals(savedValue, StringComparison.OrdinalIgnoreCase));
                                        }
                                        else if (dynamicField.Options.Contains(currSelected, StringComparer.OrdinalIgnoreCase))
                                        {
                                            combo.SelectedItem = dynamicField.Options.First(o => o.Equals(currSelected, StringComparison.OrdinalIgnoreCase));
                                        }
                                        else
                                        {
                                            string defVal = dynamicField.DefaultValue?.ToString() ?? "";
                                            string matchedDef = dynamicField.Options.FirstOrDefault(o => o.Equals(defVal, StringComparison.OrdinalIgnoreCase))
                                                ?? dynamicField.Options.FirstOrDefault() ?? "";
                                            combo.SelectedItem = matchedDef;
                                            // Не перезаписываем _settingsManager принудительно, чтобы сохранить выбор пользователя,
                                            // если нужный элемент временно недоступен или список динамически перестраивается.
                                        }
                                    }
                                }
                            }
                            // В. Динамическое обновление диапазонов NumberBox (Minimum / Maximum)
                            else if (dynamicField.Type == SettingType.Int || dynamicField.Type == SettingType.Float)
                            {
                                var numberBox = FindChildElement<NumberBox>(container);
                                if (numberBox != null)
                                {
                                    if (dynamicField.Minimum.HasValue) numberBox.Minimum = dynamicField.Minimum.Value;
                                    if (dynamicField.Maximum.HasValue) numberBox.Maximum = dynamicField.Maximum.Value;
                                }
                            }
                        }
                    }
                }
            }
        }

        // 1. Обновляем видимость отдельных элементов настроек
        foreach (var item in _generatedElements)
        {
            if (item.Field.VisibilityConditions != null && item.Field.VisibilityConditions.Count > 0)
            {
                bool isCondVisible = true;
                foreach (var cond in item.Field.VisibilityConditions)
                {
                    object? targetDefVal = _generatedElements.FirstOrDefault(x => x.Field.Key == cond.Key).Field?.DefaultValue;
                    string condValue = _settingsManager.GetSetting(
                        settingsGroup,
                        cond.Key,
                        targetDefVal?.ToString() ?? string.Empty);

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

            object? visibleIfDefVal = _generatedElements.FirstOrDefault(x => x.Field.Key == item.Field.VisibleIfKey).Field?.DefaultValue;
            string controlValue = _settingsManager.GetSetting(
                settingsGroup,
                item.Field.VisibleIfKey,
                visibleIfDefVal?.ToString() ?? string.Empty);

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

        // 1.5. Обработка отключения (DisableConditions)
        foreach (var item in _generatedElements)
        {
            if (item.Field.DisableConditions != null && item.Field.DisableConditions.Count > 0)
            {
                bool isDisabled = false;
                foreach (var cond in item.Field.DisableConditions)
                {
                    object? targetDefVal = _generatedElements.FirstOrDefault(x => x.Field.Key == cond.Key).Field?.DefaultValue;
                    string condValue = _settingsManager.GetSetting(
                        settingsGroup,
                        cond.Key,
                        targetDefVal?.ToString() ?? string.Empty);

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

                    if (matches)
                    {
                        isDisabled = true;
                        break;
                    }
                }
                if (item.Element is Control ctrl)
                {
                    ctrl.IsEnabled = !isDisabled;
                }
                else if (item.Element is Grid grid)
                {
                    foreach (var child in grid.Children.OfType<Control>())
                    {
                        child.IsEnabled = !isDisabled;
                    }
                }
                item.Element.IsHitTestVisible = !isDisabled;
                item.Element.Opacity = isDisabled ? 0.5 : 1.0;
            }
            else
            {
                if (item.Element is Control ctrl)
                {
                    ctrl.IsEnabled = true;
                }
                else if (item.Element is Grid grid)
                {
                    foreach (var child in grid.Children.OfType<Control>())
                    {
                        child.IsEnabled = true;
                    }
                }
                item.Element.IsHitTestVisible = true;
                item.Element.Opacity = 1.0;
            }
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
            Style = (Style)Application.Current.Resources["SettingsPrimaryTextBlockStyle"]
        });

        labelStack.Children.Add(new TextBlock
        {
            Tag = "FieldComment",
            Text = field.Comment ?? string.Empty,
            FontSize = 12,
            Style = (Style)Application.Current.Resources["SettingsSecondaryTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap,
            Visibility = string.IsNullOrEmpty(field.Comment) ? Visibility.Collapsed : Visibility.Visible
        });
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
                var checkBox = row.Children.OfType<CheckBox>().FirstOrDefault(c => c.Content == null);
                var textBox = row.Children.OfType<TextBox>().FirstOrDefault();
                var onlyPartCheckBox = row.Children.OfType<CheckBox>().FirstOrDefault(c => c.Content?.ToString() == "Только часть");
                if (checkBox != null && textBox != null)
                {
                    var itemDict = new Dictionary<string, object>
                    {
                        { "word", textBox.Text },
                        { "active", checkBox.IsChecked == true }
                    };
                    if (onlyPartCheckBox != null)
                    {
                        itemDict["only_part"] = onlyPartCheckBox.IsChecked == true;
                    }
                    listToSave.Add(itemDict);
                }
            }
            _settingsManager.SetSetting(settingsGroup, field.Key, listToSave);
        }

        // Вспомогательная локальная функция добавления строки ключевого слова в UI
        void AddKeywordRow(string word, bool isActive, bool isOnlyPart = false)
        {
            var rowGrid = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 2, 0, 2)
            };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) }); // Галочка
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Поле ввода

            bool isPatternField = field.Key == "text_patterns";
            if (isPatternField)
            {
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Только часть
            }
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

            if (isPatternField)
            {
                var onlyPartCheckBox = new CheckBox
                {
                    Content = "Только часть",
                    IsChecked = isOnlyPart,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                };
                onlyPartCheckBox.Checked += (s, e) => SaveList();
                onlyPartCheckBox.Unchecked += (s, e) => SaveList();
                Grid.SetColumn(onlyPartCheckBox, 2);
                rowGrid.Children.Add(onlyPartCheckBox);
            }

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
            Grid.SetColumn(deleteButton, isPatternField ? 3 : 2);
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
                bool isOnlyPart = item.TryGetValue("only_part", out var op) && SafeGetBool(op);
                AddKeywordRow(word, isActive, isOnlyPart);
            }
        }

        // Действие при клике на кнопку добавления
        addButton.Click += (s, e) =>
        {
            AddKeywordRow("", true, false);
            SaveList();
        };

        return mainStack;
    }

    /// <summary>
    /// Создает интерактивный конструктор шаблонов имен файлов для скрипта разборки контейнера.
    /// Предоставляет текстовое поле, набор быстрых кнопок-тегов и панель живого предпросмотра.
    /// </summary>
    private FrameworkElement CreateNameFormatDesignerContainer(string settingsGroup, SettingField field)
    {
        var mainStack = new StackPanel
        {
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 12)
        };

        // Заголовок настройки и комментарий
        var labelStack = new StackPanel { Spacing = 2 };
        labelStack.Children.Add(new TextBlock
        {
            Text = field.Label,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Style = (Style)Application.Current.Resources["SettingsPrimaryTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap
        });

        labelStack.Children.Add(new TextBlock
        {
            Tag = "FieldComment",
            Text = field.Comment ?? string.Empty,
            FontSize = 12,
            Style = (Style)Application.Current.Resources["SettingsSecondaryTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap,
            Visibility = string.IsNullOrEmpty(field.Comment) ? Visibility.Collapsed : Visibility.Visible
        });
        mainStack.Children.Add(labelStack);

        // Поле ввода шаблона
        var textBox = new TextBox
        {
            Text = _settingsManager.GetSetting(
                settingsGroup,
                field.Key,
                field.DefaultValue?.ToString() ?? string.Empty),
            PlaceholderText = "Введите шаблон (например, {original}_{lang}_{id})",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 2, 0, 2)
        };
        mainStack.Children.Add(textBox);

        // Сетка/панель быстрых тегов (кнопок-чипсов)
        var chipsLabel = new TextBlock
        {
            Text = "Доступные теги (нажмите для добавления):",
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            Style = (Style)Application.Current.Resources["SettingsSecondaryTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap
        };
        mainStack.Children.Add(chipsLabel);

        var chipsPanel = new VariableSizedWrapGrid
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemWidth = 140,
            ItemHeight = 36,
            Margin = new Thickness(0, 2, 0, 2)
        };

        var tags = new[]
        {
            (Tag: "{original}", Label: "Имя файла"),
            (Tag: "{lang}", Label: "Язык"),
            (Tag: "{id}", Label: "ID дорожки"),
            (Tag: "{title}", Label: "Заголовок"),
            (Tag: "{codec}", Label: "Кодек")
        };

        foreach (var (tag, tagLabel) in tags)
        {
            var btn = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children = {
                        new FontIcon { Glyph = "\uE710", FontSize = 10 },
                        new TextBlock { Text = tagLabel, FontSize = 12 }
                    }
                },
                Style = (Style)Application.Current.Resources["DefaultButtonStyle"],
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(0, 0, 4, 4),
                CornerRadius = new CornerRadius(4)
            };

            btn.Click += (s, e) =>
            {
                int selectionStart = textBox.SelectionStart;
                string currentText = textBox.Text;
                string newText = currentText.Insert(selectionStart, tag);
                textBox.Text = newText;
                textBox.SelectionStart = selectionStart + tag.Length;
                textBox.Focus(FocusState.Programmatic);
            };

            chipsPanel.Children.Add(btn);
        }
        mainStack.Children.Add(chipsPanel);

        // Панель живого предпросмотра
        var previewBorder = new Border
        {
            Style = (Style)Application.Current.Resources["SettingsSecondaryCardBorderStyle"],
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 4, 0, 4)
        };

        var previewStack = new StackPanel { Spacing = 2 };
        previewStack.Children.Add(new TextBlock
        {
            Text = "Предпросмотр имени файла:",
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Style = (Style)Application.Current.Resources["SettingsSecondaryTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap
        });

        var previewTextBlock = new TextBlock
        {
            Text = string.Empty,
            FontSize = 13,
            FontFamily = new FontFamily("Consolas"),
            Style = (Style)Application.Current.Resources["SettingsAccentTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap
        };
        previewStack.Children.Add(previewTextBlock);
        previewBorder.Child = previewStack;
        mainStack.Children.Add(previewBorder);

        // Функция обновления предпросмотра
        void UpdateLocalPreview()
        {
            string pattern = textBox.Text;
            
            // Тестовые данные для отображения примера
            string originalStem = "Overlord - 01";
            string trackIdStr = "track03";
            string lang = "rus";
            string trackTitle = "TrackTitle";
            string trackCodec = "dts";

            // Симулируем логику FormatFilename
            string name = pattern;
            name = ReplacePlaceholderLocal(name, new[] { "{original}", "{original_name}", "{file_name}" }, originalStem);
            name = ReplacePlaceholderLocal(name, new[] { "{id}", "{track_id}" }, trackIdStr);
            name = ReplacePlaceholderLocal(name, new[] { "{title}", "{track_title}", "{name}" }, trackTitle);
            name = ReplacePlaceholderLocal(name, new[] { "{codec}", "{track_codec}" }, trackCodec);
            name = ReplacePlaceholderLocal(name, new[] { "{lang}", "{language}" }, lang);

            name = CleanSeparatorsLocal(name);
            if (string.IsNullOrWhiteSpace(name))
            {
                name = $"{originalStem}_{trackIdStr}";
            }

            previewTextBlock.Text = $"{name}.{trackCodec}";
        }

        // Подписываемся на события изменения текста
        textBox.TextChanged += (s, e) =>
        {
            _settingsManager.SetSetting(settingsGroup, field.Key, textBox.Text);
            UpdateLocalPreview();
        };

        // Первоначальное обновление предпросмотра
        UpdateLocalPreview();

        return mainStack;
    }

    private static string ReplacePlaceholderLocal(string template, string[] placeholders, string value)
    {
        foreach (var placeholder in placeholders)
        {
            if (template.Contains(placeholder, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(value))
                {
                    template = System.Text.RegularExpressions.Regex.Replace(template, System.Text.RegularExpressions.Regex.Escape(placeholder), value, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                }
                else
                {
                    template = System.Text.RegularExpressions.Regex.Replace(template, "_" + System.Text.RegularExpressions.Regex.Escape(placeholder), "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    template = System.Text.RegularExpressions.Regex.Replace(template, System.Text.RegularExpressions.Regex.Escape(placeholder) + "_", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    template = System.Text.RegularExpressions.Regex.Replace(template, "-" + System.Text.RegularExpressions.Regex.Escape(placeholder), "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    template = System.Text.RegularExpressions.Regex.Replace(template, System.Text.RegularExpressions.Regex.Escape(placeholder) + "-", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    template = System.Text.RegularExpressions.Regex.Replace(template, System.Text.RegularExpressions.Regex.Escape(placeholder), "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                }
            }
        }
        return template;
    }

    private static string CleanSeparatorsLocal(string name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        
        // 1. Очистка пустых скобок, оставшихся от незаполненных плейсхолдеров
        name = System.Text.RegularExpressions.Regex.Replace(name, @"\[\s*\]", "");
        name = System.Text.RegularExpressions.Regex.Replace(name, @"\(\s*\)", "");
        name = System.Text.RegularExpressions.Regex.Replace(name, @"\{\s*\}", "");

        // 2. Схлопывание дублирующихся разделителей и пробелов
        name = System.Text.RegularExpressions.Regex.Replace(name, @"_+", "_");
        name = System.Text.RegularExpressions.Regex.Replace(name, @"-+", "-");
        name = System.Text.RegularExpressions.Regex.Replace(name, @"\s+", " ");
        name = System.Text.RegularExpressions.Regex.Replace(name, @"_-", "-");
        name = System.Text.RegularExpressions.Regex.Replace(name, @"-_", "-");
        
        return name.Trim('_', '-', ' ');
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
    /// Вызывается при изменении целочисленного параметра в интерфейсе.
    /// </summary>
    private void HandleIntSettingChanged(string settingsGroup, string key, int newValue)
    {
        if (_isInternalNumberBoxUpdate) return;

        _settingsManager.SetSetting(settingsGroup, key, newValue);

        if (key == "v_bitrate")
        {
            bool autoBitrate = _settingsManager.GetSetting(settingsGroup, "auto_bitrate", true);
            if (autoBitrate)
            {
                RecalculateAutoBitrate(settingsGroup, newValue);
            }
        }
        else if (key == "max_bitrate")
        {
            bool autoBitrate = _settingsManager.GetSetting(settingsGroup, "auto_bitrate", true);
            if (autoBitrate)
            {
                int bufSize = newValue * 2;
                UpdateIntSettingAndUI(settingsGroup, "bufsize", bufSize);
            }
        }

        UpdateVisibility(settingsGroup);
    }

    /// <summary>
    /// Выполняет авторасчет параметров битрейта (min_bitrate, max_bitrate, bufsize) на основе целевого битрейта.
    /// </summary>
    public void RecalculateAutoBitrate(string settingsGroup, int? targetBitrate = null)
    {
        int vBr = targetBitrate ?? _settingsManager.GetSetting(settingsGroup, "v_bitrate", 4000);
        int minBr = vBr;
        int maxBr = vBr * 2;
        int bufSize = maxBr * 2;

        UpdateIntSettingAndUI(settingsGroup, "min_bitrate", minBr);
        UpdateIntSettingAndUI(settingsGroup, "max_bitrate", maxBr);
        UpdateIntSettingAndUI(settingsGroup, "bufsize", bufSize);
    }

    private void ApplyFastestPresetOnLossless(string settingsGroup)
    {
        _settingsManager.SetSetting(settingsGroup, "nvenc_preset", "p1");
        _settingsManager.SetSetting(settingsGroup, "x265_preset", "ultrafast");

        var presetsToSync = new[] { ("nvenc_preset", "p1"), ("x265_preset", "ultrafast") };
        foreach (var (key, fastest) in presetsToSync)
        {
            var targetItem = _generatedElements.FirstOrDefault(x => x.Field.Key == key);
            if (targetItem.Element != null)
            {
                ComboBox? combo = FindChildElement<ComboBox>(targetItem.Element);
                if (combo != null && combo.Items.Count > 0)
                {
                    string? matchedOption = combo.Items.Cast<object>()
                        .Select(o => o.ToString() ?? "")
                        .FirstOrDefault(o => o.Equals(fastest, StringComparison.OrdinalIgnoreCase));
                    if (matchedOption != null && !object.Equals(combo.SelectedItem, matchedOption))
                    {
                        combo.SelectedItem = matchedOption;
                    }
                }
            }
        }
    }

    private void UpdateIntSettingAndUI(string settingsGroup, string key, int value)
    {
        _settingsManager.SetSetting(settingsGroup, key, value);

        var targetItem = _generatedElements.FirstOrDefault(x => x.Field.Key == key);
        if (targetItem.Element != null)
        {
            NumberBox? numBox = targetItem.Element as NumberBox;
            if (numBox == null && targetItem.Element is Grid grid)
            {
                numBox = grid.Children.OfType<NumberBox>().FirstOrDefault();
            }

            if (numBox != null && Math.Abs(numBox.Value - value) > 0.0001)
            {
                _isInternalNumberBoxUpdate = true;
                numBox.Value = value;
                _isInternalNumberBoxUpdate = false;
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
            Style = (Style)Application.Current.Resources["SettingsSecondaryTextBlockStyle"], 
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
                Style = (Style)Application.Current.Resources["SettingsAccentTextBlockStyle"] 
            });
            rowStack.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(item.Description) ? "" : $"— {CleanDescription(item.Description)}", FontSize = 12 });
            
            var copiedText = new TextBlock
            {
                Text = "Скопировано!",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Style = (Style)Application.Current.Resources["SettingsAccentTextBlockStyle"],
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
            Style = (Style)Application.Current.Resources["SettingsSecondaryTextBlockStyle"], 
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
                Style = (Style)Application.Current.Resources["SettingsAccentTextBlockStyle"] 
            });
            rowStack.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(item.Description) ? "" : $"— {CleanDescription(item.Description)}", FontSize = 12 });
            
            var copiedText = new TextBlock
            {
                Text = "Скопировано!",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Style = (Style)Application.Current.Resources["SettingsAccentTextBlockStyle"],
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

    private static void UpdateWhisperButtonVisuals(Button actionButton, bool downloaded)
    {
        if (downloaded)
        {
            actionButton.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new FontIcon { Glyph = "\uE74D", FontSize = 13 },
                    new TextBlock { Text = "Удалить" }
                }
            };
            actionButton.ClearValue(Button.StyleProperty);
        }
        else
        {
            actionButton.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new FontIcon { Glyph = "\uE896", FontSize = 13 },
                    new TextBlock { Text = "Загрузить" }
                }
            };
            actionButton.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        }
    }

    /// <summary>
    /// Создает интерактивный элемент управления для модели Whisper:
    /// Кнопка «Загрузить» / «Удалить» с отображением ProgressRing, процентов прогресса и кнопкой отмены.
    /// </summary>
    private FrameworkElement CreateWhisperModelActionControl(SettingField field, string settingsGroup)
    {
        string modelKey = field.DefaultValue?.ToString() ?? field.Key.Replace("whisper_model_action_", "");
        var container = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };

        if (_whisperModelManager == null)
        {
            return container;
        }

        bool isDownloaded = _whisperModelManager.IsModelDownloaded(modelKey);
        bool isDownloading = _whisperModelManager.IsModelDownloading(modelKey);
        int currentProgress = isDownloading ? _whisperModelManager.GetModelDownloadProgress(modelKey) : 0;

        var actionButton = new Button
        {
            MinWidth = 110,
            Height = 34,
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = !isDownloading
        };

        var progressRing = new ProgressRing
        {
            Width = 18,
            Height = 18,
            IsActive = isDownloading,
            Visibility = isDownloading ? Visibility.Visible : Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center
        };

        var progressText = new TextBlock
        {
            Text = $"{currentProgress}%",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = isDownloading ? Visibility.Visible : Visibility.Collapsed,
            Style = (Style)Application.Current.Resources["SettingsSecondaryTextBlockStyle"]
        };

        var cancelButton = new Button
        {
            Content = "\uE711",
            FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
            Width = 34,
            Height = 34,
            Padding = new Thickness(0),
            Visibility = isDownloading ? Visibility.Visible : Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(cancelButton, "Отменить загрузку");

        UpdateWhisperButtonVisuals(actionButton, isDownloaded);

        // Регистрируем элементы управления модели в реестре активных контролов для получения событий прогресса
        _whisperActionControls[modelKey] = (actionButton, progressRing, progressText, cancelButton);

        cancelButton.Click += (s, e) =>
        {
            _whisperModelManager.CancelDownload(modelKey);
        };

        actionButton.Click += async (s, e) =>
        {
            bool currentDownloaded = _whisperModelManager.IsModelDownloaded(modelKey);
            if (currentDownloaded)
            {
                // Подтверждение удаления
                var modelInfo = _whisperModelManager.GetModelInfo(modelKey);
                string title = "Удаление модели Whisper";
                string prompt = $"Вы действительно хотите удалить файл модели '{modelInfo?.DisplayName ?? modelKey}' с диска?";

                bool confirm = await _dialogService.ShowConfirmationAsync(title, prompt, "Удалить", "Отмена");
                if (confirm)
                {
                    _whisperModelManager.DeleteModel(modelKey);
                    UpdateWhisperButtonVisuals(actionButton, false);
                    UpdateVisibility(settingsGroup);
                }
                return;
            }

            // Запуск скачивания модели через центральный менеджер
            actionButton.IsEnabled = false;
            progressRing.Visibility = Visibility.Visible;
            progressRing.IsActive = true;
            progressText.Visibility = Visibility.Visible;
            progressText.Text = "0%";
            cancelButton.Visibility = Visibility.Visible;

            _ = Task.Run(async () =>
            {
                await _whisperModelManager.DownloadModelAsync(modelKey);
            });
        };

        container.Children.Add(progressRing);
        container.Children.Add(progressText);
        container.Children.Add(actionButton);
        container.Children.Add(cancelButton);

        return container;
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
                Style = (Style)Application.Current.Resources["SettingsSecondaryTextBlockStyle"],
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
                Style = (Style)Application.Current.Resources["SettingsSecondaryTextBlockStyle"],
                FontStyle = Windows.UI.Text.FontStyle.Italic,
                Margin = new Thickness(0, 4, 0, 0)
            });
            return;
        }

        int maxFiles = _isPreviewExpanded ? files.Count : 5;
        var previewStack = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Stretch };

        // Собираем текущие настройки для предпросмотра
        var settings = new Dictionary<string, object>();
        foreach (var field in _activeScript.GetFullSettingsSchema())
        {
            if (field.Type != SettingType.Subtitle)
            {
                settings[field.Key] = _settingsManager.GetSetting(settingsGroup, field.Key, field.DefaultValue);
            }

            if (field.ChildFields != null && field.ChildFields.Count > 0)
            {
                foreach (var child in field.ChildFields)
                {
                    if (child.Type != SettingType.Subtitle)
                    {
                        settings[child.Key] = _settingsManager.GetSetting(settingsGroup, child.Key, child.DefaultValue);
                    }
                }
            }
        }

        for (int i = 0; i < Math.Min(files.Count, maxFiles); i++)
        {
            var file = files[i];
            string previewOutPath = _activeScript.GetPreviewOutputPath(file.FilePath, file.FilePath, i + 1, settings);
            string newName = Path.GetFileName(previewOutPath);

            var rowGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var oldText = new TextBlock
            {
                Text = file.FileName,
                FontSize = 12,
                Style = (Style)Application.Current.Resources["SettingsSecondaryTextBlockStyle"],
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            Grid.SetColumn(oldText, 0);
            rowGrid.Children.Add(oldText);

            var arrow = new TextBlock
            {
                Text = " ➜ ",
                FontSize = 12,
                Style = (Style)Application.Current.Resources["SettingsTertiaryTextBlockStyle"],
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
                newText.Style = (Style)Application.Current.Resources["SettingsSecondaryTextBlockStyle"];
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

    private static T? FindChildElement<T>(DependencyObject parent, Func<T, bool>? predicate = null) where T : DependencyObject
    {
        if (parent == null) return null;

        if (parent is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is T typedChild && (predicate == null || predicate(typedChild)))
                    return typedChild;

                var result = FindChildElement<T>(child, predicate);
                if (result != null) return result;
            }
        }
        else if (parent is ContentControl cc && cc.Content is DependencyObject contentDep)
        {
            if (contentDep is T typedContent && (predicate == null || predicate(typedContent)))
                return typedContent;

            var result = FindChildElement<T>(contentDep, predicate);
            if (result != null) return result;
        }

        try
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild && (predicate == null || predicate(typedChild)))
                    return typedChild;

                var result = FindChildElement<T>(child, predicate);
                if (result != null) return result;
            }
        }
        catch { }

        return null;
    }
}
