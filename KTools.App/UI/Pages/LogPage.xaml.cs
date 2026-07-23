// -*- coding: utf-8 -*-
using System;
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

    /// <summary>
    /// Инициализирует новый экземпляр LogPage, разрешая зависимости через DI.
    /// </summary>
    public LogPage()
    {
        ViewModel = App.Services.GetRequiredService<LogViewModel>();
        InitializeComponent();
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
    }

    /// <summary>
    /// Обработчик события поступления нового сообщения лога. Перенаправляет добавление записи в поток UI.
    /// </summary>
    private void OnLogReceived(object? sender, LogReceivedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ViewModel.AddLog(e.FormattedMessage, e.Level);
            ScrollToEnd();
        });
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
