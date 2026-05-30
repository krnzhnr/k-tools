// -*- coding: utf-8 -*-
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using KTools_App.Core;

namespace KTools_App.UI.Pages;

/// <summary>
/// Представитель слоя представления (ViewModel) для связывания данных конкретной зависимости с XAML-интерфейсом.
/// Реализует интерфейс INotifyPropertyChanged для мгновенного визуального обновления статуса, прогресса и скорости.
/// </summary>
public class DependencyVM : INotifyPropertyChanged
{
    private DependencyStatus _status;
    private int _progress;
    private string _speed = string.Empty;
    private string _errorMessage = string.Empty;

    /// <summary>Ссылка на базовую модель данных зависимости.</summary>
    public DependencyInfo Info { get; }

    /// <summary>Текущий статус установки/загрузки.</summary>
    public DependencyStatus Status
    {
        get => _status;
        set
        {
            if (_status != value)
            {
                _status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(StatusColor));
                OnPropertyChanged(nameof(IsExtracting));
                OnPropertyChanged(nameof(ProgressVisibility));
                OnPropertyChanged(nameof(SubStatusVisibility));
                OnPropertyChanged(nameof(SubStatusText));
                OnPropertyChanged(nameof(InstallButtonVisibility));
                OnPropertyChanged(nameof(CancelButtonVisibility));
                OnPropertyChanged(nameof(DeleteButtonVisibility));
            }
        }
    }

    /// <summary>Текущий прогресс скачивания в процентах (0-100).</summary>
    public int Progress
    {
        get => _progress;
        set
        {
            if (_progress != value)
            {
                _progress = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Текущая скорость скачивания.</summary>
    public string Speed
    {
        get => _speed;
        set
        {
            if (_speed != value)
            {
                _speed = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SubStatusText));
            }
        }
    }

    /// <summary>Сообщение об ошибке, возникшей при установке.</summary>
    public string ErrorMessage
    {
        get => _errorMessage;
        set
        {
            if (_errorMessage != value)
            {
                _errorMessage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SubStatusText));
            }
        }
    }

    /// <summary>Форматированный текст размера компонента.</summary>
    public string SizeText => $"Размер на диске: ~{Info.SizeMb:F1} МБ";

    /// <summary>Локализованный текст статуса зависимости.</summary>
    public string StatusText => Status switch
    {
        DependencyStatus.Installed => "Установлено",
        DependencyStatus.NotInstalled => "Не установлено",
        DependencyStatus.Downloading => "Загрузка архива...",
        DependencyStatus.Extracting => "Распаковка архива...",
        DependencyStatus.Error => "Ошибка установки",
        _ => "Неизвестный статус"
    };

    /// <summary>Цветовое оформление индикатора статуса в зависимости от состояния.</summary>
    public Brush StatusColor => Status switch
    {
        DependencyStatus.Installed => new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x10, 0x7C, 0x41)), // Насыщенный зеленый
        DependencyStatus.Error => new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0xE8, 0x11, 0x23)),      // Красный
        DependencyStatus.Downloading => new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x00, 0x78, 0xD4)), // Fluent-синий
        DependencyStatus.Extracting => new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x00, 0x78, 0xD4)),  // Fluent-синий
        _ => new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x7A, 0x7A, 0x7A))                             // Нейтральный серый
    };

    /// <summary>Указывает, выполняется ли в данный момент распаковка архива.</summary>
    public bool IsExtracting => Status == DependencyStatus.Extracting;

    /// <summary>Определяет видимость прогресс-бара.</summary>
    public Visibility ProgressVisibility => 
        (Status == DependencyStatus.Downloading || Status == DependencyStatus.Extracting) 
        ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Определяет видимость дополнительного текстового блока скорости/ошибки.</summary>
    public Visibility SubStatusVisibility => 
        (Status == DependencyStatus.Downloading || Status == DependencyStatus.Error) 
        ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Дополнительный текст состояния (скорость загрузки или сообщение об ошибке).</summary>
    public string SubStatusText => Status switch
    {
        DependencyStatus.Downloading => Speed,
        DependencyStatus.Error => ErrorMessage,
        _ => string.Empty
    };

    /// <summary>Определяет видимость кнопки установки.</summary>
    public Visibility InstallButtonVisibility => 
        (Status == DependencyStatus.NotInstalled || Status == DependencyStatus.Error) 
        ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Определяет видимость кнопки отмены.</summary>
    public Visibility CancelButtonVisibility => 
        (Status == DependencyStatus.Downloading || Status == DependencyStatus.Extracting) 
        ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Определяет видимость кнопки удаления.</summary>
    public Visibility DeleteButtonVisibility => 
        Status == DependencyStatus.Installed ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Инициализирует новый экземпляр ViewModel для зависимости.</summary>
    public DependencyVM(DependencyInfo info)
    {
        Info = info;
        _status = DependencyManager.Instance.GetStatus(info.Key);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// Класс логики (Code-Behind) для страницы управления и настройки внешних бинарных зависимостей.
/// Координирует вызовы DependencyManager с реактивным обновлением интерфейса.
/// </summary>
public sealed partial class DependencySetupPage : Page
{
    private readonly ObservableCollection<DependencyVM> _requiredDependencies = new();
    private readonly ObservableCollection<DependencyVM> _optionalDependencies = new();

    /// <summary>
    /// Инициализирует новый экземпляр страницы DependencySetupPage.
    /// </summary>
    public DependencySetupPage()
    {
        InitializeComponent();

        RequiredItemsControl.ItemsSource = _requiredDependencies;
        OptionalItemsControl.ItemsSource = _optionalDependencies;

        LoadDependencies();

        // Подписка на глобальные события изменения состояния из DependencyManager
        DependencyManager.Instance.StatusChanged += OnStatusChanged;
        DependencyManager.Instance.ProgressChanged += OnProgressChanged;
        DependencyManager.Instance.SpeedUpdated += OnSpeedUpdated;
        DependencyManager.Instance.InstallFinished += OnInstallFinished;

        Unloaded += OnPageUnloaded;
    }

    /// <summary>
    /// Извлекает список зависимостей из менеджера и распределяет их по группам обязательных/дополнительных.
    /// </summary>
    private void LoadDependencies()
    {
        _requiredDependencies.Clear();
        _optionalDependencies.Clear();

        var registry = DependencyManager.Instance.GetRegistry();
        foreach (var dep in registry)
        {
            var vm = new DependencyVM(dep);
            if (dep.IsRequired)
            {
                _requiredDependencies.Add(vm);
            }
            else
            {
                _optionalDependencies.Add(vm);
            }
        }
        UpdateInstallAllButtonState();
        UpdateRefreshButtonState();
    }

    /// <summary>
    /// Обновляет доступность кнопки пакетной установки в зависимости от наличия ненайденных компонентов.
    /// </summary>
    private void UpdateInstallAllButtonState()
    {
        bool hasMissing = _requiredDependencies.Concat(_optionalDependencies)
            .Any(d => d.Status == DependencyStatus.NotInstalled || d.Status == DependencyStatus.Error);
        
        InstallAllButton.IsEnabled = hasMissing;
    }

    /// <summary>
    /// Динамически настраивает визуальное отображение и поведение кнопки проверки.
    /// Если все обязательные компоненты загружены, кнопка трансформируется в кнопку "На главную" с иконкой Home.
    /// </summary>
    private void UpdateRefreshButtonState()
    {
        bool hasRequired = DependencyManager.Instance.AreRequiredDependenciesInstalled();
        if (hasRequired)
        {
            RefreshIcon.Symbol = Symbol.Home;
            RefreshText.Text = "На главную";
        }
        else
        {
            RefreshIcon.Symbol = Symbol.Refresh;
            RefreshText.Text = "Проверить заново";
        }
    }

    private DependencyVM? FindViewModel(string key)
    {
        return _requiredDependencies.Concat(_optionalDependencies)
            .FirstOrDefault(d => d.Info.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    private void OnStatusChanged(string key, DependencyStatus status)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var vm = FindViewModel(key);
            if (vm != null)
            {
                vm.Status = status;
                UpdateInstallAllButtonState();
                UpdateRefreshButtonState();
            }
        });
    }

    private void OnProgressChanged(string key, int percent)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var vm = FindViewModel(key);
            if (vm != null)
            {
                vm.Progress = percent;
            }
        });
    }

    private void OnSpeedUpdated(string key, string speedStr)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var vm = FindViewModel(key);
            if (vm != null)
            {
                vm.Speed = speedStr;
            }
        });
    }

    private void OnInstallFinished(string key, bool success, string errorMsg)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var vm = FindViewModel(key);
            if (vm != null)
            {
                if (!success)
                {
                    vm.ErrorMessage = errorMsg;
                }
                UpdateInstallAllButtonState();
                UpdateRefreshButtonState();
                CheckAndRedirectToHome();
            }
        });
    }

    /// <summary>
    /// Проверяет, установлены ли все обязательные бинарники.
    /// Если да, автоматически выполняет переход на главную страницу (HomePage).
    /// </summary>
    private void CheckAndRedirectToHome()
    {
        if (DependencyManager.Instance.AreRequiredDependenciesInstalled())
        {
            // Получаем корневой Frame навигации приложения
            if (Frame != null)
            {
                // Находим родительский MainPage и переключаем пункт меню на Главная
                var mainPage = FindParentPage<MainPage>(this);
                if (mainPage != null)
                {
                    mainPage.NavigateToHomeExternally();
                }
                else
                {
                    Frame.Navigate(typeof(HomePage));
                }
            }
        }
    }

    private static T? FindParentPage<T>(DependencyObject child) where T : DependencyObject
    {
        var parentObject = VisualTreeHelper.GetParent(child);
        if (parentObject == null) return null;
        if (parentObject is T parent) return parent;
        return FindParentPage<T>(parentObject);
    }

    /// <summary>
    /// Обработчик клика по кнопке Установить на отдельной карточке.
    /// </summary>
    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is DependencyVM vm)
        {
            await DependencyManager.Instance.InstallDependencyAsync(vm.Info.Key);
        }
    }

    /// <summary>
    /// Обработчик клика по кнопке Отмена.
    /// </summary>
    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is DependencyVM vm)
        {
            DependencyManager.Instance.CancelInstallation(vm.Info.Key);
        }
    }

    /// <summary>
    /// Обработчик клика по кнопке Удалить.
    /// </summary>
    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is DependencyVM vm)
        {
            DependencyManager.Instance.RemoveDependency(vm.Info.Key);
        }
    }

    /// <summary>
    /// Обработчик клика по кнопке пакетной установки всех отсутствующих утилит.
    /// </summary>
    private async void InstallAllButton_Click(object sender, RoutedEventArgs e)
    {
        InstallAllButton.IsEnabled = false;

        var missing = _requiredDependencies.Concat(_optionalDependencies)
            .Where(d => d.Status == DependencyStatus.NotInstalled || d.Status == DependencyStatus.Error)
            .ToList();

        var tasks = missing.Select(vm => DependencyManager.Instance.InstallDependencyAsync(vm.Info.Key));
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Обработчик клика по кнопке перепроверки бинарников.
    /// Если все обязательные компоненты успешно установлены, кнопка принудительно осуществляет навигацию пользователя на домашний экран.
    /// </summary>
    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        DependencyManager.Instance.RefreshAllStatuses();
        LoadDependencies();
        
        // В случае наличия всех обязательных зависимостей на диске, осуществляем безусловное перенаправление
        if (DependencyManager.Instance.AreRequiredDependenciesInstalled())
        {
            var mainPage = FindParentPage<MainPage>(this);
            if (mainPage != null)
            {
                mainPage.NavigateToHomeExternally();
            }
            else
            {
                Frame?.Navigate(typeof(HomePage));
            }
        }
    }

    /// <summary>
    /// Обработчик клика по кнопке открытия локальной директории bin в Проводнике Windows.
    /// Гарантирует автоматическое создание директории, если она отсутствует на физическом диске.
    /// </summary>
    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        string binDir = PathManager.GetBinDirectory();

        try
        {
            // Создаем целевую директорию, если она физически отсутствует на накопителе
            if (!Directory.Exists(binDir))
            {
                Directory.CreateDirectory(binDir);
            }

            // Запускаем стандартный системный Проводник Windows для визуального отображения директории
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{binDir}\"",
                UseShellExecute = true
            });
        }
        catch (Exception)
        {
            // Игнорируем любые непредвиденные системные сбои при запуске внешнего процесса Проводника
        }
    }

    /// <summary>
    /// Обработчик события наведения указателя мыши на карточку зависимости.
    /// Слегка понижает прозрачность карточки для создания визуального отклика наведения.
    /// </summary>
    private void Card_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            border.Opacity = 0.85;
        }
    }

    /// <summary>
    /// Обработчик события ухода указателя мыши с карточки зависимости.
    /// Возвращает стандартную стопроцентную непрозрачность карточки.
    /// </summary>
    private void Card_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            border.Opacity = 1.0;
        }
    }

    /// <summary>
    /// Освобождение ресурсов при переходе со страницы.
    /// </summary>
    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        DependencyManager.Instance.StatusChanged -= OnStatusChanged;
        DependencyManager.Instance.ProgressChanged -= OnProgressChanged;
        DependencyManager.Instance.SpeedUpdated -= OnSpeedUpdated;
        DependencyManager.Instance.InstallFinished -= OnInstallFinished;
    }
}
