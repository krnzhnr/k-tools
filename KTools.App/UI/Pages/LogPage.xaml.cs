// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;

using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Models;
using KTools_App.Services.Contracts;
using KTools_App.ViewModels;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace KTools_App.UI.Pages;

public sealed partial class LogPage
{
    private const string SourceName = nameof(LogPage);
    private const int PendingQueueCapacity = 4096;
    private const long PendingQueueMaxBytes = 4L * 1024 * 1024;
    private const int BatchIntervalMilliseconds = 200;

    private readonly LogUiSession _session = new(PendingQueueCapacity, PendingQueueMaxBytes);

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _logBatchTimer;
    private ILogService? _logService;
    private EventHandler<LogEvent>? _logReceivedHandler;
    private long _generation;
    private long _timerGeneration;

    public LogViewModel ViewModel { get; }

    public LogPage()
    {
        ViewModel = App.Services.GetRequiredService<LogViewModel>();
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
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

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_session.IsCurrent(generation))
            {
                ScrollToEnd();
            }
        });
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        long generation = _generation;
        _session.Deactivate(generation);

        if (_logService is not null && _logReceivedHandler is not null)
        {
            _logService.LogReceived -= _logReceivedHandler;
            _logService.Write(
                "ui.log_page.closed",
                LogLevel.Debug,
                LogStatus.Succeeded,
                "Панель журнала событий закрыта",
                source: SourceName);
        }

        _logService = null;
        _logReceivedHandler = null;

        if (_logBatchTimer is { IsRunning: true })
        {
            _logBatchTimer.Stop();
        }
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
            FlushPendingLogs(generation);
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
        ViewModel.AddLogs(items);
        ScrollToEnd();
    }

    private void ScrollToEnd()
    {
        try
        {
            if (ViewModel.Logs.Count > 0)
            {
                LogListView.ScrollIntoView(ViewModel.Logs[ViewModel.Logs.Count - 1]);
            }
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogService>().Write(
                "ui.log_page.scroll_failed",
                LogLevel.Warning,
                LogStatus.Failed,
                "Список журнала не прокручен до последнего события",
                ex,
                SourceName,
                properties: LogProps.Create("ErrorCode", "LOG_SCROLL_FAILED").With("Control", "LogListView"));
        }
    }

    private void CopySelectedLogs_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
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
