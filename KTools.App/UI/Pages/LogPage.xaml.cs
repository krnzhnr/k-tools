// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;

using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Models;
using KTools_App.Services.Contracts;
using KTools_App.ViewModels;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace KTools_App.UI.Pages;

public sealed partial class LogPage
{
    private const string SourceName = nameof(LogPage);
    private const int PendingQueueCapacity = 4096;
    private const long PendingQueueMaxBytes = 4L * 1024 * 1024;
    private const int BatchIntervalMilliseconds = 200;
    private const double AutoScrollEndTolerance = 2.0;

    private readonly LogUiSession _session = new(PendingQueueCapacity, PendingQueueMaxBytes);

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _logBatchTimer;
    private ILogService? _logService;
    private EventHandler<LogEvent>? _logReceivedHandler;
    private long _generation;
    private long _timerGeneration;
    private ScrollViewer? _logScrollViewer;
    private bool _followTail = true;

    public LogViewModel ViewModel { get; }

    public LogPage()
    {
        ViewModel = App.Services.GetRequiredService<LogViewModel>();
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
        Unloaded += LogPage_Unloaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (_logService is not null && _logReceivedHandler is not null)
        {
            _logService.LogReceived -= _logReceivedHandler;
        }

        if (_logBatchTimer is { IsRunning: true })
        {
            _logBatchTimer.Stop();
        }

        ILogService logService = App.Services.GetRequiredService<ILogService>();
        _logService = logService;
        long generation = _session.Activate();
        _generation = generation;
        EventHandler<LogEvent> handler = (_, logEvent) => OnLogReceived(generation, logEvent);
        _logReceivedHandler = handler;
        logService.LogReceived += handler;

        IReadOnlyList<LogEvent> snapshot = ViewModel.LoadLogs();
        _session.RegisterSnapshot(generation, snapshot);
        _session.PurgeSnapshotDuplicates(generation, snapshot.Select(static logEvent => logEvent.CorrelationKey));

        logService.Write("ui.log_page.opened", LogLevel.Debug, LogStatus.Succeeded, "Панель журнала событий открыта", source: SourceName);

        _followTail = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_session.IsCurrent(generation))
            {
                EnsureLogScrollTracking();
                ScrollToEnd();
            }
        });
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ShutdownBatching(writeClosedMarker: true);
    }

    private void LogPage_Unloaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        // Закрытие окна не вызывает OnNavigatedFrom: таймер иначе добивает
        // в уже disposed ServiceProvider и падает в дебаге.
        ShutdownBatching(writeClosedMarker: false);
    }

    private void ShutdownBatching(bool writeClosedMarker)
    {
        long generation = _generation;
        _session.Deactivate(generation);
        DetachLogScrollTracking();

        if (_logBatchTimer is { IsRunning: true })
        {
            _logBatchTimer.Stop();
        }

        if (_logService is not null && _logReceivedHandler is not null)
        {
            try
            {
                _logService.LogReceived -= _logReceivedHandler;
            }
            catch (ObjectDisposedException)
            {
            }

            if (writeClosedMarker)
            {
                try
                {
                    _logService.Write(
                        "ui.log_page.closed",
                        LogLevel.Debug,
                        LogStatus.Succeeded,
                        "Панель журнала событий закрыта",
                        source: SourceName);
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        _logService = null;
        _logReceivedHandler = null;
    }

    private void OnLogReceived(long generation, LogEvent logEvent)
    {
        if (!_session.TryAdd(generation, logEvent) || !_session.TryBeginBatch(generation))
        {
            return;
        }

        if (!DispatcherQueue.TryEnqueue(() => StartBatchTimer(generation)))
        {
            _session.EndBatch(generation);
        }
    }

    private void StartBatchTimer(long generation)
    {
        if (!_session.IsCurrent(generation))
        {
            _session.EndBatch(generation);
            return;
        }

        if (_logBatchTimer is null || _timerGeneration != generation)
        {
            if (_logBatchTimer is { IsRunning: true })
            {
                _logBatchTimer.Stop();
            }

            _logBatchTimer = DispatcherQueue.CreateTimer();
            _logBatchTimer.Interval = TimeSpan.FromMilliseconds(BatchIntervalMilliseconds);
            _timerGeneration = generation;
            _logBatchTimer.Tick += (_, _) => OnBatchTick(generation);
        }

        if (!_logBatchTimer.IsRunning)
        {
            _logBatchTimer.Start();
        }
    }

    private void OnBatchTick(long generation)
    {
        if (_session.IsCurrent(generation))
        {
            try
            {
                FlushPendingLogs(generation);
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private void FlushPendingLogs(long generation)
    {
        if (_logBatchTimer is { IsRunning: true })
        {
            _logBatchTimer.Stop();
        }

        if (!_session.TryDrain(generation, out IReadOnlyList<LogItem> batch, out long dropped))
        {
            return;
        }

        if (batch.Count == 0 && dropped == 0)
        {
            return;
        }

        List<LogItem> items = new(batch.Count + 1);
        if (dropped > 0)
        {
            items.Add(LogItem.CreateMarker(
                $"Пропущено событий журнала из-за переполнения очереди интерфейса: {dropped}",
                LogLevel.Warning,
                dropped,
                Guid.Empty,
                LogEventMarkerNames.DroppedMarker));
        }

        items.AddRange(batch);
        bool followTail = ShouldFollowTail();
        ViewModel.AddLogs(items);
        if (followTail)
        {
            ScrollToEnd();
        }
    }

    private void ScrollToEnd()
    {
        try
        {
            if (ViewModel.Logs.Count > 0)
            {
                LogListView.ScrollIntoView(ViewModel.Logs[ViewModel.Logs.Count - 1]);
            }
        }        catch (ObjectDisposedException)
        {
            // Гашение при закрытии: провайдер уже disposed, логировать некуда.
        }
        catch (Exception ex)
        {
            try
            {
                _logService?.Write(
                    "ui.log_page.scroll_failed",
                    LogLevel.Warning,
                    LogStatus.Failed,
                    "Список журнала не прокручен до последнего события",
                    ex,
                    SourceName,
                    properties: LogProps.Create("ErrorCode", "LOG_SCROLL_FAILED").With("Control", "LogListView"));
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    // Умный автоскролл: новый батч тянет список вниз только если пользователь
    // уже находится у конца. Чтение середины журнала не срывается.
    private void EnsureLogScrollTracking()
    {
        if (_logScrollViewer is not null)
        {
            return;
        }

        ScrollViewer? viewer = FindVisualChild<ScrollViewer>(LogListView);
        if (viewer is null)
        {
            return;
        }

        _logScrollViewer = viewer;
        _logScrollViewer.ViewChanged += LogScrollViewer_ViewChanged;
        _followTail = true;
    }

    private void DetachLogScrollTracking()
    {
        if (_logScrollViewer is not null)
        {
            _logScrollViewer.ViewChanged -= LogScrollViewer_ViewChanged;
            _logScrollViewer = null;
        }
    }

    private void LogScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        _followTail = IsScrolledToEnd();
    }

    private bool ShouldFollowTail()
    {
        EnsureLogScrollTracking();
        if (_logScrollViewer is null)
        {
            return _followTail;
        }

        _followTail = IsScrolledToEnd();
        return _followTail;
    }

    private bool IsScrolledToEnd()
    {
        ScrollViewer? viewer = _logScrollViewer;
        if (viewer is null)
        {
            return true;
        }

        if (viewer.ScrollableHeight <= 0)
        {
            return true;
        }

        return viewer.VerticalOffset + viewer.ViewportHeight >= viewer.ScrollableHeight - AutoScrollEndTolerance;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed)
            {
                return typed;
            }

            T? nested = FindVisualChild<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private void CopySelectedLogs_Click(object sender, RoutedEventArgs e)
    {
        CopySelectedLogs();
    }

    private void SelectAllLogs_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        LogListView.SelectAll();
    }

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

    private void CopySelectedLogs()
    {
        var selected = System.Linq.Enumerable.ToList(System.Linq.Enumerable.OfType<LogItem>(LogListView.SelectedItems));
        ViewModel.CopySelectedLogs(selected);
    }
}
