// -*- coding: utf-8 -*-
using System;
using KTools_App.Services.Contracts;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Extensions.DependencyInjection;
using Windows.Storage.Pickers;
using WinRT.Interop;
using KTools_App.Core;
using KTools_App.UI.Controls;
using KTools_App.ViewModels;
using KTools_App.Scripts;

namespace KTools_App.UI.Pages;

/// <summary>
/// Класс логики (Code-Behind) универсальной рабочей панели для выполнения скриптов медиаобработки.
/// Управляет динамическим переключением вкладок (Файлы, Дорожки, Настройки),
/// считывает параметры из UI-компонентов и передает их во ViewModel.
/// </summary>
public sealed partial class WorkPanel : Page
{
    private ISettingsManager _settingsManager => App.Services.GetRequiredService<ISettingsManager>();

    private AbstractScript? _script;

    // Контейнеры контента для горизонтального NavigationView
    private Grid _filesContainer = null!;
    private TrackSelectionControl _tracksControl = null!;
    private StreamReplaceControl? _streamReplaceControl;
    private ScriptSettingsControl _settingsControl = null!;

    // Динамический элемент навигации для вкладки "Дорожки"
    private NavigationViewItem? _tracksPageItem;

    // Ссылки на элементы управления для совместимости
    private FileListControl FileList = null!;
    private Expander LogExpander = null!;
    private TextBox LogTextBox = null!;
    private ScriptSettingsControl ScriptSettings = null!;

    /// <summary>
    /// Предоставляет доступ к модели представления рабочей панели.
    /// </summary>
    public WorkPanelViewModel ViewModel { get; }

    /// <summary>
    /// Инициализирует новый экземпляр WorkPanel, разрешая зависимости через DI.
    /// </summary>
    public WorkPanel()
    {
        ViewModel = App.Services.GetRequiredService<WorkPanelViewModel>();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        InitializeTabs();
        InitializeComponent();

        Unloaded += WorkPanel_Unloaded;
    }

    /// <summary>
    /// Программное создание и разметка контента вкладок для предотвращения наложений и повышения скорости работы.
    /// </summary>
    private void InitializeTabs()
    {
        // 1. Создаем контейнер Файлов (Grid с двумя строками)
        _filesContainer = new Grid();
        _filesContainer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _filesContainer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        FileList = new FileListControl
        {
            Margin = new Thickness(0, 0, 0, 12),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        Grid.SetRow(FileList, 0);
        _filesContainer.Children.Add(FileList);

        LogExpander = new Expander
        {
            Header = "Журнал выполнения",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            IsExpanded = false,
            Margin = new Thickness(0, 0, 0, 4)
        };
        Grid.SetRow(LogExpander, 1);

        LogTextBox = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            Height = 140,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
            PlaceholderText = "Здесь будет отображаться лог работы утилит...",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        ScrollViewer.SetVerticalScrollBarVisibility(LogTextBox, ScrollBarVisibility.Auto);
        LogExpander.Content = LogTextBox;
        _filesContainer.Children.Add(LogExpander);

        // 2. Создаем виджет выбора дорожек
        _tracksControl = new TrackSelectionControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        // 3. Создаем контейнер настроек
        _settingsControl = new ScriptSettingsControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        ScriptSettings = _settingsControl;
    }

    private Grid? _urlInputBar;
    private TextBox? _urlTextBox;

    private Grid CreateUrlInputBar()
    {
        var grid = new Grid
        {
            Margin = new Thickness(0, 0, 0, 12),
            ColumnSpacing = 8
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _urlTextBox = new TextBox
        {
            PlaceholderText = "Введите URL-адрес для скачивания (например, с YouTube, SoundCloud)...",
            Height = 36,
            VerticalAlignment = VerticalAlignment.Center
        };
        _urlTextBox.KeyDown += (s, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                AddUrlFromInput();
            }
        };
        Grid.SetColumn(_urlTextBox, 0);
        grid.Children.Add(_urlTextBox);

        var pasteBtn = new Button
        {
            Content = new SymbolIcon(Symbol.Paste),
            Height = 36,
            Width = 36,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(pasteBtn, "Вставить из буфера");
        pasteBtn.Click += async (s, e) =>
        {
            try
            {
                var dataPackageView = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                if (dataPackageView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                {
                    string text = await dataPackageView.GetTextAsync();
                    _urlTextBox.Text = text;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка буфера обмена: {ex.Message}");
            }
        };
        Grid.SetColumn(pasteBtn, 1);
        grid.Children.Add(pasteBtn);

        var addBtn = new Button
        {
            Content = new SymbolIcon(Symbol.Add),
            Height = 36,
            Width = 36,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(addBtn, "Добавить в очередь");
        addBtn.Click += (s, e) => AddUrlFromInput();
        Grid.SetColumn(addBtn, 2);
        grid.Children.Add(addBtn);

        return grid;
    }

    private void AddUrlFromInput()
    {
        if (_urlTextBox == null) return;
        string url = _urlTextBox.Text.Trim();
        if (!string.IsNullOrEmpty(url))
        {
            FileList.AddUrl(url);
            _urlTextBox.Text = string.Empty;
        }
    }

    /// <summary>
    /// Метод жизненного цикла страницы, вызываемый при навигации на нее.
    /// </summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is AbstractScript script)
        {
            _script = script;
            
            // Связываем скрипт со списком файлов и генерируем его параметры настроек
            FileList.ActiveScript = _script;
            FileList.SetFiles(_script.FilesQueue);
            ScriptSettings.GenerateSettingsUI(_script);

            // Инициализируем ViewModel активным скриптом и коллекцией файлов
            ViewModel.Initialize(_script, FileList.Files);



            // Динамически управляем вкладкой "Дорожки"
            if (_script.UseCustomWidget)
            {
                if (_tracksPageItem == null)
                {
                    _tracksPageItem = new NavigationViewItem
                    {
                        Content = "Дорожки",
                        Icon = new SymbolIcon(Symbol.SelectAll),
                        Tag = "tracks"
                    };
                }

                if (!nvSample.MenuItems.Contains(_tracksPageItem))
                {
                    nvSample.MenuItems.Insert(1, _tracksPageItem);
                }

                if (_script is StreamReplacementScript)
                {
                    if (_streamReplaceControl == null)
                    {
                        _streamReplaceControl = new StreamReplaceControl
                        {
                            HorizontalAlignment = HorizontalAlignment.Stretch,
                            VerticalAlignment = VerticalAlignment.Stretch
                        };
                    }
                    _streamReplaceControl.ActiveScript = _script;
                    _streamReplaceControl.Populate(FileList.Files);
                }
                else
                {
                    _tracksControl.ActiveScript = _script;
                    _tracksControl.Populate(FileList.Files);
                }
            }
            else
            {
                if (_tracksPageItem != null && nvSample.MenuItems.Contains(_tracksPageItem))
                {
                    nvSample.MenuItems.Remove(_tracksPageItem);
                }
            }

            // Динамически управляем кнопкой предпросмотра для скрипта субтитров
            if (_script is SubtitlesConvertScript)
            {
                PreviewButton.Visibility = Visibility.Visible;
            }
            else
            {
                PreviewButton.Visibility = Visibility.Collapsed;
            }

            // Динамически управляем вкладкой "Настройки"
            var schemaList = _script.GetFullSettingsSchema();
            if (schemaList == null || schemaList.Count == 0)
            {
                nvSample.MenuItems.Remove(SamplePage2Item);
            }
            else
            {
                if (!nvSample.MenuItems.Contains(SamplePage2Item))
                {
                    nvSample.MenuItems.Add(SamplePage2Item);
                }
            }

            // Настройка имени первой вкладки и отображения URL-панели
            SamplePage1Item.Content = _script.FirstTabHeader;

            if (_script.ShowUrlInputBar)
            {
                if (_urlInputBar == null)
                {
                    _urlInputBar = CreateUrlInputBar();
                }

                if (!_filesContainer.Children.Contains(_urlInputBar))
                {
                    _filesContainer.RowDefinitions.Clear();
                    _filesContainer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // URL
                    _filesContainer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // FileList
                    _filesContainer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // LogExpander

                    Grid.SetRow(_urlInputBar, 0);
                    Grid.SetRow(FileList, 1);
                    Grid.SetRow(LogExpander, 2);

                    _filesContainer.Children.Add(_urlInputBar);
                }
                _urlInputBar.Visibility = Visibility.Visible;
            }
            else
            {
                if (_urlInputBar != null)
                {
                    _urlInputBar.Visibility = Visibility.Collapsed;
                }

                _filesContainer.RowDefinitions.Clear();
                _filesContainer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                _filesContainer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                Grid.SetRow(FileList, 0);
                Grid.SetRow(LogExpander, 1);
            }

            // По умолчанию выбираем первую вкладку «Файлы» (или «Загрузка»)
            nvSample.SelectedItem = SamplePage1Item;

            // Инициализируем состояние кнопки запуска/отмены в соответствии с текущим состоянием обработки
            UpdateActionButtonState(ViewModel.IsProcessing);

            // Инициализируем режим блокировки списка файлов
            FileList.IsProcessing = ViewModel.IsProcessing;
        }
    }

    /// <summary>
    /// Обработчик изменения свойств ViewModel в Code-Behind.
    /// Синхронизирует UI-специфичные элементы (Expander, TextBox, скроллинг),
    /// которые не поддерживают прямое связывание данных WinUI.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkPanelViewModel.LogText))
        {
            LogTextBox.Text = ViewModel.LogText;
            LogTextBox.SelectionStart = LogTextBox.Text.Length;
            LogTextBox.SelectionLength = 0;
        }
        else if (e.PropertyName == nameof(WorkPanelViewModel.IsLogExpanded))
        {
            LogExpander.IsExpanded = ViewModel.IsLogExpanded;
        }
        else if (e.PropertyName == nameof(WorkPanelViewModel.IsProcessing))
        {
            // Блокируем контролы во время обработки
            bool isProcessing = ViewModel.IsProcessing;
            FileList.IsProcessing = isProcessing;
            ScriptSettings.SetProcessingMode(isProcessing);
            
            if (isProcessing)
            {
                // Перекидываем пользователя на вкладку Файлы
                nvSample.SelectedItem = SamplePage1Item;
            }

            // Синхронизируем состояние кнопки запуска/отмены при изменении статуса обработки
            UpdateActionButtonState(isProcessing);
        }
        else if (e.PropertyName == nameof(WorkPanelViewModel.IsStartButtonEnabled))
        {
            // Обновляем доступность кнопки запуска в режиме ожидания
            if (!ViewModel.IsProcessing)
            {
                ActionButton.IsEnabled = ViewModel.IsStartButtonEnabled;
            }
        }
    }

    /// <summary>
    /// Обработчик переключения горизонтальных вкладок верхнего NavigationView.
    /// Подменяет контент фрейма без пересоздания виджетов.
    /// </summary>
    private void nvSample_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer is NavigationViewItem selectedItem)
        {
            string tag = selectedItem.Tag?.ToString() ?? string.Empty;
            if (tag == "files")
            {
                contentFrame.Content = _filesContainer;
                FileList.NotifySubtitlesSettingChanged();
            }
            else if (tag == "tracks")
            {
                if (_script is StreamReplacementScript)
                {
                    contentFrame.Content = _streamReplaceControl;
                }
                else
                {
                    contentFrame.Content = _tracksControl;
                }
            }
            else if (tag == "settings")
            {
                contentFrame.Content = _settingsControl;
            }
        }
    }

    private void InstallDependencies_Click(object sender, RoutedEventArgs e)
    {
        var mainPage = FindParentPage<MainPage>(this);
        if (mainPage != null)
        {
            mainPage.NavigateToDependenciesExternally();
        }
    }

    /// <summary>
    /// Обработчик выбора директории сохранения результатов.
    /// </summary>
    private async void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker();
            
            // Настройка сопоставления с главным окном в WinUI 3
            var hwnd = WindowNative.GetWindowHandle(App.CurrentMainWindow);
            InitializeWithWindow.Initialize(picker, hwnd);

            picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
            picker.FileTypeFilter.Add("*");

            var folder = await picker.PickSingleFolderAsync();
            if (folder != null)
            {
                ViewModel.OutputPath = folder.Path;
            }
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogService>().Error($"Не удалось открыть окно выбора папки: {ex.Message}", "WorkPanel");
        }
    }

    /// <summary>
    /// Обработчик клика по кнопке-переключателю (Выполнить/Отменить).
    /// </summary>
    private void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_script == null) return;

        if (ViewModel.IsProcessing)
        {
            // Если процесс запущен, выполняем его отмену
            if (ViewModel.CancelExecutionCommand.CanExecute(null))
            {
                ViewModel.CancelExecutionCommand.Execute(null);
            }
        }
        else
        {
            // Если процесс не запущен, собираем настройки из UI и запускаем команду ViewModel на выполнение
            var settings = ReadCurrentSettings();
            if (_script.UseCustomWidget)
            {
                if (_script is StreamReplacementScript)
                {
                    if (_streamReplaceControl != null)
                    {
                        _streamReplaceControl.GetReplacements(out var replacements);
                        settings["replacements"] = replacements;
                    }
                }
                else
                {
                    // Выбор дорожек передается во ViewModel автоматически через WeakReferenceMessenger
                }
            }

            if (ViewModel.StartExecutionCommand.CanExecute(settings))
            {
                ViewModel.StartExecutionCommand.Execute(settings);
            }
        }
    }

    /// <summary>
    /// Обновляет текстовое содержимое, стиль оформления и доступность кнопки-переключателя
    /// в зависимости от того, запущена ли в данный момент обработка.
    /// </summary>
    private void UpdateActionButtonState(bool isProcessing)
    {
        if (isProcessing)
        {
            ActionButton.Content = "Отменить";
            ActionButton.Style = (Style)Application.Current.Resources["DefaultButtonStyle"];
            ActionButton.IsEnabled = true;
        }
        else
        {
            ActionButton.Content = "Выполнить";
            ActionButton.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            ActionButton.IsEnabled = ViewModel.IsStartButtonEnabled;
        }
    }

    /// <summary>
    /// Собирает текущие настройки скрипта из SettingsManager на основе схемы.
    /// </summary>
    private Dictionary<string, object> ReadCurrentSettings()
    {
        var settings = new Dictionary<string, object>();
        if (_script == null) return settings;

        string settingsGroup = _settingsManager.GetSafeGroupName(_script.Name);
        foreach (var field in _script.GetFullSettingsSchema())
        {
            if (field.Type == SettingType.Subtitle) continue;

            object val = _settingsManager.GetSetting(settingsGroup, field.Key, field.DefaultValue);
            settings[field.Key] = val;
        }

        return settings;
    }

    private T? FindParentPage<T>(DependencyObject child) where T : DependencyObject
    {
        DependencyObject parentObject = VisualTreeHelper.GetParent(child);
        if (parentObject == null) return null;

        if (parentObject is T parent)
        {
            return parent;
        }
        
        return FindParentPage<T>(parentObject);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        
        ViewModel.SaveState();
    }

    /// <summary>
    /// Освобождает ресурсы при выгрузке страницы.
    /// </summary>
    private void WorkPanel_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }



    /// <summary>
    /// Обработчик клика по кнопке "Предпросмотр...".
    /// Выполняет фоновый парсинг файлов и открывает окно предпросмотра с интерактивными фильтрами.
    /// </summary>
    private async void PreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (_script is not SubtitlesConvertScript subScript) return;

        var filePaths = FileList.Files.Select(f => f.FilePath).ToList();
        if (filePaths.Count == 0)
        {
            App.Services.GetRequiredService<ILogService>().Warn("Попытка открыть предпросмотр без добавленных файлов субтитров.", "WorkPanel");
            return;
        }

        PreviewButton.IsEnabled = false;

        try
        {
            // Синхронизируем FilterState с текущими настройками из SettingsManager перед открытием
            var settingsManager = App.Services.GetRequiredService<ISettingsManager>();
            string settingsGroup = settingsManager.GetSafeGroupName(subScript.Name);
            subScript.FilterState.StripFormatting = settingsManager.GetSetting(settingsGroup, "strip_formatting", true);
            subScript.FilterState.StripCaps = settingsManager.GetSetting(settingsGroup, "strip_caps", false);

            var viewModel = new SubtitlePreviewViewModel(subScript.FilterState, settingsGroup);
            await viewModel.LoadDataAsync(filePaths);

            var previewWindow = new SubtitlePreviewWindow(viewModel);
            previewWindow.Closed += (s, ev) =>
            {
                // При закрытии окна предпросмотра принудительно перерисовываем UI настроек,
                // чтобы актуализировать чекбоксы очистки форматирования и капса на вкладке параметров.
                App.CurrentMainWindow?.DispatcherQueue?.TryEnqueue(() =>
                {
                    if (_script != null)
                    {
                        ScriptSettings.GenerateSettingsUI(_script);
                    }
                });
            };
            previewWindow.Activate();
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogService>().Exception(
                ex,
                $"Критический сбой при инициализации или открытии окна предпросмотра субтитров: {ex.Message}",
                "WorkPanel");
        }
        finally
        {
            PreviewButton.IsEnabled = true;
        }
    }
}
