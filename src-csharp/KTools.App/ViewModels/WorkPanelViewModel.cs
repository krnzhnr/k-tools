// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KTools_App.Core;
using KTools_App.UI.Controls;
using KTools_App.Services.Contracts;

namespace KTools_App.ViewModels;

/// <summary>
/// Модель представления универсальной рабочей панели для выполнения скриптов обработки медиа.
/// Полностью изолирована от UI-элементов, управляет ходом асинхронного выполнения CLI-процессов.
/// </summary>
public partial class WorkPanelViewModel : ObservableObject
{
    private readonly INavigationService _navigationService;
    private readonly IDialogService _dialogService;
    private readonly SettingsManager _settingsManager;
    private readonly LogService _logService;

    private ObservableCollection<FileQueueItem> _files = new();
    private bool _isRestoringQueue;

    /// <summary>
    /// Активный исполняемый скрипт обработки медиаданных.
    /// </summary>
    [ObservableProperty]
    public partial AbstractScript? ActiveScript { get; set; }

    /// <summary>
    /// Указывает, запущен ли в данный момент процесс обработки файлов.
    /// </summary>
    [ObservableProperty]
    public partial bool IsProcessing { get; set; }

    /// <summary>
    /// Пользовательский путь для сохранения обработанных файлов.
    /// </summary>
    [ObservableProperty]
    public partial string OutputPath { get; set; } = string.Empty;

    /// <summary>
    /// Информационный текст текущего статуса выполнения скрипта.
    /// </summary>
    [ObservableProperty]
    public partial string StatusText { get; set; } = "Ожидание запуска...";

    /// <summary>
    /// Значение общего (глобального) прогресса выполнения очереди задач (в процентах).
    /// </summary>
    [ObservableProperty]
    public partial double GlobalProgressValue { get; set; }

    /// <summary>
    /// Накопленный текст системного журнала (логов) для вывода в консоль интерфейса.
    /// </summary>
    [ObservableProperty]
    public partial string LogText { get; set; } = string.Empty;

    /// <summary>
    /// Флаг развернутого состояния панели системного журнала (логов).
    /// </summary>
    [ObservableProperty]
    public partial bool IsLogExpanded { get; set; }

    /// <summary>
    /// Указывает, должна ли быть видима вкладка со звуковыми дорожками (актуально для скриптов с кастомным виджетом).
    /// </summary>
    [ObservableProperty]
    public partial bool IsTracksTabVisible { get; set; }

    /// <summary>
    /// Указывает, должна ли быть видима вкладка с дополнительными параметрами настроек скрипта.
    /// </summary>
    [ObservableProperty]
    public partial bool IsSettingsTabVisible { get; set; }

    /// <summary>
    /// Указывает, доступна ли кнопка запуска обработки скрипта.
    /// </summary>
    [ObservableProperty]
    public partial bool IsStartButtonEnabled { get; set; } = true;

    /// <summary>
    /// Текст предупреждения о нехватке необходимых бинарных зависимостей для скрипта.
    /// </summary>
    [ObservableProperty]
    public partial string DependencyWarningText { get; set; } = string.Empty;

    /// <summary>
    /// Указывает, открыто ли предупреждение об отсутствующих бинарных зависимостях скрипта.
    /// </summary>
    [ObservableProperty]
    public partial bool IsDependencyWarningOpen { get; set; }

    /// <summary>
    /// Коллекция файлов, находящихся в очереди на обработку.
    /// </summary>
    public ObservableCollection<FileQueueItem> Files
    {
        get => _files;
        private set => SetProperty(ref _files, value);
    }

    /// <summary>
    /// Инициализирует новый экземпляр WorkPanelViewModel с внедрением зависимостей.
    /// </summary>
    public WorkPanelViewModel(
        INavigationService navigationService,
        IDialogService dialogService,
        SettingsManager settingsManager,
        LogService logService)
    {
        _navigationService = navigationService;
        _dialogService = dialogService;
        _settingsManager = settingsManager;
        _logService = logService;
    }

    /// <summary>
    /// Связывает активный скрипт и коллекцию файлов с моделью представления.
    /// </summary>
    public void Initialize(AbstractScript script, ObservableCollection<FileQueueItem> files)
    {
        ActiveScript = script;
        Files = files;

        IsTracksTabVisible = script.UseCustomWidget;
        IsSettingsTabVisible = script.SettingsSchema != null && script.SettingsSchema.Count > 0;

        RestoreState();
        CheckDependencies();
    }

    /// <summary>
    /// Выполняет проверку установленных бинарных зависимостей, необходимых для текущего скрипта.
    /// </summary>
    public bool CheckDependencies()
    {
        if (ActiveScript == null) return false;

        bool allInstalled = true;
        var missingDeps = new List<string>();

        foreach (var depKey in ActiveScript.RequiredDependencies)
        {
            if (!DependencyManager.Instance.IsInstalled(depKey))
            {
                allInstalled = false;
                missingDeps.Add(depKey.ToUpperInvariant());
            }
        }

        if (!allInstalled)
        {
            DependencyWarningText = $"Для работы требуются отсутствующие компоненты: {string.Join(", ", missingDeps)}";
            IsDependencyWarningOpen = true;
            IsStartButtonEnabled = false;
        }
        else
        {
            IsDependencyWarningOpen = false;
            IsStartButtonEnabled = !IsProcessing;
        }

        return allInstalled;
    }

    /// <summary>
    /// Восстанавливает сохраненное ранее состояние скрипта (очередь файлов, логи, прогресс).
    /// </summary>
    public void RestoreState()
    {
        if (ActiveScript == null) return;

        _isRestoringQueue = true;
        try
        {
            Files.Clear();
            if (ActiveScript.SavedFiles.Count > 0)
            {
                foreach (var state in ActiveScript.SavedFiles)
                {
                    if (!File.Exists(state.FilePath)) continue;
                    
                    var item = new FileQueueItem(state.FilePath)
                    {
                        Status = state.Status,
                        Progress = state.Progress
                    };
                    Files.Add(item);
                }
            }

            LogText = ActiveScript.SavedLogText ?? string.Empty;
            if (!string.IsNullOrEmpty(LogText))
            {
                IsLogExpanded = true;
            }

            StatusText = ActiveScript.SavedStatusText ?? "Ожидание запуска...";
            GlobalProgressValue = ActiveScript.SavedGlobalProgress;
        }
        finally
        {
            _isRestoringQueue = false;
        }
    }

    /// <summary>
    /// Сохраняет текущее состояние очереди файлов и логов в объект скрипта перед уходом со страницы.
    /// </summary>
    public void SaveState()
    {
        if (ActiveScript == null) return;

        ActiveScript.SavedFiles.Clear();
        foreach (var item in Files)
        {
            ActiveScript.SavedFiles.Add(new SavedFileState
            {
                FilePath = item.FilePath,
                Status = item.Status,
                Progress = item.Progress
            });
        }

        ActiveScript.SavedLogText = LogText;
        ActiveScript.SavedStatusText = StatusText;
        ActiveScript.SavedGlobalProgress = GlobalProgressValue;
    }

    /// <summary>
    /// Синхронизирует коллекцию SavedFiles при интерактивном изменении файлов в UI.
    /// </summary>
    public void SyncSavedFiles()
    {
        if (_isRestoringQueue || ActiveScript == null) return;

        ActiveScript.SavedFiles.Clear();
        foreach (var item in Files)
        {
            ActiveScript.SavedFiles.Add(new SavedFileState
            {
                FilePath = item.FilePath,
                Status = item.Status,
                Progress = item.Progress
            });
        }
    }

    /// <summary>
    /// Команда отмены выполнения активного скрипта.
    /// </summary>
    [RelayCommand]
    private void CancelExecution()
    {
        if (ActiveScript != null && IsProcessing)
        {
            StatusText = "Отмена выполнения...";
            ActiveScript.Cancel();
        }
    }

    /// <summary>
    /// Асинхронная команда запуска обработки файлов.
    /// </summary>
    /// <param name="settings">Словарь с настройками скрипта, переданный из View.</param>
    [RelayCommand]
    private async Task StartExecutionAsync(Dictionary<string, object>? settings)
    {
        if (ActiveScript == null || IsProcessing) return;

        var filesList = Files.ToList();
        if (filesList.Count == 0)
        {
            AppendLog("❌ Ошибка: В очереди нет файлов для обработки.\r\n");
            return;
        }

        SetProcessingState(true);
        LogText = string.Empty;
        IsLogExpanded = true;
        GlobalProgressValue = 0;
        StatusText = "Подготовка к обработке...";

        ActiveScript.ResetCancellation();
        string? outPath = string.IsNullOrEmpty(OutputPath) ? null : OutputPath;
        var activeSettings = settings ?? new Dictionary<string, object>();

        AppendLog($"🚀 Запуск скрипта '{ActiveScript.Name}' для {filesList.Count} файлов.\r\n");

        await Task.Run(async () =>
        {
            int total = filesList.Count;

            for (int i = 0; i < total; i++)
            {
                if (ActiveScript.IsCancelled)
                {
                    break;
                }

                var fileItem = filesList[i];

                UpdateFileStatus(fileItem, "Обработка...", 0.0);
                UpdateProgressState(i, total, $"Обработка файла {i + 1} из {total}: {fileItem.FileName}", 0.0);

                try
                {
                    Action<int, int, string, double?> progressCallback = (currIdx, totCount, msg, percent) =>
                    {
                        UpdateFileStatus(fileItem, msg, percent ?? 0.0);
                        UpdateProgressState(i, total, $"Файл {i + 1} из {total}: {fileItem.FileName} ({percent:F0}%)", percent ?? 0.0);
                    };

                    var results = await ActiveScript.ExecuteSingleAsync(
                        fileItem.FilePath,
                        activeSettings,
                        outPath,
                        progressCallback,
                        i,
                        total);

                    AppendLogs(results);

                    if (ActiveScript.IsCancelled)
                    {
                        UpdateFileStatus(fileItem, "Отменено", 0.0);
                        break;
                    }

                    UpdateFileStatus(fileItem, "Завершено", 100.0);
                }
                catch (Exception ex)
                {
                    _logService.Exception(ex, $"Ошибка выполнения скрипта на файле '{fileItem.FileName}': {ex.Message}", "WorkPanelViewModel");
                    UpdateFileStatus(fileItem, "Ошибка", 0.0);
                    AppendLogs(new List<string> { $"❌ Критическая ошибка: {ex.Message}" });
                }
            }

            // Завершение выполнения
            App.CurrentMainWindow?.DispatcherQueue?.TryEnqueue(() =>
            {
                if (ActiveScript.IsCancelled)
                {
                    LogText += "⚠ Обработка прервана пользователем.\r\n";
                    StatusText = "Обработка отменена";
                    GlobalProgressValue = 0;
                }
                else
                {
                    LogText += "🎉 Все файлы успешно обработаны.\r\n";
                    StatusText = "Обработка завершена";
                    GlobalProgressValue = 100;
                }

                IsProcessing = false;
                IsStartButtonEnabled = CheckDependencies();
            });
        });
    }

    private void SetProcessingState(bool processing)
    {
        App.CurrentMainWindow?.DispatcherQueue?.TryEnqueue(() =>
        {
            IsProcessing = processing;
            IsStartButtonEnabled = !processing && CheckDependencies();
        });
    }

    private void UpdateFileStatus(FileQueueItem item, string status, double progress)
    {
        App.CurrentMainWindow?.DispatcherQueue?.TryEnqueue(() =>
        {
            item.Status = status;
            item.Progress = progress;
        });
    }

    private void UpdateProgressState(int completedCount, int totalCount, string status, double filePercent)
    {
        App.CurrentMainWindow?.DispatcherQueue?.TryEnqueue(() =>
        {
            StatusText = status;
            GlobalProgressValue = (completedCount * 100.0 + filePercent) / totalCount;
        });
    }

    private void AppendLog(string message)
    {
        App.CurrentMainWindow?.DispatcherQueue?.TryEnqueue(() =>
        {
            LogText += message;
        });
    }

    private void AppendLogs(List<string> lines)
    {
        App.CurrentMainWindow?.DispatcherQueue?.TryEnqueue(() =>
        {
            var sb = new StringBuilder(LogText);
            foreach (var line in lines)
            {
                sb.AppendLine(line);
            }
            LogText = sb.ToString();
        });
    }
}
