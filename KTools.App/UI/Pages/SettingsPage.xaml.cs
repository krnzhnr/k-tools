// -*- coding: utf-8 -*-
using System;

using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Services.Contracts;
using KTools_App.ViewModels;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Windows.Storage.Pickers;

using WinRT.Interop;

namespace KTools_App.UI.Pages;

/// <summary>
/// Класс логики (Code-Behind) для страницы настроек SettingsPage.
/// Инициализирует биндинги к SettingsViewModel и обрабатывает выбор папки логов.
/// </summary>
public partial class SettingsPage : Page
{
    private const string SourceName = nameof(SettingsPage);
    private static readonly TimeSpan HelpFlyoutRebuildDelay = TimeSpan.FromMilliseconds(400);

    private ISettingsManager _settingsManager => App.Services.GetRequiredService<ISettingsManager>();
    private IDialogService _dialogService => App.Services.GetRequiredService<IDialogService>();
    private ILogService _logService => App.Services.GetRequiredService<ILogService>();

    private List<TemplateItem> _searchTemplates = new();
    private List<TemplateItem> _replaceTemplates = new();
    private DispatcherTimer? _helpFlyoutRebuildTimer;
    private TextBlock? _commitStatusBlock;

    /// <summary>
    /// Предоставляет доступ к модели представления страницы настроек.
    /// </summary>
    public SettingsViewModel ViewModel { get; }

    /// <summary>
    /// Инициализирует новый экземпляр SettingsPage, разрешая зависимости через DI.
    /// </summary>
    public SettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
        this.Loaded += SettingsPage_Loaded;

        AttachCommitStatusHost();
        SubscribeToCommitStatus();
        UpdateCommitStatus(ViewModel.LastCommitStatusText);

        // Сброс фокуса при клике на свободную область страницы
        this.PointerPressed += (s, e) =>
        {
            this.IsTabStop = true;
            this.Focus(FocusState.Programmatic);
        };

        this.Unloaded += (s, e) => UnsubscribeFromCommitStatus();
    }

    private void SubscribeToCommitStatus()
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void UnsubscribeFromCommitStatus()
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    /// <summary>
    /// Размещает строку статуса фиксации настроек над первым разделом страницы.
    /// Разметка XAML не меняется: строка добавляется в код-behind.
    /// </summary>
    private void AttachCommitStatusHost()
    {
        if (RootScrollViewer.Content is not StackPanel root)
        {
            return;
        }

        _commitStatusBlock = new TextBlock
        {
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 4),
            TextWrapping = TextWrapping.Wrap
        };

        root.Children.Insert(0, _commitStatusBlock);
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.LastCommitStatusText))
        {
            UpdateCommitStatus(ViewModel.LastCommitStatusText);
        }
    }

    private void UpdateCommitStatus(string text)
    {
        if (_commitStatusBlock == null)
        {
            return;
        }

        _commitStatusBlock.Text = string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : text;

        if (ViewModel.LastCommitState == SettingCommitState.Failed &&
            Application.Current.Resources.TryGetValue("SystemFillColorCriticalBrush", out object? failedBrush) &&
            failedBrush is Microsoft.UI.Xaml.Media.Brush failure)
        {
            _commitStatusBlock.Foreground = failure;
            return;
        }

        if (Application.Current.Resources.TryGetValue("TextFillColorSecondaryBrush", out object? secondaryBrush) &&
            secondaryBrush is Microsoft.UI.Xaml.Media.Brush normal)
        {
            _commitStatusBlock.Foreground = normal;
            return;
        }

        _commitStatusBlock.ClearValue(TextBlock.ForegroundProperty);
    }

    /// <summary>
    /// Вызывается при навигации на страницу настроек.
    /// Обрабатывает параметр навигации для скролла к блоку обновлений.
    /// </summary>
    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string action && action == "scroll_to_updates")
        {
            this.Loaded += (s, ev) =>
            {
                UpdatesSection.StartBringIntoView(new BringIntoViewOptions
                {
                    VerticalAlignmentRatio = 0.0
                });
            };
        }
    }

    /// <summary>
    /// Обработчик клика по кнопке выбора директории для логов.
    /// Требует HWND главного окна приложения для открытия системного FolderPicker в WinUI 3.
    /// </summary>
    private async void BrowseLogDirButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folderPicker = new FolderPicker();

            // Получаем HWND главного окна для интеграции COM
            var hwnd = WindowNative.GetWindowHandle(App.CurrentMainWindow);
            InitializeWithWindow.Initialize(folderPicker, hwnd);

            folderPicker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
            folderPicker.FileTypeFilter.Add("*");

            var folder = await folderPicker.PickSingleFolderAsync();
            if (folder != null)
            {
                PersistenceResult result = ViewModel.SetLogDirectory(folder.Path);
                if (result.IsSuccess && result.Persisted)
                {
                    await _dialogService.ShowMessageAsync(
                        "Каталог сохранён",
                        PersistenceSummaries.NextLaunchOnly);
                    return;
                }

                await _dialogService.ShowMessageAsync(
                    "Каталог не сохранён",
                    $"{PersistenceSummaries.NextLaunchLost} Код ошибки: {result.ErrorCode}. {result.UserSummary}");
            }
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogService>().Write("ui.settings.log_directory_picker_failed", LogLevel.Warning, LogStatus.Failed, "Каталог журналирования не выбран", ex, nameof(SettingsPage), properties: LogProps.Create("ErrorCode", "LOG_DIRECTORY_PICKER_FAILED"));
            await _dialogService.ShowMessageAsync(
                "Ошибка выбора директории",
                "Не удалось открыть окно выбора папки. Подробности записаны в журнал.");
        }
    }

    /// <summary>
    /// Обработчик клика по переменной форматирования. Копирует ее значение в буфер обмена
    /// и показывает временный статус «Скопировано!».
    /// </summary>
    private void CopyVariableButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.CommandParameter is string varText)
        {
            try
            {
                var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dataPackage.SetText(varText);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);

                Grid btnGrid;
                FrameworkElement normalContent;
                TextBlock copiedContent;

                if (btn.Content is Grid existingGrid && existingGrid.Tag?.ToString() == "CopiedWrapperGrid")
                {
                    btnGrid = existingGrid;
                    normalContent = (FrameworkElement)btnGrid.Children[0];
                    copiedContent = (TextBlock)btnGrid.Children[1];
                }
                else
                {
                    btnGrid = new Grid { Tag = "CopiedWrapperGrid", HorizontalAlignment = HorizontalAlignment.Stretch };
                    normalContent = (FrameworkElement)btn.Content;

                    // Безопасное получение ресурса темы с проверкой типа
                    Microsoft.UI.Xaml.Media.Brush? accentBrush = null;
                    if (Application.Current.Resources.TryGetValue("AccentTextFillColorPrimaryBrush", out var brushObj))
                    {
                        accentBrush = brushObj as Microsoft.UI.Xaml.Media.Brush;
                    }

                    copiedContent = new TextBlock
                    {
                        Text = "Скопировано!",
                        FontSize = 12,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Visibility = Visibility.Collapsed
                    };
                    if (accentBrush != null)
                    {
                        copiedContent.Foreground = accentBrush;
                    }

                    btn.Content = null;
                    btnGrid.Children.Add(normalContent);
                    btnGrid.Children.Add(copiedContent);
                    btn.Content = btnGrid;
                }

                normalContent.Opacity = 0;
                copiedContent.Visibility = Visibility.Visible;

                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                timer.Tick += (s2, ev) =>
                {
                    copiedContent.Visibility = Visibility.Collapsed;
                    normalContent.Opacity = 1;
                    timer.Stop();
                };
                timer.Start();
            }
            catch (Exception ex)
            {
                App.Services.GetRequiredService<ILogService>().Write("ui.clipboard.write_failed", LogLevel.Warning, LogStatus.Failed, "Значение не скопировано в буфер обмена", ex, nameof(SettingsPage), properties: LogProps.Create("ErrorCode", "CLIPBOARD_WRITE_FAILED"));
            }
        }
    }

    private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        SubscribeToCommitStatus();
        UpdateCommitStatus(ViewModel.LastCommitStatusText);
        LoadTemplatesUI();
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

    private void LoadTemplatesUI()
    {
        // Менеджер отдаёт глубокую копию: правки идут в локальный рабочий список,
        // а в настройки попадают только явной фиксацией с проверкой PersistenceResult.
        _searchTemplates = _settingsManager.SearchTemplates;
        _replaceTemplates = _settingsManager.ReplaceTemplates;

        SearchTemplatesContainer.Children.Clear();
        foreach (TemplateItem item in _searchTemplates)
        {
            item.Description = CleanDescription(item.Description);
            SearchTemplatesContainer.Children.Add(CreateTemplateRow(item, true));
        }

        ReplaceTemplatesContainer.Children.Clear();
        foreach (TemplateItem item in _replaceTemplates)
        {
            item.Description = CleanDescription(item.Description);
            ReplaceTemplatesContainer.Children.Add(CreateTemplateRow(item, false));
        }

        UpdateHelpFlyouts();
    }

    /// <summary>
    /// Фиксирует отложенные правки шаблонов одной записью и проверяет подтверждение диска.
    /// Вызывается при потере фокуса полем шаблона, а не на каждый keystroke.
    /// Дополнительный SaveSettings не выполняется: SetSetting сам фиксирует на диск
    /// (или планирует одну отложенную запись), поэтому правка не вызывает двойной fsync.
    /// </summary>
    /// <param name="isSearch">Шаблоны поиска (true) или шаблоны замены (false).</param>
    private void PersistTemplates(bool isSearch)
    {
        string key = isSearch ? "SearchTemplates" : "ReplaceTemplates";
        List<TemplateItem> working = isSearch ? _searchTemplates : _replaceTemplates;

        PersistenceResult result = PersistenceResult.Normalize(
            _settingsManager.SetSetting("General", key, working));

        if (result.IsSuccess && result.Persisted)
        {
            UpdateCommitStatus($"Шаблоны {(isSearch ? "поиска" : "замены")}: сохранено.");
            return;
        }

        if (result.IsPending)
        {
            UpdateCommitStatus(
                $"Шаблоны {(isSearch ? "поиска" : "замены")}: применено в текущем сеансе, запись на диск отложена.");
            return;
        }

        ReportTemplateCommitFailure(key, result);
    }

    private void ReportTemplateCommitFailure(string key, PersistenceResult result)
    {
        UpdateCommitStatus($"Шаблоны не сохранены ({result.ErrorCode}). {result.UserSummary}");
        _logService.Write(SettingsEventIds.DefaultsDeferred, LogLevel.Warning, LogStatus.Changed, $"Шаблон применён только в текущем сеансе: настройка {LogRedactor.CompactSafeToken(key)} не сохранена", source: SourceName, properties: LogProps.Create("Key", LogRedactor.CompactSafeToken(key)).With("Persisted", false).With("ErrorCode", result.ErrorCode));
    }

    private FrameworkElement CreateTemplateRow(TemplateItem item, bool isSearch)
    {
        var rowGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 2, 0, 2) };
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) }); // Шаблон
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Описание
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Кнопка удаления

        var patternBox = new TextBox
        {
            Text = item.Pattern,
            PlaceholderText = isSearch ? "Шаблон (например, \\d+)" : "Шаблон (например, $1 или ${num})",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Height = 32,
            Margin = new Thickness(0, 0, 8, 0)
        };
        patternBox.TextChanged += (s, e) =>
        {
            item.Pattern = patternBox.Text;
            ScheduleHelpFlyoutRebuild();
        };
        patternBox.LostFocus += (s, e) => PersistTemplates(isSearch);
        Grid.SetColumn(patternBox, 0);
        rowGrid.Children.Add(patternBox);

        var descBox = new TextBox
        {
            Text = item.Description,
            PlaceholderText = "Описание (например, поиск серии)",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Height = 32,
            Margin = new Thickness(0, 0, 8, 0)
        };
        descBox.TextChanged += (s, e) =>
        {
            item.Description = CleanDescription(descBox.Text);
            ScheduleHelpFlyoutRebuild();
        };
        descBox.LostFocus += (s, e) => PersistTemplates(isSearch);
        Grid.SetColumn(descBox, 1);
        rowGrid.Children.Add(descBox);

        var deleteBtn = new Button
        {
            Content = new FontIcon { Glyph = "\uE74D", FontSize = 12 },
            Style = (Style)Application.Current.Resources["DefaultButtonStyle"],
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center
        };
        deleteBtn.Click += (s, e) =>
        {
            if (isSearch)
            {
                _searchTemplates.Remove(item);
            }
            else
            {
                _replaceTemplates.Remove(item);
            }

            PersistTemplates(isSearch);
            LoadTemplatesUI();
        };
        Grid.SetColumn(deleteBtn, 2);
        rowGrid.Children.Add(deleteBtn);

        return rowGrid;
    }

    private void AddSearchTemplate_Click(object sender, RoutedEventArgs e)
    {
        _searchTemplates.Add(new TemplateItem { Pattern = "", Description = "" });
        PersistTemplates(true);
        LoadTemplatesUI();
    }

    private void AddReplaceTemplate_Click(object sender, RoutedEventArgs e)
    {
        _replaceTemplates.Add(new TemplateItem { Pattern = "", Description = "" });
        PersistTemplates(false);
        LoadTemplatesUI();
    }

    /// <summary>
    /// Откладывает перестроение всплывающих подсказок: полное дерево кнопок
    /// пересобирается один раз после паузы в наборе, а не на каждый keystroke.
    /// </summary>
    private void ScheduleHelpFlyoutRebuild()
    {
        if (_helpFlyoutRebuildTimer == null)
        {
            _helpFlyoutRebuildTimer = new DispatcherTimer { Interval = HelpFlyoutRebuildDelay };
            _helpFlyoutRebuildTimer.Tick += (s, e) =>
            {
                _helpFlyoutRebuildTimer.Stop();
                UpdateHelpFlyouts();
            };
        }

        _helpFlyoutRebuildTimer.Stop();
        _helpFlyoutRebuildTimer.Start();
    }

    private void UpdateHelpFlyouts()
    {
        if (SearchHelpButton != null) AttachSearchHelpFlyout(SearchHelpButton);
        if (ReplaceHelpButton != null) AttachReplaceHelpFlyout(ReplaceHelpButton);
    }

    private void AttachSearchHelpFlyout(Button button)
    {
        var flyout = new Flyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight };
        var mainStack = new StackPanel { Spacing = 6, Width = 280 };

        // Безопасное получение ресурсов темы с проверкой типа
        Application.Current.Resources.TryGetValue("AccentTextFillColorPrimaryBrush", out var accentBrushObj);
        var accentBrush = accentBrushObj as Microsoft.UI.Xaml.Media.Brush;
        Application.Current.Resources.TryGetValue("TextFillColorSecondaryBrush", out var secondaryBrushObj);
        var secondaryBrush = secondaryBrushObj as Microsoft.UI.Xaml.Media.Brush;

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
            Margin = new Thickness(0, 0, 0, 8)
        };
        if (secondaryBrush != null) desc.Foreground = secondaryBrush;

        mainStack.Children.Add(title);
        mainStack.Children.Add(desc);

        foreach (TemplateItem item in _searchTemplates)
        {
            if (string.IsNullOrEmpty(item.Pattern)) continue;

            var rowGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var insertBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uE710", FontSize = 10 },
                Width = 32,
                Height = 32,
                Margin = new Thickness(0, 0, 6, 0),
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center
            };
            insertBtn.Click += (s, e) =>
            {
                RenameRegexSearchTextBox.Text += item.Pattern;
                RenameRegexSearchTextBox.Focus(FocusState.Programmatic);
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
            var patternText = new TextBlock
            {
                Text = item.Pattern,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            };
            if (accentBrush != null) patternText.Foreground = accentBrush;
            rowStack.Children.Add(patternText);
            rowStack.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(item.Description) ? "" : $"— {CleanDescription(item.Description)}", FontSize = 12 });

            var copiedText = new TextBlock
            {
                Text = "Скопировано!",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            if (accentBrush != null) copiedText.Foreground = accentBrush;

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

    private void AttachReplaceHelpFlyout(Button button)
    {
        var flyout = new Flyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight };
        var mainStack = new StackPanel { Spacing = 6, Width = 280 };

        // Безопасное получение ресурсов темы с проверкой типа
        Application.Current.Resources.TryGetValue("AccentTextFillColorPrimaryBrush", out var accentBrushObj);
        var accentBrush = accentBrushObj as Microsoft.UI.Xaml.Media.Brush;
        Application.Current.Resources.TryGetValue("TextFillColorSecondaryBrush", out var secondaryBrushObj);
        var secondaryBrush = secondaryBrushObj as Microsoft.UI.Xaml.Media.Brush;

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
            Margin = new Thickness(0, 0, 0, 8)
        };
        if (secondaryBrush != null) desc.Foreground = secondaryBrush;

        mainStack.Children.Add(title);
        mainStack.Children.Add(desc);

        foreach (TemplateItem item in _replaceTemplates)
        {
            if (string.IsNullOrEmpty(item.Pattern)) continue;

            var rowGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var insertBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uE710", FontSize = 10 },
                Width = 32,
                Height = 32,
                Margin = new Thickness(0, 0, 6, 0),
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center
            };
            insertBtn.Click += (s, e) =>
            {
                RenameRegexReplaceTextBox.Text += item.Pattern;
                RenameRegexReplaceTextBox.Focus(FocusState.Programmatic);
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
            var patternText = new TextBlock
            {
                Text = item.Pattern,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            };
            if (accentBrush != null) patternText.Foreground = accentBrush;
            rowStack.Children.Add(patternText);
            rowStack.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(item.Description) ? "" : $"— {CleanDescription(item.Description)}", FontSize = 12 });

            var copiedText = new TextBlock
            {
                Text = "Скопировано!",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            if (accentBrush != null) copiedText.Foreground = accentBrush;

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
}
