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
using CommunityToolkit.Mvvm.Messaging;
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
    private DateTime _startTime;
    private readonly Dictionary<int, double> _filesProgress = new();
    private readonly HashSet<int> _finishedIndices = new();

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
    /// Связывает активный скрипт и коллекцию файлов
    /// с моделью представления.
    /// </summary>
    public void Initialize(
        AbstractScript script, 
        ObservableCollection<FileQueueItem> files)
    {
        if (ActiveScript != null)
        {
            ActiveScript.StateChanged -= OnScriptStateChanged;
        }

        ActiveScript = script;
        Files = files;

        IsTracksTabVisible = script.UseCustomWidget;
        var fullSchema = script.GetFullSettingsSchema();
        IsSettingsTabVisible = fullSchema != null && 
                               fullSchema.Count > 0;

        RestoreState();
        CheckDependencies();

        ActiveScript.StateChanged += OnScriptStateChanged;

        // Отправляем сообщение об изменении активного скрипта
        CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(
            new ActiveScriptChangedMessage(script));
    }

    /// <summary>
    /// Выполняет проверку установленных бинарных зависимостей,
    /// необходимых для текущего скрипта.
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
            DependencyWarningText = 
                "Для работы требуются отсутствующие компоненты: " +
                string.Join(", ", missingDeps);
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
    /// Восстанавливает сохраненное ранее состояние скрипта
    /// (очередь файлов, логи, прогресс).
    /// </summary>
    public void RestoreState()
    {
        if (ActiveScript == null) return;

        // Поскольку файлы в FilesQueue (ссылающемся на Files)
        // сохраняются на протяжении всей жизни скрипта,
        // нам не нужно очищать и наполнять коллекцию заново.
        // Мы просто запускаем анализ для файлов, у которых он
        // по какой-то причине отсутствует (например, не завершился).
        foreach (var item in Files)
        {
            if (item.MediaInfo == null)
            {
                StartAsyncAnalysis(item);
            }
        }

        LogText = ActiveScript.SavedLogText ?? string.Empty;
        if (!string.IsNullOrEmpty(LogText))
        {
            IsLogExpanded = true;
        }

        StatusText = 
            ActiveScript.SavedStatusText ?? "Ожидание запуска...";
        GlobalProgressValue = ActiveScript.SavedGlobalProgress;
        IsProcessing = ActiveScript.IsProcessing;
        IsStartButtonEnabled = !IsProcessing && CheckDependencies();
    }

    /// <summary>
    /// Сохраняет текущее состояние логов и прогресса скрипта
    /// перед уходом со страницы.
    /// </summary>
    public void SaveState()
    {
        if (ActiveScript == null) return;

        ActiveScript.StateChanged -= OnScriptStateChanged;

        ActiveScript.SavedLogText = LogText;
        ActiveScript.SavedStatusText = StatusText;
        ActiveScript.SavedGlobalProgress = GlobalProgressValue;
    }

    /// <summary>
    /// Команда отмены выполнения активного скрипта.
    /// </summary>
    [RelayCommand]
    private void CancelExecution()
    {
        if (ActiveScript != null && IsProcessing)
        {
            ActiveScript.SavedStatusText = "Отмена выполнения...";
            ActiveScript.RaiseStateChanged();
            ActiveScript.Cancel();
        }
    }

    /// <summary>
    /// Асинхронная команда запуска обработки файлов.
    /// </summary>
    [RelayCommand]
    private async Task StartExecutionAsync(
        Dictionary<string, object>? settings)
    {
        if (ActiveScript == null || IsProcessing) return;

        var filesList = Files.ToList();
        if (filesList.Count == 0)
        {
            AppendLog("❌ Ошибка: В очереди нет файлов для обработки.\r\n");
            return;
        }

        PrepareExecutionState(filesList);

        var activeSettings = settings ?? new Dictionary<string, object>();
        string? outPath = string.IsNullOrEmpty(OutputPath) 
            ? null 
            : OutputPath;

        await Task.Run(async () =>
        {
            await ProcessQueueAsync(
                filesList, 
                activeSettings, 
                outPath);
        });
    }

    /// <summary>
    /// Инициализирует состояние скрипта перед началом обработки очереди.
    /// </summary>
    private void PrepareExecutionState(List<FileQueueItem> filesList)
    {
        if (ActiveScript == null) return;

        _startTime = DateTime.Now;
        _filesProgress.Clear();
        _finishedIndices.Clear();

        for (int i = 0; i < filesList.Count; i++)
        {
            _filesProgress[i] = 0.0;
        }

        foreach (var item in filesList)
        {
            item.Status = "Ожидание";
            item.Progress = 0.0;
            item.State = FileProcessingState.Pending;
            item.IsProcessing = true;
        }

        ActiveScript.IsProcessing = true;
        ActiveScript.SavedLogText = string.Empty;
        ActiveScript.SavedGlobalProgress = 0;
        ActiveScript.SavedStatusText = "Подготовка к обработке...";
        
        ActiveScript.ResetCancellation();
        ActiveScript.RaiseStateChanged();

        AppendLog($"🚀 Запуск скрипта '{ActiveScript.Name}' " +
                  $"для {filesList.Count} файлов.\r\n");
    }

    /// <summary>
    /// Обрабатывает очередь файлов в фоновом потоке.
    /// </summary>
    private async Task ProcessQueueAsync(
        List<FileQueueItem> filesList,
        Dictionary<string, object> settings,
        string? outPath)
    {
        if (ActiveScript == null) return;

        int total = filesList.Count;
        for (int i = 0; i < total; i++)
        {
            if (ActiveScript.IsCancelled)
            {
                break;
            }

            var fileItem = filesList[i];
            await ProcessQueueItemAsync(
                fileItem, 
                settings, 
                outPath, 
                i, 
                total);
        }

        FinalizeExecution();
    }

    /// <summary>
    /// Выполняет обработку одного элемента очереди.
    /// </summary>
    private async Task ProcessQueueItemAsync(
        FileQueueItem fileItem,
        Dictionary<string, object> settings,
        string? outPath,
        int index,
        int total)
    {
        if (ActiveScript == null) return;

        UpdateFileStatus(fileItem.FilePath, "Обработка...", 0.0, FileProcessingState.Processing);
        UpdateProgressState(
            index, 
            total, 
            $"Обработка файла {index + 1} из {total}", 
            0.0);

        try
        {
            Action<int, int, string, double?> progressCallback = 
                (currIdx, totCount, msg, percent) =>
                {
                    UpdateFileStatus(
                        fileItem.FilePath, 
                        msg, 
                        percent ?? 0.0,
                        FileProcessingState.Processing);
                        
                    UpdateProgressState(
                        index, 
                        total, 
                        $"Файл {index + 1} из {total} ({percent:F0}%)", 
                        percent ?? 0.0);
                };

            var results = await ActiveScript.ExecuteSingleAsync(
                fileItem.FilePath,
                settings,
                outPath,
                progressCallback,
                index,
                total);

            AppendLogs(results);

            if (ActiveScript.IsCancelled)
            {
                UpdateFileStatus(fileItem.FilePath, "Отменено", 0.0, FileProcessingState.Cancelled);
                return;
            }

            UpdateFileStatus(fileItem.FilePath, "Завершено", 100.0, FileProcessingState.Completed);
        }
        catch (Exception ex)
        {
            _logService.Exception(
                ex, 
                $"Ошибка выполнения скрипта на файле " +
                $"'{fileItem.FileName}': {ex.Message}", 
                "WorkPanelViewModel");
                
            UpdateFileStatus(fileItem.FilePath, "Ошибка", 0.0, FileProcessingState.Failed);
            AppendLogs(new List<string> { 
                $"❌ Критическая ошибка: {ex.Message}" 
            });
        }
    }

    /// <summary>
    /// Финализирует состояние скрипта после окончания обработки очереди.
    /// </summary>
    private void FinalizeExecution()
    {
        if (ActiveScript == null) return;

        // Обновляем состояние обработки элементов списка в UI-потоке,
        // чтобы избежать исключения перекрестного доступа к потокам (thread access violation)
        App.CurrentMainWindow?.DispatcherQueue?.TryEnqueue(() =>
        {
            foreach (var item in Files)
            {
                item.IsProcessing = false;
            }
        });

        if (ActiveScript.IsCancelled)
        {
            ActiveScript.SavedLogText += 
                "⚠ Обработка прервана пользователем.\r\n";
            ActiveScript.SavedStatusText = "Обработка отменена";
            ActiveScript.SavedGlobalProgress = 0;
        }
        else
        {
            ActiveScript.SavedLogText += 
                "🎉 Все файлы успешно обработаны.\r\n";
            ActiveScript.SavedStatusText = "Обработка завершена";
            ActiveScript.SavedGlobalProgress = 100;
        }

        ActiveScript.IsProcessing = false;
        ActiveScript.RaiseStateChanged();
    }

    private void OnScriptStateChanged(object? sender, EventArgs e)
    {
        App.CurrentMainWindow?.DispatcherQueue?.TryEnqueue(() =>
        {
            if (ActiveScript == null) return;

            StatusText = ActiveScript.SavedStatusText;
            GlobalProgressValue = ActiveScript.SavedGlobalProgress;
            LogText = ActiveScript.SavedLogText;
            IsProcessing = ActiveScript.IsProcessing;
            IsStartButtonEnabled = !IsProcessing && CheckDependencies();
            
            if (!string.IsNullOrEmpty(LogText))
            {
                IsLogExpanded = true;
            }
        });
    }

    private void UpdateFileStatus(
        string filePath, 
        string status, 
        double progress,
        FileProcessingState? state = null)
    {
        App.CurrentMainWindow?.DispatcherQueue?.TryEnqueue(() =>
        {
            var fileItem = Files.FirstOrDefault(f => 
                f.FilePath.Equals(
                    filePath, 
                    StringComparison.OrdinalIgnoreCase));
            if (fileItem != null)
            {
                fileItem.Status = status;
                fileItem.Progress = progress;
                if (state.HasValue)
                {
                    fileItem.State = state.Value;
                }
                else
                {
                    fileItem.State = InferStateFromStatus(status);
                }
            }
        });

        ActiveScript?.RaiseStateChanged();
    }

    private FileProcessingState InferStateFromStatus(string status)
    {
        if (status == "Завершено") return FileProcessingState.Completed;
        if (status == "Ошибка") return FileProcessingState.Failed;
        if (status == "Отменено") return FileProcessingState.Cancelled;
        if (status.StartsWith("Пропуск") || status.StartsWith("Пропущен")) return FileProcessingState.Skipped;
        if (status == "Обработка" || status.StartsWith("Обработка") || status.Contains("%")) return FileProcessingState.Processing;
        if (status == "Ожидание") return FileProcessingState.Pending;
        
        return FileProcessingState.Processing;
    }

    private void UpdateProgressState(
        int fileIndex, 
        int totalCount, 
        string status, 
        double filePercent)
    {
        if (ActiveScript == null) return;

        // 1. Обновляем индивидуальный прогресс файла в словаре
        _filesProgress[fileIndex] = filePercent;

        if (filePercent >= 100.0)
        {
            _finishedIndices.Add(fileIndex);
        }

        // 2. Рассчитываем общий процент очереди (0-100%)
        double totalProgressSum = 0.0;
        foreach (var val in _filesProgress.Values)
        {
            totalProgressSum += val;
        }
        double overallPercent = (totalProgressSum / (totalCount * 100.0)) * 100.0;
        overallPercent = Math.Min(Math.Max(overallPercent, 0.0), 100.0);

        // 3. Рассчитываем общее оставшееся время (ETA) для очереди
        double elapsedSeconds = (DateTime.Now - _startTime).TotalSeconds;
        string etaStr = "-";

        if (overallPercent > 1.0) // Начинаем расчет после 1% для стабильности
        {
            double totalEstSeconds = elapsedSeconds / (overallPercent / 100.0);
            double remainingSeconds = totalEstSeconds - elapsedSeconds;
            if (remainingSeconds > 0)
            {
                int remM = (int)(remainingSeconds / 60);
                int remS = (int)(remainingSeconds % 60);
                if (remM > 60)
                {
                    int remH = remM / 60;
                    remM = remM % 60;
                    etaStr = $"{remH:D2}:{remM:D2}:{remS:D2}";
                }
                else
                {
                    etaStr = $"{remM:D2}:{remS:D2}";
                }
            }
        }

        // 4. Формируем информативный текст статуса
        int finishedCount = _finishedIndices.Count;
        string queueStats = $"Готово: {finishedCount}/{totalCount} ({overallPercent:F1}%)";

        // Очищаем сообщение от дублирования процентов
        string percentStr = $"{filePercent:F1}%";
        string cleanMsg = status;
        if (!string.IsNullOrEmpty(status))
        {
            var parts = status.Split('|', StringSplitOptions.RemoveEmptyEntries);
            var cleanParts = new List<string>();
            foreach (var part in parts)
            {
                string trimmed = part.Trim();
                if (!string.IsNullOrEmpty(trimmed) && !trimmed.Contains(percentStr))
                {
                    cleanParts.Add(trimmed);
                }
            }
            cleanMsg = string.Join(" | ", cleanParts);
        }

        string displayStatusText = $"{queueStats} | {cleanMsg} | Осталось (очередь): {etaStr}";

        ActiveScript.SavedStatusText = displayStatusText;
        ActiveScript.SavedGlobalProgress = overallPercent;

        ActiveScript.RaiseStateChanged();
    }

    private void AppendLog(string message)
    {
        if (ActiveScript == null) return;

        ActiveScript.SavedLogText += message;
        ActiveScript.RaiseStateChanged();
    }

    private void AppendLogs(List<string> lines)
    {
        if (ActiveScript == null) return;

        var sb = new StringBuilder(ActiveScript.SavedLogText);
        foreach (var line in lines)
        {
            sb.AppendLine(line);
        }
        ActiveScript.SavedLogText = sb.ToString();
        ActiveScript.RaiseStateChanged();
    }

    /// <summary>
    /// Запускает фоновый асинхронный технический анализ медиафайла,
    /// если он не был восстановлен из кэшированного состояния.
    /// </summary>
    private void StartAsyncAnalysis(FileQueueItem item)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var structure = await MediaProbeService.Instance
                    .ProbeAsync(item.FilePath);
                if (structure != null)
                {
                    App.CurrentMainWindow?.DispatcherQueue?
                        .TryEnqueue(() =>
                        {
                            item.MediaInfo = structure;
                        });
                }
            }
            catch (Exception ex)
            {
                _logService.Exception(
                    ex,
                    $"Ошибка фонового анализа при восстановлении " +
                    $"файла '{item.FileName}': {ex.Message}",
                    "WorkPanelViewModel");
            }
        });
    }
}

/// <summary>
/// Сообщение для уведомления об изменении активного скрипта на WorkPanel.
/// </summary>
public sealed class ActiveScriptChangedMessage
{
    /// <summary>Активный исполняемый скрипт.</summary>
    public AbstractScript Script { get; }

    /// <summary>
    /// Инициализирует новый экземпляр ActiveScriptChangedMessage.
    /// </summary>
    public ActiveScriptChangedMessage(AbstractScript script)
    {
        Script = script;
    }
}
