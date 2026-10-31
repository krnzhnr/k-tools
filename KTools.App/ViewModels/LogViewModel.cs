// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Models;
using KTools_App.Services.Contracts;

using Windows.ApplicationModel.DataTransfer;

namespace KTools_App.ViewModels;

public partial class LogViewModel : ThreadSafeViewModel
{
    private const string SourceName = nameof(LogViewModel);
    public const int RecentEventsLimit = 1000;
    public const int RingLimit = 2000;
    public const int ExportEventLimit = 2000;
    public const int ExportCharLimit = 512 * 1024;

    private readonly ILogService _logService;
    private readonly ISettingsManager _settingsManager;
    private readonly IPathManager _pathManager;

    public ObservableRangeCollection<LogItem> Logs { get; } = new();

    public LogViewModel(
        ILogService logService,
        ISettingsManager settingsManager,
        IPathManager pathManager)
    {
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
        _pathManager = pathManager ?? throw new ArgumentNullException(nameof(pathManager));
    }

    public IReadOnlyList<LogEvent> LoadLogs()
    {
        try
        {
            IReadOnlyList<LogEvent>? events = _logService.ReadRecentEvents(RecentEventsLimit);
            if (events is null)
            {
                events = Array.Empty<LogEvent>();
            }

            Logs.ReplaceRange(events.Select(LogItem.FromEvent));
            _logService.Write(
                "log.history.loaded",
                LogLevel.Debug,
                LogStatus.Succeeded,
                "История журнала событий загружена в панель",
                source: SourceName,
                properties: LogProps.Create("Count", events.Count));
            return events;
        }
        catch (Exception ex)
        {
            _logService.Write(
                "log.history.load_failed",
                LogLevel.Error,
                LogStatus.Failed,
                "Не удалось загрузить историю журнала событий",
                ex,
                SourceName,
                properties: LogProps.Create("ErrorCode", "LOG_HISTORY_LOAD_FAILED"));
            return Array.Empty<LogEvent>();
        }
    }

    public void AddLogs(IEnumerable<LogItem> items)
    {
        if (items is null)
        {
            return;
        }

        try
        {
            Logs.AddRange(items);
            TrimToLimit();
        }
        catch (Exception ex)
        {
            _logService.Write(
                "log.collection.append_failed",
                LogLevel.Error,
                LogStatus.Failed,
                "Не удалось добавить пакет событий в коллекцию панели журнала",
                ex,
                SourceName,
                properties: LogProps.Create("ErrorCode", "LOG_COLLECTION_APPEND_FAILED"));
        }
    }

    public void ClearLogsCollection()
    {
        Logs.Clear();
    }

    public string GetEffectiveLogDirectory()
    {
        string? effective = _logService.EffectiveLogDirectory;
        if (!string.IsNullOrWhiteSpace(effective))
        {
            return effective;
        }

        string custom = _settingsManager.LogDir;
        if (!string.IsNullOrWhiteSpace(custom))
        {
            return custom;
        }

        try
        {
            return Path.Combine(_pathManager.GetSettingsDirectory(), "logs");
        }
        catch (Exception)
        {
            return AppContext.BaseDirectory;
        }
    }

    [RelayCommand]
    private void CopyAllLogs()
    {
        try
        {
            string text = BuildBoundedExportText();
            if (string.IsNullOrEmpty(text))
            {
                _logService.Write(
                    "log.export.empty",
                    LogLevel.Info,
                    LogStatus.Skipped,
                    "Копирование журнала пропущено: история текущего сеанса пуста",
                    source: SourceName);
                return;
            }

            DataPackage dataPackage = new();
            dataPackage.SetText(text);
            Clipboard.SetContent(dataPackage);

            _logService.Write(
                "log.export.copied",
                LogLevel.Warning,
                LogStatus.Succeeded,
                "Ограниченная копия диагностического журнала помещена в буфер обмена",
                source: SourceName,
                properties: LogProps
                    .Create("Count", text.Length)
                    .With("TotalBytes", text.Length)
                    .With("Truncated", text.Length >= ExportCharLimit)
                    .With("Schema", "log.export.bounded.v1"));
        }
        catch (Exception ex)
        {
            _logService.Write(
                "log.export.copy_failed",
                LogLevel.Warning,
                LogStatus.Failed,
                "Не удалось поместить журнал в буфер обмена",
                ex,
                SourceName,
                properties: LogProps.Create("ErrorCode", "CLIPBOARD_WRITE_FAILED"));
        }
    }

    public static string BuildBoundedExportText(IReadOnlyList<LogEvent> events)
    {
        if (events is null || events.Count == 0)
        {
            return string.Empty;
        }

        int total = events.Count;
        StringBuilder builder = new();
        int used = 0;
        int omitted = 0;

        foreach (LogEvent logEvent in events)
        {
            string line = LogEvent.Format(logEvent, includeDetail: true);
            int cost = line.Length + Environment.NewLine.Length;
            if (used + cost > ExportCharLimit)
            {
                omitted++;
                continue;
            }

            used += cost;
            builder.Append(line).Append(Environment.NewLine);
        }

        if (omitted > 0)
        {
            builder
                .Append($"Записей в копии: {omitted.ToString(CultureInfo.InvariantCulture)} из {total.ToString(CultureInfo.InvariantCulture)}. Превышен лимит экспорта {ExportCharLimit.ToString(CultureInfo.InvariantCulture)} символов.")
                .Append(Environment.NewLine);
        }

        return builder.ToString();
    }

    private string BuildBoundedExportText()
    {
        try
        {
            IReadOnlyList<LogEvent> events = _logService.ReadRecentEvents(ExportEventLimit);
            return BuildBoundedExportText(events);
        }
        catch (Exception ex)
        {
            _logService.Write(
                "log.export.build_failed",
                LogLevel.Warning,
                LogStatus.Failed,
                "Не удалось сформировать ограниченную копию журнала",
                ex,
                SourceName,
                properties: LogProps.Create("ErrorCode", "LOG_EXPORT_BUILD_FAILED"));
            return string.Empty;
        }
    }

    public void CopySelectedLogs(IEnumerable<LogItem> selectedItems)
    {
        try
        {
            if (selectedItems is null)
            {
                return;
            }

            List<string> lines = selectedItems
                .Where(static item => item is not null)
                .Select(static item => item.Message)
                .Where(static message => !string.IsNullOrEmpty(message))
                .ToList();
            if (lines.Count == 0)
            {
                _logService.Write(
                    "log.export.selection_empty",
                    LogLevel.Debug,
                    LogStatus.Skipped,
                    "Копирование выделенных записей пропущено: выделение пусто",
                    source: SourceName);
                return;
            }

            string text = BuildBoundedExportText(lines);
            DataPackage dataPackage = new();
            dataPackage.SetText(text);
            Clipboard.SetContent(dataPackage);

            _logService.Write(
                "log.export.selection_copied",
                LogLevel.Warning,
                LogStatus.Succeeded,
                "Выделенные записи журнала скопированы в буфер обмена",
                source: SourceName,
                properties: LogProps
                    .Create("Count", lines.Count)
                    .With("Truncated", text.Length >= ExportCharLimit));
        }
        catch (Exception ex)
        {
            _logService.Write(
                "log.export.selection_copy_failed",
                LogLevel.Warning,
                LogStatus.Failed,
                "Не удалось скопировать выделенные записи журнала в буфер обмена",
                ex,
                SourceName,
                properties: LogProps.Create("ErrorCode", "CLIPBOARD_WRITE_FAILED"));
        }
    }

    [RelayCommand]
    private void ClearLogs()
    {
        try
        {
            Logs.Clear();
            _logService.Write(
                "log.history.window_cleared",
                LogLevel.Info,
                LogStatus.Changed,
                "Панель журнала событий очищена пользователем",
                source: SourceName);
        }
        catch (Exception ex)
        {
            _logService.Write(
                "log.history.window_clear_failed",
                LogLevel.Error,
                LogStatus.Failed,
                "Не удалось очистить панель журнала событий",
                ex,
                SourceName,
                properties: LogProps.Create("ErrorCode", "LOG_WINDOW_CLEAR_FAILED"));
        }
    }

    [RelayCommand]
    private void ClearPersistedLogs()
    {
        try
        {
            if (_logService.ClearCurrentLog())
            {
                _logService.Write(
                    LogEventMarkerNames.PersistedLogsCleared,
                    LogLevel.Info,
                    LogStatus.Changed,
                    "Файл журнала текущего сеанса очищен пользователем",
                    null,
                    SourceName);
                return;
            }

            _logService.Write(
                LogEventMarkerNames.PersistedLogsClearFailed,
                LogLevel.Error,
                LogStatus.Failed,
                "Файл журнала текущего сеанса не очищен",
                null,
                SourceName,
                properties: LogProps.Create("ErrorCode", "LOG_FILE_CLEAR_FAILED"));
        }
        catch (Exception ex)
        {
            _logService.Write(
                "log.file.clear_failed",
                LogLevel.Error,
                LogStatus.Failed,
                "Не удалось очистить файл журнала",
                ex,
                SourceName,
                properties: LogProps.Create("ErrorCode", "LOG_FILE_CLEAR_FAILED"));
        }
    }

    [RelayCommand]
    private void OpenLogDirectory()
    {
        string logDir = GetEffectiveLogDirectory();
        string label = SafeDirectoryLabel(logDir);
        try
        {
            Directory.CreateDirectory(logDir);
            _logService.Write(
                "log.directory.opened",
                LogLevel.Debug,
                LogStatus.Succeeded,
                $"Открыт проводник для каталога журнала '{label}'",
                source: SourceName,
                properties: LogProps.Create("FileName", label));
            Process.Start("explorer.exe", $"\"{logDir}\"");
        }
        catch (Exception ex)
        {
            _logService.Write(
                "log.directory.open_failed",
                LogLevel.Warning,
                LogStatus.Failed,
                $"Не удалось открыть каталог журнала '{label}'",
                ex,
                SourceName,
                properties: LogProps
                    .Create("FileName", label)
                    .With("ErrorCode", "LOG_DIRECTORY_OPEN_FAILED"));
        }
    }

    private static string BuildBoundedExportText(List<string> lines)
    {
        StringBuilder builder = new();
        int used = 0;
        int omitted = 0;

        foreach (string line in lines)
        {
            int cost = line.Length + Environment.NewLine.Length;
            if (used + cost > ExportCharLimit)
            {
                omitted++;
                continue;
            }

            used += cost;
            builder.Append(line).Append(Environment.NewLine);
        }

        if (omitted > 0)
        {
            builder
                .Append($"Записей в копии: {omitted.ToString(CultureInfo.InvariantCulture)} из {lines.Count.ToString(CultureInfo.InvariantCulture)}. Превышен лимит экспорта {ExportCharLimit.ToString(CultureInfo.InvariantCulture)} символов.")
                .Append(Environment.NewLine);
        }

        return builder.ToString();
    }

    private static string SafeDirectoryLabel(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return LogRedactor.UnknownIdentifier;
        }

        return LogProps.FileNameOnly(path);
    }

    private void TrimToLimit()
    {
        int excess = Logs.Count - RingLimit;
        if (excess > 0)
        {
            Logs.RemoveRangeFront(excess);
        }
    }
}
