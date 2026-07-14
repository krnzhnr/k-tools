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
public partial class WorkPanelViewModel : ThreadSafeViewModel
{
    private readonly INavigationService _navigationService;
    private readonly IDialogService _dialogService;
    private readonly ISettingsManager _settingsManager;
    private readonly ILogService _logService;
    private readonly IDependencyManager _dependencyManager;
    private readonly IMediaProbeService _mediaProbeService;

    private ObservableCollection<FileQueueItem> _files = new();
    private DateTime _startTime;
    private readonly Dictionary<int, double> _filesProgress = new();
    private readonly HashSet<int> _finishedIndices = new();
    private readonly Dictionary<int, double> _activeFps = new();
    private readonly Dictionary<int, string> _activeBitrates = new();
    private Dictionary<string, List<int>> _selectedTracks = new();
    private Dictionary<string, List<int>> _selectedAttachments = new();

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
        ISettingsManager settingsManager,
        ILogService logService,
        IDependencyManager dependencyManager,
        IMediaProbeService mediaProbeService)
    {
        _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _dependencyManager = dependencyManager ?? throw new ArgumentNullException(nameof(dependencyManager));
        _mediaProbeService = mediaProbeService ?? throw new ArgumentNullException(nameof(mediaProbeService));

        // Регистрация подписки на сообщение изменения выбора дорожек
        CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Register<Messages.TrackSelectedMessage>(this, (r, m) =>
        {
            _selectedTracks = m.SelectedTracks;
            _selectedAttachments = m.SelectedAttachments;
        });
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
            if (!_dependencyManager.IsInstalled(depKey))
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

        var filesList = ActiveScript.GetProcessableFiles(Files.ToList());
        if (filesList.Count == 0)
        {
            AppendLog("❌ Ошибка: В очереди нет файлов для обработки.\r\n");
            return;
        }

        PrepareExecutionState(filesList);

        var activeSettings = settings ?? new Dictionary<string, object>();
        if (!activeSettings.ContainsKey("selected_tracks_per_file") && _selectedTracks.Count > 0)
        {
            activeSettings["selected_tracks_per_file"] = _selectedTracks;
        }
        if (!activeSettings.ContainsKey("selected_attachments_per_file") && _selectedAttachments.Count > 0)
        {
            activeSettings["selected_attachments_per_file"] = _selectedAttachments;
        }

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
        _activeFps.Clear();
        _activeBitrates.Clear();

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
        
        ActiveScript.PrepareBatch(filesList.Select(f => f.FilePath));
        ActiveScript.ResetCancellation();
        ActiveScript.RaiseStateChanged();

        AppendLog($"🚀 Запуск скрипта '{ActiveScript.Name}' " +
                  $"для {filesList.Count} файлов.\r\n");
    }

    private async Task ProcessQueueAsync(
        List<FileQueueItem> filesList,
        Dictionary<string, object> settings,
        string? outPath)
    {
        if (ActiveScript == null) return;

        int total = filesList.Count;
        bool supportsParallel = ActiveScript.SupportsParallel && _settingsManager.EnableParallel;
        int maxParallel = supportsParallel ? Math.Max(1, _settingsManager.MaxParallelTasks) : 1;

        if (supportsParallel && maxParallel > 1 && total > 1)
        {
            var semaphore = new System.Threading.SemaphoreSlim(maxParallel);
            var tasks = new List<Task>();

            for (int i = 0; i < total; i++)
            {
                if (ActiveScript.IsCancelled)
                {
                    break;
                }

                await semaphore.WaitAsync();

                var fileItem = filesList[i];
                int index = i;
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        if (!ActiveScript.IsCancelled)
                        {
                            await ProcessQueueItemAsync(
                                fileItem,
                                settings,
                                outPath,
                                index,
                                total);
                        }
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }));
            }

            await Task.WhenAll(tasks);
        }
        else
        {
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
            ScriptProgressCallback progressCallback = 
                (currIdx, totCount, msg, percent, fps, bitrate) =>
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
                        percent ?? 0.0,
                        fps,
                        bitrate);
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

            bool hasError = results.Any(r => r.StartsWith("❌") || 
                                             r.Contains("Ошибка", StringComparison.OrdinalIgnoreCase) || 
                                             r.Contains("ОШИБКА", StringComparison.OrdinalIgnoreCase));
            if (hasError)
            {
                UpdateFileStatus(fileItem.FilePath, "Ошибка", 0.0, FileProcessingState.Failed);
            }
            else
            {
                UpdateFileStatus(fileItem.FilePath, "Завершено", 100.0, FileProcessingState.Completed);
            }
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
            IsLogExpanded = true;
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

    private readonly object _progressLock = new();

    private void UpdateProgressState(
        int fileIndex, 
        int totalCount, 
        string status, 
        double filePercent,
        double? fps = null,
        string? bitrate = null)
    {
        if (ActiveScript == null) return;

        double overallPercent;
        int finishedCount;
        string etaStr = "-";
        double? displayFps = null;
        string? displayBitrate = null;

        lock (_progressLock)
        {
            // 1. Обновляем индивидуальный прогресс файла в словаре
            _filesProgress[fileIndex] = filePercent;

            if (filePercent >= 100.0)
            {
                _finishedIndices.Add(fileIndex);
                _activeFps.Remove(fileIndex);
                _activeBitrates.Remove(fileIndex);
            }
            else
            {
                if (fps.HasValue)
                {
                    _activeFps[fileIndex] = fps.Value;
                }
                if (!string.IsNullOrEmpty(bitrate))
                {
                    _activeBitrates[fileIndex] = bitrate;
                }
            }

            // 2. Рассчитываем общий процент очереди (0-100%)
            double totalProgressSum = 0.0;
            foreach (var val in _filesProgress.Values)
            {
                totalProgressSum += val;
            }
            overallPercent = (totalProgressSum / (totalCount * 100.0)) * 100.0;
            overallPercent = Math.Min(Math.Max(overallPercent, 0.0), 100.0);

            // 3. Рассчитываем общее оставшееся время (ETA) для очереди
            double elapsedSeconds = (DateTime.Now - _startTime).TotalSeconds;

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

            finishedCount = _finishedIndices.Count;

            // Находим первый активный файл для отображения его метрик
            int? targetIndex = null;
            foreach (var idx in _filesProgress.Keys)
            {
                if (!_finishedIndices.Contains(idx) && _filesProgress[idx] < 100.0)
                {
                    if (targetIndex == null || idx < targetIndex.Value)
                    {
                        targetIndex = idx;
                    }
                }
            }

            if (targetIndex.HasValue)
            {
                if (_activeFps.TryGetValue(targetIndex.Value, out double f)) displayFps = f;
                if (_activeBitrates.TryGetValue(targetIndex.Value, out string? b)) displayBitrate = b;
            }
        }

        string metrics = "";
        if (displayFps.HasValue)
        {
            metrics += $" | {displayFps.Value:F0} FPS";
        }
        if (!string.IsNullOrEmpty(displayBitrate))
        {
            metrics += $" | {displayBitrate}";
        }

        // 4. Формируем чистый общий текст статуса без мерцания индивидуальных данных
        string displayStatusText = $"Выполнение: готово {finishedCount} из {totalCount} ({overallPercent:F1}%){metrics} | Осталось: {etaStr}";

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
                var structure = await _mediaProbeService
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
