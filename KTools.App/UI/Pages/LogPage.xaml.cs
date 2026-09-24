// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Extensions.DependencyInjection;
using KTools_App.Core;
using KTools_App.Services.Contracts;
using KTools_App.ViewModels;

namespace KTools_App.UI.Pages;

/// <summary>
/// Класс логики (Code-Behind) для страницы логов LogPage.
/// Осуществляет координацию подписок на события логирования и автопрокрутку списка в интерфейсе.
/// </summary>
public sealed partial class LogPage : Page
{
    /// <summary>
    /// Предоставляет доступ к модели представления страницы логов.
    /// </summary>
    public LogViewModel ViewModel { get; }

    private readonly List<KTools_App.Models.LogItem> _pendingLogs = new();
    private readonly object _pendingLock = new();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _logBatchTimer;

    /// <summary>
    /// Инициализирует новый экземпляр LogPage, разрешая зависимости через DI.
    /// </summary>
    public LogPage()
    {
        ViewModel = App.Services.GetRequiredService<LogViewModel>();
        InitializeComponent();

        // Страница кэшируется навигационным фреймом: список логов (до 2000 строк)
        // не пересоздается при каждом возвращении на вкладку.
        NavigationCacheMode = NavigationCacheMode.Required;
    }

    /// <summary>
    /// Вызывается при переходе на страницу логов. Загружает историю и подписывается на событие получения логов.
    /// </summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // Загружаем сохраненную историю логов во ViewModel
        ViewModel.LoadLogs();

        // Подписываемся на новые логи
        LogService.LogReceived += OnLogReceived;

        App.Services.GetRequiredService<ILogService>().DebugLog("Открыта высокопроизводительная вкладка логов с поддержкой виртуализации списка", "LogPage");

        // Прокручиваем список в самый конец после рендеринга элементов
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            ScrollToEnd();
        });
    }

    /// <summary>
    /// Вызывается при переходе со страницы логов. Гарантированно отписывается от событий во избежание утечек памяти.
    /// </summary>
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        LogService.LogReceived -= OnLogReceived;
        App.Services.GetRequiredService<ILogService>().DebugLog("Пользователь покинул вкладку мониторинга логов", "LogPage");

        // Накопленные записи не сбрасываем в список при уходе со страницы:
        // при следующем открытии вкладки OnNavigatedTo заново загружает журнал
        // из файла (ViewModel.LoadLogs), поэтому синхронное обновление ListView
        // на 2000 элементов в момент навигации только задерживало переход.
        if (_logBatchTimer is { IsRunning: true })
        {
            _logBatchTimer.Stop();
        }

        // Буфер очищаем: его содержимое уже будет прочитано из файла при следующем входе,
        // иначе после загрузки истории записи продублировались бы в списке.
        lock (_pendingLock)
        {
            _pendingLogs.Clear();
        }
    }

    /// <summary>
    /// Обработчик события поступления нового сообщения лога. Перенаправляет добавление записи в поток UI.
    /// </summary>
    private void OnLogReceived(object? sender, LogReceivedEventArgs e)
    {
        lock (_pendingLock)
        {
            _pendingLogs.Add(new KTools_App.Models.LogItem { Message = e.FormattedMessage, Level = e.Level });
        }

        bool isEnqueued = DispatcherQueue.TryEnqueue(() =>
        {
            if (_logBatchTimer == null)
            {
                _logBatchTimer = DispatcherQueue.CreateTimer();
                _logBatchTimer.Interval = TimeSpan.FromMilliseconds(200);
                _logBatchTimer.Tick += (s, args) => FlushPendingLogs();
            }

            if (!_logBatchTimer.IsRunning)
            {
                _logBatchTimer.Start();
            }
        });

        if (!isEnqueued)
        {
            DispatcherQueue.TryEnqueue(() => FlushPendingLogs());
        }
    }

    /// <summary>
    /// Добавляет накопленные логи в ViewModel одной пачкой и прокручивает список к последнему элементу.
    /// </summary>
    private void FlushPendingLogs()
    {
        if (_logBatchTimer is { IsRunning: true })
        {
            _logBatchTimer.Stop();
        }

        List<KTools_App.Models.LogItem> batch;
        lock (_pendingLock)
        {
            if (_pendingLogs.Count == 0)
            {
                return;
            }
            batch = new List<KTools_App.Models.LogItem>(_pendingLogs);
            _pendingLogs.Clear();
        }

        ViewModel.AddLogs(batch);
        ScrollToEnd();
    }

    /// <summary>
    /// Прокручивает виртуализированный список логов к самому последнему элементу.
    /// </summary>
    private void ScrollToEnd()
    {
        try
        {
            if (ViewModel.Logs.Count > 0)
            {
                var lastItem = ViewModel.Logs[^1];
                LogListView.ScrollIntoView(lastItem);
            }
        }
        catch (Exception ex)
        {
            // Используем системную отладку для предотвращения бесконечных циклов логирования
            System.Diagnostics.Debug.WriteLine($"[Error] Ошибка при прокрутке ListView к последней строке: {ex.Message}");
        }
    }

    /// <summary>
    /// Копирует выделенные пользователем строки лога в системный буфер обмена Windows.
    /// </summary>
    private void CopySelectedLogs_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        CopySelectedLogs();
    }

    /// <summary>
    /// Выделяет все записи логов в списке.
    /// </summary>
    private void SelectAllLogs_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        LogListView.SelectAll();
    }

    /// <summary>
    /// Обработчик нажатия горячих клавиш в списке логов (Ctrl+C — копировать выделенное, Ctrl+A — выделить все).
    /// </summary>
    private void LogListView_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        var ctrlState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
        bool isCtrlPressed = (ctrlState & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;

        if (isCtrlPressed)
        {
            if (e.Key == Windows.System.VirtualKey.C)
            {
                CopySelectedLogs();
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.A)
            {
                LogListView.SelectAll();
                e.Handled = true;
            }
        }
    }

    /// <summary>
    /// Вспомогательный метод для копирования элементов или вызова полного копирования при отсутствии выделения.
    /// </summary>
    private void CopySelectedLogs()
    {
        var selected = System.Linq.Enumerable.ToList(System.Linq.Enumerable.OfType<KTools_App.Models.LogItem>(LogListView.SelectedItems));
        if (selected.Count > 0)
        {
            ViewModel.CopySelectedLogs(selected);
        }
        else
        {
            ViewModel.CopyAllLogsCommand.Execute(null);
        }
    }
}
