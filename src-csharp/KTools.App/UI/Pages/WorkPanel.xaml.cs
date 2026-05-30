// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using KTools_App.Core;
using KTools_App.UI.Controls;

namespace KTools_App.UI.Pages;

/// <summary>
/// Универсальная страница выполнения конкретного скрипта медиаобработки.
/// Управляет ходом выполнения, считывает очереди файлов, выводит логи,
/// отслеживает индивидуальный и интегральный прогресс, обрабатывает отмену.
/// </summary>
public sealed partial class WorkPanel : Page
{
    private AbstractScript? _script;
    private bool _isProcessing;
    private bool _isRestoringQueue;

    // Контейнеры контента для горизонтального NavigationView
    private Grid _filesContainer = null!;
    private ScriptSettingsControl _settingsControl = null!;

    // Динамические ссылки на элементы управления для сохранения совместимости с кодом запуска
    private FileListControl FileList = null!;
    private Expander LogExpander = null!;
    private TextBox LogTextBox = null!;
    private ScriptSettingsControl ScriptSettings = null!;

    public WorkPanel()
    {
        InitializeComponent();
        InitializeTabs();
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

        // Список файлов очереди с Drag-and-Drop
        FileList = new FileListControl
        {
            Margin = new Thickness(0, 0, 0, 12),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        Grid.SetRow(FileList, 0);
        _filesContainer.Children.Add(FileList);

        // Разворачиваемый лог на базе Expander
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

        // 2. Создаем контейнер настроек
        _settingsControl = new ScriptSettingsControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        ScriptSettings = _settingsControl;
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
            ScriptSettings.GenerateSettingsUI(_script);

            // Отписываемся от старой подписки для предотвращения дублирования
            FileList.Files.CollectionChanged -= Files_CollectionChanged;

            _isRestoringQueue = true;
            try
            {
                // Восстанавливаем сохраненную очередь файлов для скрипта
                FileList.Clear();
                if (_script.SavedFiles.Count > 0)
                {
                    FileList.AddSavedFiles(_script.SavedFiles);
                }

                // Восстанавливаем текст журнала выполнения (лога)
                LogTextBox.Text = _script.SavedLogText ?? string.Empty;
                if (!string.IsNullOrEmpty(LogTextBox.Text))
                {
                    LogExpander.IsExpanded = true;
                }

                // Восстанавливаем глобальный статус и прогресс-бар
                StatusTextBlock.Text = _script.SavedStatusText ?? 
                    "Ожидание запуска...";
                GlobalProgressBar.Value = _script.SavedGlobalProgress;
            }
            finally
            {
                _isRestoringQueue = false;
            }

            // Подписываемся на изменения для синхронизации
            // очереди в реальном времени
            FileList.Files.CollectionChanged += Files_CollectionChanged;

            // Если настроек нет, скрываем вторую вкладку из верхнего NavigationView меню
            if (_script.SettingsSchema == null ||
                _script.SettingsSchema.Count == 0)
            {
                nvSample.MenuItems.Remove(SamplePage2Item);
            }

            // По умолчанию принудительно выбираем первую вкладку «Файлы»
            nvSample.SelectedItem = SamplePage1Item;

            // Выполняем проверку бинарных зависимостей скрипта
            CheckDependencies();
        }
    }

    /// <summary>
    /// Обработчик переключения горизонтальных вкладок верхнего NavigationView.
    /// Динамически подменяет контент фрейма contentFrame без пересоздания виджетов.
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
            }
            else if (tag == "settings")
            {
                contentFrame.Content = _settingsControl;
            }
        }
    }

    /// <summary>
    /// Выполнить валидацию внешних бинарных утилит, необходимых для работы скрипта.
    /// </summary>
    private bool CheckDependencies()
    {
        if (_script == null) return false;

        bool allInstalled = true;
        var missingDeps = new List<string>();

        foreach (var depKey in _script.RequiredDependencies)
        {
            if (!DependencyManager.Instance.IsInstalled(depKey))
            {
                allInstalled = false;
                missingDeps.Add(depKey.ToUpperInvariant());
            }
        }

        if (!allInstalled)
        {
            // Показываем предупреждение и блокируем запуск
            DependencyWarningBar.Message = 
                $"Для работы требуются отсутствующие компоненты: {string.Join(", ", missingDeps)}";
            DependencyWarningBar.IsOpen = true;
            StartButton.IsEnabled = false;
        }
        else
        {
            DependencyWarningBar.IsOpen = false;
            StartButton.IsEnabled = true;
        }

        return allInstalled;
    }

    private void InstallDependencies_Click(object sender, RoutedEventArgs e)
    {
        // Переход на страницу установки зависимостей через публичный метод роутинга
        var mainPage = FindParentPage<MainPage>(this);
        if (mainPage != null)
        {
            mainPage.NavigateToDependenciesExternally();
        }
    }

    private async void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        
        // Настройка сопоставления с главным окном в WinUI 3
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.CurrentMainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
        picker.FileTypeFilter.Add("*");

        var folder = await picker.PickSingleFolderAsync();
        if (folder != null)
        {
            OutputPathTextBox.Text = folder.Path;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_script != null && _isProcessing)
        {
            StatusTextBlock.Text = "Отмена выполнения...";
            _script.Cancel();
        }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_script == null || _isProcessing) return;

        var files = FileList.Files.ToList();
        if (files.Count == 0)
        {
            AppendLog("❌ Ошибка: В очереди нет файлов для обработки.\r\n");
            return;
        }

        // Блокируем интерфейс перед началом
        SetUiProcessingState(true);
        LogTextBox.Text = string.Empty;
        LogExpander.IsExpanded = true; // Автоматически раскрываем лог при старте
        GlobalProgressBar.Value = 0;
        StatusTextBlock.Text = "Подготовка к обработке...";

        _script.ResetCancellation();
        string? outputPath = string.IsNullOrEmpty(OutputPathTextBox.Text) ? null : OutputPathTextBox.Text;

        AppendLog($"🚀 Запуск скрипта '{_script.Name}' для {files.Count} файлов.\r\n");

        await Task.Run(async () =>
        {
            int total = files.Count;
            var settings = ReadCurrentSettings();

            for (int i = 0; i < total; i++)
            {
                if (_script.IsCancelled)
                {
                    break;
                }

                var fileItem = files[i];
                
                // Обновляем статус в UI
                UpdateFileStatusInUi(fileItem, "Обработка...", 0.0);
                UpdateGlobalProgress(i, total, $"Обработка файла {i + 1} из {total}: {fileItem.FileName}", 0.0);

                try
                {
                    // Создаем прокси-callback для отправки прогресса обработки
                    Action<int, int, string, double?> progressCallback = (currIdx, totCount, msg, percent) =>
                    {
                        UpdateFileStatusInUi(fileItem, msg, percent ?? 0.0);
                        UpdateGlobalProgress(i, total, $"Файл {i + 1} из {total}: {fileItem.FileName} ({percent:F0}%)", percent ?? 0.0);
                    };

                    // Асинхронное выполнение скрипта
                    var results = await _script.ExecuteSingleAsync(
                        fileItem.FilePath,
                        settings,
                        outputPath,
                        progressCallback,
                        i,
                        total);

                    // Вывод логов в UI
                    AppendLogFromThread(results);
                    
                    if (_script.IsCancelled)
                    {
                        UpdateFileStatusInUi(fileItem, "Отменено", 0.0);
                        break;
                    }

                    UpdateFileStatusInUi(fileItem, "Завершено", 100.0);
                }
                catch (Exception ex)
                {
                    UpdateFileStatusInUi(fileItem, "Ошибка", 0.0);
                    AppendLogFromThread(new List<string> { $"❌ Критическая ошибка: {ex.Message}" });
                }
            }

            // Финализация обработки
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_script.IsCancelled)
                {
                    AppendLog("⚠ Обработка прервана пользователем.\r\n");
                    StatusTextBlock.Text = "Обработка отменена";
                    GlobalProgressBar.Value = 0;
                }
                else
                {
                    AppendLog("🎉 Все файлы успешно обработаны.\r\n");
                    StatusTextBlock.Text = "Обработка завершена";
                    GlobalProgressBar.Value = 100;
                }

                SetUiProcessingState(false);
            });
        });
    }

    private Dictionary<string, object> ReadCurrentSettings()
    {
        var settings = new Dictionary<string, object>();
        if (_script == null) return settings;

        string settingsGroup = SettingsManager.Instance.GetSafeGroupName(_script.Name);
        foreach (var field in _script.SettingsSchema)
        {
            if (field.Type == SettingType.Subtitle) continue;

            object val = SettingsManager.Instance.GetSetting(settingsGroup, field.Key, field.DefaultValue);
            settings[field.Key] = val;
        }

        return settings;
    }

    private void SetUiProcessingState(bool processing)
    {
        _isProcessing = processing;
        FileList.IsEnabled = true;
        ScriptSettings.IsEnabled = !processing;
        OutputPathTextBox.IsEnabled = !processing;
        StartButton.IsEnabled = !processing;
        CancelButton.Visibility = processing
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateFileStatusInUi(FileQueueItem item, string status, double progress)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            item.Status = status;
            item.Progress = progress;
        });
    }

    private void UpdateGlobalProgress(int completedCount, int totalCount, string statusText, double filePercent)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            StatusTextBlock.Text = statusText;
            double progress = (completedCount * 100.0 + filePercent) / totalCount;
            GlobalProgressBar.Value = progress;
        });
    }

    private void AppendLog(string message)
    {
        LogTextBox.Text += message;
        // Автопрокрутка
        LogTextBox.SelectionStart = LogTextBox.Text.Length;
        LogTextBox.SelectionLength = 0;
    }

    private void AppendLogFromThread(List<string> lines)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            foreach (var line in lines)
            {
                AppendLog($"{line}\r\n");
            }
        });
    }

    private T? FindParentPage<T>(DependencyObject child) where T : DependencyObject
    {
        DependencyObject parentObject = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(child);
        if (parentObject == null) return null;

        if (parentObject is T parent)
        {
            return parent;
        }
        
        return FindParentPage<T>(parentObject);
    }

    /// <summary>
    /// Метод жизненного цикла страницы, вызываемый при навигации с нее.
    /// </summary>
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        
        if (FileList != null && FileList.Files != null)
        {
            FileList.Files.CollectionChanged -= Files_CollectionChanged;
        }

        // Гарантированно сохраняем полное состояние перед уходом
        if (_script != null && FileList != null && FileList.Files != null)
        {
            _script.SavedFiles.Clear();
            foreach (var item in FileList.Files)
            {
                _script.SavedFiles.Add(new SavedFileState
                {
                    FilePath = item.FilePath,
                    Status = item.Status,
                    Progress = item.Progress
                });
            }

            _script.SavedLogText = LogTextBox.Text;
            _script.SavedStatusText = StatusTextBlock.Text;
            _script.SavedGlobalProgress = GlobalProgressBar.Value;
        }
    }

    /// <summary>
    /// Гарантированная отписка от событий изменения коллекции при выгрузке.
    /// </summary>
    private void WorkPanel_Unloaded(object sender, RoutedEventArgs e)
    {
        if (FileList != null && FileList.Files != null)
        {
            FileList.Files.CollectionChanged -= Files_CollectionChanged;
        }
    }

    /// <summary>
    /// Синхронизирует пути к файлам в очереди с SavedFiles активного скрипта.
    /// </summary>
    private void Files_CollectionChanged(
        object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_isRestoringQueue) return;

        if (_script != null)
        {
            _script.SavedFiles.Clear();
            foreach (var item in FileList.Files)
            {
                _script.SavedFiles.Add(new SavedFileState
                {
                    FilePath = item.FilePath,
                    Status = item.Status,
                    Progress = item.Progress
                });
            }
        }
    }
}
