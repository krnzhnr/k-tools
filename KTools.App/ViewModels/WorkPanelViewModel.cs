// -*- coding: utf-8 -*-
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Models;
using KTools_App.Services.Contracts;
using KTools_App.UI.Controls;

using ExecutionContext = KTools_App.Models.ExecutionContext;

namespace KTools_App.ViewModels;

/// <summary>
/// Модель представления универсальной рабочей панели для выполнения скриптов обработки медиа.
/// Полностью изолирована от UI-элементов, управляет ходом асинхронного выполнения CLI-процессов.
/// </summary>
public partial class WorkPanelViewModel : ThreadSafeViewModel
{
    private const string SourceName = nameof(WorkPanelViewModel);
    private readonly INavigationService _navigationService;
    private readonly IDialogService _dialogService;
    private readonly ISettingsManager _settingsManager;
    private readonly ILogService _logService;
    private readonly IDependencyManager _dependencyManager;
    private readonly IMediaProbeService _mediaProbeService;

    private ObservableCollection<FileQueueItem> _files = new();
    private DateTime _startTime;
    private readonly Dictionary<int, double> _filesProgress = new();
    private double _progressSumTotal;
    private int _finishedCountIndex;
    private readonly Dictionary<int, (int Tick, double Percent, string Msg, double Fps, string Bitrate)> _lastProgressEmit = new();
    private readonly HashSet<int> _finishedIndices = new();
    private readonly Dictionary<int, double> _activeFps = new();
    private readonly Dictionary<int, string> _activeBitrates = new();
    private Dictionary<string, List<int>> _selectedTracks = new();
    private Dictionary<string, List<int>> _selectedAttachments = new();
    private readonly object _resultLock = new();
    private readonly Dictionary<string, ExecutionResult> _itemResults = new(StringComparer.Ordinal);
    private List<ExecutionContext> _itemContexts = new();
    private ExecutionContext? _batchContext;
    private int _succeededCount;
    private int _failedCount;
    private int _cancelledCount;
    private int _skippedCount;
    private int _partiallySucceededCount;
    private ExecutionResult? _lastQueueResult;
    private AbstractScript? _executionScript;

    public string? CurrentOperationId => _batchContext?.OperationId;

    public ExecutionResult? LastQueueResult => _lastQueueResult;

    public IReadOnlyList<ExecutionResult> TypedItemResults
    {
        get
        {
            lock (_resultLock)
            {
                return _itemResults.Values
                    .OrderBy(result => result.Context.ItemIndex)
                    .ToArray();
            }
        }
    }

    public int SucceededCount
    {
        get
        {
            lock (_resultLock)
            {
                return _succeededCount;
            }
        }
    }

    public int FailedCount
    {
        get
        {
            lock (_resultLock)
            {
                return _failedCount;
            }
        }
    }

    public int CancelledCount
    {
        get
        {
            lock (_resultLock)
            {
                return _cancelledCount;
            }
        }
    }

    public int SkippedCount
    {
        get
        {
            lock (_resultLock)
            {
                return _skippedCount;
            }
        }
    }

    public int PartiallySucceededCount
    {
        get
        {
            lock (_resultLock)
            {
                return _partiallySucceededCount;
            }
        }
    }

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
    /// Динамический текст плейсхолдера для поля выбора выходного пути.
    /// </summary>
    [ObservableProperty]
    public partial string OutputPathPlaceholder { get; set; } = "По умолчанию (в папку с исходными файлами)";

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

        if (Files != null)
        {
            Files.CollectionChanged -= OnFilesCollectionChanged;
        }

        ActiveScript = script;
        Files = files;

        if (Files != null)
        {
            Files.CollectionChanged += OnFilesCollectionChanged;
        }

        IsTracksTabVisible = script.UseCustomWidget;
        var fullSchema = script.GetFullSettingsSchema();
        IsSettingsTabVisible = fullSchema != null &&
                               fullSchema.Count > 0;

        RestoreState();
        CheckDependencies();
        UpdateOutputPathPlaceholder();

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
        OutputPath = ActiveScript.SavedOutputPath ?? string.Empty;

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

        AbstractScript script = ActiveScript;
        script.StateChanged -= OnScriptStateChanged;

        try
        {
            if (!script.IsProcessing)
            {
                lock (_progressLock)
                {
                    script.SavedStatusText = StatusText;
                    script.SavedGlobalProgress = GlobalProgressValue;
                }
            }

            script.SavedOutputPath = OutputPath;
        }
        finally
        {
            script.StateChanged += OnScriptStateChanged;
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
        AbstractScript? script = ActiveScript;
        if (script == null || IsProcessing) return;

        var filesList = new List<FileQueueItem>();
        bool queueStarted = false;
        try
        {
            filesList = script.GetProcessableFiles(Files.ToList());
            if (filesList.Count == 0)
            {
                HandleEmptyQueue(script);
                return;
            }

            PrepareExecutionState(script, filesList);
            queueStarted = true;

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
                    script,
                    filesList,
                    activeSettings,
                    outPath);
            });
        }
        catch (Exception ex)
        {
            if (queueStarted)
            {
                HandleBatchException(script, filesList, ex);
            }
            else
            {
                HandleQueueStartFailure(script, filesList, ex);
            }
        }
    }

    /// <summary>
    /// Инициализирует состояние скрипта перед началом обработки очереди.
    /// </summary>
    private void PrepareExecutionState(AbstractScript script, List<FileQueueItem> filesList)
    {
        _startTime = DateTime.UtcNow;
        _filesProgress.Clear();
        _progressSumTotal = 0.0;
        _finishedCountIndex = 0;
        _lastProgressEmit.Clear();
        _finishedIndices.Clear();
        _activeFps.Clear();
        _activeBitrates.Clear();
        lock (_resultLock)
        {
            _itemResults.Clear();
            _succeededCount = 0;
            _failedCount = 0;
            _cancelledCount = 0;
            _skippedCount = 0;
            _partiallySucceededCount = 0;
            _lastQueueResult = null;
        }

        _executionScript = script;
        _batchContext = ExecutionContext.CreateBatch(script.GetType().Name, filesList.Count);
        _itemContexts = filesList
            .Select((_, index) => _batchContext!.ForItem(index))
            .ToList();

        IsLogExpanded = false;
        IsProcessing = true;

        for (int i = 0; i < filesList.Count; i++)
        {
            _filesProgress[i] = 0.0;
        }

        foreach (var item in filesList)
        {
            item.ResetStateForRetry();
            item.IsProcessing = true;
        }

        script.IsProcessing = true;
        script.ClearSavedLog();
        script.SavedGlobalProgress = 0;
        script.SavedStatusText = "Подготовка к обработке...";

        script.PrepareBatch(filesList.Select(f => f.FilePath));
        script.ResetCancellation();
        script.RaiseStateChanged();

        AppendJournalText(
            $"Начало обработки: {filesList.Count} файлов.",
            _batchContext);
        AppendJournalText(
            $"🚀 Запуск скрипта '{script.Name}' для {filesList.Count} файлов.",
            _batchContext);
        WriteQueueEvent(
            "exec.queue.started",
            LogLevel.Info,
            LogStatus.Running,
            "Очередь обработки запущена",
            _batchContext,
            new Dictionary<string, object?>
            {
                ["Total"] = filesList.Count,
                ["ScriptId"] = _batchContext.ScriptId
            });
    }

    private async Task ProcessQueueAsync(
        AbstractScript script,
        List<FileQueueItem> filesList,
        Dictionary<string, object> settings,
        string? outPath)
    {
        int total = filesList.Count;
        bool supportsParallel = script.SupportsParallel && _settingsManager.EnableParallel;
        int maxParallel = supportsParallel ? Math.Max(1, _settingsManager.MaxParallelTasks) : 1;

        if (supportsParallel && maxParallel > 1 && total > 1)
        {
            using var semaphore = new System.Threading.SemaphoreSlim(maxParallel);
            var tasks = new List<Task>(total);

            for (int i = 0; i < total; i++)
            {
                if (script.IsCancelled)
                {
                    break;
                }

                await semaphore.WaitAsync();

                if (script.IsCancelled)
                {
                    semaphore.Release();
                    break;
                }

                int index = i;
                FileQueueItem fileItem = filesList[index];
                ExecutionContext context = _itemContexts[index];
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await ProcessQueueItemAsync(
                            script,
                            fileItem,
                            settings,
                            outPath,
                            index,
                            total,
                            context);
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
                if (script.IsCancelled)
                {
                    break;
                }

                await ProcessQueueItemAsync(
                    script,
                    filesList[i],
                    settings,
                    outPath,
                    i,
                    total,
                    _itemContexts[i]);
            }
        }

        MarkUnprocessedItems(script, filesList);
        FinalizeExecution(script, filesList);
    }

    private async Task ProcessQueueItemAsync(
        AbstractScript script,
        FileQueueItem fileItem,
        Dictionary<string, object> settings,
        string? outPath,
        int index,
        int total,
        ExecutionContext context)
    {
        UpdateFileStatus(fileItem, "Обработка...", 0.0, FileProcessingState.Processing);
        UpdateProgressState(
            index,
            total,
            $"Обработка файла {index + 1} из {total}",
            0.0);
        WriteItemEvent(
            fileItem,
            context,
            "exec.item.started",
            LogLevel.Info,
            LogStatus.Running,
            "Элемент очереди запущен",
            null,
            0);

        try
        {
            long startedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();

            ScriptProgressCallback progressCallback =
                (currIdx, totCount, msg, percent, fps, bitrate) =>
                {
                    double percentValue = percent ?? 0.0;
                    if (percentValue < 100.0 && !ShouldEmitProgress(index, percentValue, msg, fps, bitrate))
                    {
                        return;
                    }

                    UpdateFileStatus(
                        fileItem,
                        msg,
                        percentValue,
                        FileProcessingState.Processing,
                        syncScriptState: false);
                    UpdateProgressState(
                        index,
                        total,
                        $"Файл {index + 1} из {total} ({percentValue:F0}%)",
                        percentValue,
                        fps,
                        bitrate,
                        syncScriptState: false);
                    script.RaiseStateChanged();
                };

            ExecutionResult? result = await script.ExecuteSingleAsync(
                fileItem.FilePath,
                settings,
                outPath,
                progressCallback,
                index,
                total,
                context);

            double measuredMs = System.Diagnostics.Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;
            result = NormalizeResult(result, context, script);
            result = WithMeasuredDuration(result, context, measuredMs);
            RecordTerminalResult(fileItem, index, total, context, result);
        }
        catch (Exception ex)
        {
            _logService.Write(
                "exec.item.exception",
                LogLevel.Error,
                LogStatus.Failed,
                $"Элемент очереди '{LogProps.FileName(fileItem.FilePath)}' не обработан",
                ex,
                SourceName,
                context: context.ToLogContext(),
                properties: LogProps
                    .Create("ErrorCode", "ITEM_EXECUTION_FAILED")
                    .With("FileName", LogProps.FileName(fileItem.FilePath)));
            ExecutionResult result = ExecutionResult.FromException(
                context,
                ex,
                new[] { "Критическая ошибка выполнения." },
                errorCode: "execution-exception",
                cleanupState: CleanupState.Unknown);
            RecordTerminalResult(fileItem, index, total, context, result);
        }
    }

    /// <summary>
    /// Дополняет результат измеренной длительностью выполнения элемента очереди,
    /// если скрипт не сообщил её самостоятельно.
    /// </summary>
    private static ExecutionResult WithMeasuredDuration(
        ExecutionResult result,
        ExecutionContext context,
        double measuredMs)
    {
        if (result.DurationMs > 0 || !double.IsFinite(measuredMs) || measuredMs <= 0)
        {
            return result;
        }

        ItemResult measured = ItemResult.Create(
            result.ItemId,
            result.Status,
            result.Messages,
            result.ErrorCode,
            result.ExitCode,
            result.OutputFile,
            result.OutputExists,
            result.ExceptionInfo,
            result.Exception,
            result.Retryable,
            result.CleanupState,
            measuredMs,
            result.OperationId);

        return ExecutionResult.FromItemResult(context, measured);
    }

    private void MarkUnprocessedItems(AbstractScript script, List<FileQueueItem> filesList)
    {
        for (int index = 0; index < filesList.Count; index++)
        {
            ExecutionContext context = _itemContexts[index];
            bool hasResult;
            lock (_resultLock)
            {
                hasResult = _itemResults.ContainsKey(context.ItemId);
            }

            if (!hasResult)
            {
                ExecutionResult result = ExecutionResult.Cancelled(
                    context,
                    new[] { "Обработка отменена до запуска элемента." },
                    errorCode: "cancelled-before-start",
                    cleanupState: CleanupState.NotStarted);
                RecordTerminalResult(filesList[index], index, filesList.Count, context, result);
            }
        }
    }

    private void RecordTerminalResult(
        FileQueueItem fileItem,
        int index,
        int total,
        ExecutionContext context,
        ExecutionResult result)
    {
        bool added;
        lock (_resultLock)
        {
            added = _itemResults.TryAdd(context.ItemId, result);
            if (added)
            {
                switch (result.Status)
                {
                    case ExecutionStatus.Succeeded:
                        _succeededCount++;
                        break;
                    case ExecutionStatus.Failed:
                        _failedCount++;
                        break;
                    case ExecutionStatus.Cancelled:
                        _cancelledCount++;
                        break;
                    case ExecutionStatus.Skipped:
                        _skippedCount++;
                        break;
                    case ExecutionStatus.PartiallySucceeded:
                        _partiallySucceededCount++;
                        break;
                }
            }
        }

        if (!added)
        {
            return;
        }

        AppendJournalMessages(context, result);
        WriteItemEvent(fileItem, context, ItemEventId(result.Status), ItemLevel(result.Status), result.LogStatus, "Элемент очереди завершён", result, result.DurationMs);
        RunOnUi(() => fileItem.ApplyExecutionResult(result));
        UpdateProgressState(
            index,
            total,
            $"Файл {index + 1} из {total} завершён",
            100.0,
            syncScriptState: false);
        _executionScript?.RaiseStateChanged();
    }

    private ExecutionResult NormalizeResult(
        ExecutionResult? result,
        ExecutionContext context,
        AbstractScript script)
    {
        if (result is null)
        {
            return ExecutionResult.Failed(
                context,
                new[] { "Скрипт вернул пустой результат." },
                errorCode: "null-result",
                retryable: true,
                cleanupState: CleanupState.NotStarted);
        }

        if (script.IsCancelled && result.Status != ExecutionStatus.Cancelled)
        {
            return RecreateResult(
                result,
                context,
                ExecutionStatus.Cancelled,
                PreferErrorCode(result.ErrorCode, "cancelled"));
        }

        if (result.Status == ExecutionStatus.Succeeded &&
            result.OutputFile is not null &&
            !result.OutputExists)
        {
            return RecreateResult(
                result,
                context,
                ExecutionStatus.Failed,
                PreferErrorCode(result.ErrorCode, "output-missing"),
                outputExists: false,
                retryable: true);
        }

        return result;
    }

    private static string PreferErrorCode(string? current, string fallback)
    {
        return string.IsNullOrWhiteSpace(current) ||
            string.Equals(current, "execution-failed", StringComparison.Ordinal)
            ? fallback
            : current!;
    }

    /// <summary>
    /// Создаёт производный результат с новым статусом, сохраняя диагностические данные
    /// исходного результата (ExceptionInfo, Exception, Retryable, CleanupState, ExitCode, DurationMs).
    /// </summary>
    private static ExecutionResult RecreateResult(
        ExecutionResult source,
        ExecutionContext context,
        ExecutionStatus status,
        string? errorCode,
        bool? outputExists = null,
        bool? retryable = null)
    {
        ItemResult recreated = ItemResult.Create(
            source.ItemId,
            status,
            source.Messages,
            errorCode,
            source.ExitCode,
            source.OutputFile,
            outputExists ?? source.OutputExists,
            source.ExceptionInfo,
            source.Exception,
            retryable ?? source.Retryable,
            source.CleanupState,
            source.DurationMs,
            source.OperationId);

        return ExecutionResult.FromItemResult(context, recreated);
    }

    private void FinalizeExecution(AbstractScript script, List<FileQueueItem> filesList)
    {
        int total = filesList.Count;
        int succeeded;
        int failed;
        int cancelled;
        int skipped;
        int partial;
        int terminal;
        lock (_resultLock)
        {
            succeeded = _succeededCount;
            failed = _failedCount;
            cancelled = _cancelledCount;
            skipped = _skippedCount;
            partial = _partiallySucceededCount;
            terminal = _itemResults.Count;
        }

        bool allTerminal = total > 0 && terminal == total;
        bool hasProblems = failed > 0 || cancelled > 0 || partial > 0;
        bool anyTerminalOutcome = succeeded > 0 || failed > 0 || cancelled > 0 || skipped > 0 || partial > 0;
        ExecutionStatus aggregateStatus;
        string finalStatus;
        if (allTerminal && succeeded == total)
        {
            aggregateStatus = ExecutionStatus.Succeeded;
            finalStatus = "Все файлы успешно обработаны";
        }
        else if (allTerminal && failed == total)
        {
            aggregateStatus = ExecutionStatus.Failed;
            finalStatus = "Обработка завершилась ошибкой";
        }
        else if (cancelled > 0 && cancelled == total)
        {
            aggregateStatus = ExecutionStatus.Cancelled;
            finalStatus = $"Обработка отменена: {cancelled} из {total}";
        }
        else if (anyTerminalOutcome)
        {
            aggregateStatus = ExecutionStatus.PartiallySucceeded;
            finalStatus =
                $"Завершено частично: успешно {succeeded}, с ошибкой {failed + partial}, отменено {cancelled}, пропущено {skipped} из {total}";
        }
        else
        {
            aggregateStatus = ExecutionStatus.Cancelled;
            finalStatus = $"Обработка отменена: {cancelled} из {total}";
        }

        if (allTerminal)
        {
            script.SavedGlobalProgress = 100;
        }
        else
        {
            script.SavedGlobalProgress = 0;
        }
        script.SavedStatusText = finalStatus;
        script.IsProcessing = false;

        ExecutionContext? batch = _batchContext;
        if (batch is not null)
        {
            // При Succeeded ErrorCode обязан быть пустым: потребитель, ищущий отказы
            // по errorCode != null, иначе получал бы ложное срабатывание на успехе.
            // Диагностика успеха остаётся в отдельном свойстве Status.
            string? queueErrorCode = aggregateStatus == ExecutionStatus.Succeeded
                ? null
                : aggregateStatus == ExecutionStatus.Cancelled
                    ? "queue-cancelled"
                    : hasProblems ? "queue-partial" : "queue-incomplete";
            CleanupState queueCleanupState = aggregateStatus switch
            {
                ExecutionStatus.Succeeded => CleanupState.Completed,
                ExecutionStatus.Cancelled => CleanupState.Partial,
                ExecutionStatus.Skipped => CleanupState.NotRequired,
                _ => CleanupState.Partial
            };
            ExecutionResult queueResult = ExecutionResult.Create(
                batch,
                aggregateStatus,
                new[] { finalStatus },
                errorCode: queueErrorCode,
                cleanupState: queueCleanupState,
                durationMs: (DateTime.UtcNow - _startTime).TotalMilliseconds);
            lock (_resultLock)
            {
                _lastQueueResult = queueResult;
            }
            AppendJournalText(finalStatus, batch);
            if (aggregateStatus == ExecutionStatus.Succeeded)
            {
                AppendJournalText("🎉 Все файлы успешно обработаны.", batch);
            }
            WriteQueueEvent(
                "exec.queue.ended",
                QueueEventLevel(aggregateStatus),
                queueResult.LogStatus,
                finalStatus,
                batch,
                new Dictionary<string, object?>
                {
                    ["Total"] = total,
                    ["Succeeded"] = succeeded,
                    ["Failed"] = failed,
                    ["Cancelled"] = cancelled,
                    ["Skipped"] = skipped,
                    ["PartiallySucceeded"] = partial,
                    ["Status"] = aggregateStatus.ToString(),
                    ["CleanupState"] = queueCleanupState.ToString(),
                    ["DurationMs"] = queueResult.DurationMs
                },
                queueErrorCode);
        }

        RunOnUi(() =>
        {
            foreach (FileQueueItem item in filesList)
            {
                item.IsProcessing = false;
            }
            IsProcessing = false;
            IsLogExpanded = hasProblems || aggregateStatus != ExecutionStatus.Succeeded;
            StatusText = finalStatus;
            GlobalProgressValue = allTerminal ? 100 : 0;
        });
        script.RaiseStateChanged();
    }

    private static LogLevel QueueEventLevel(ExecutionStatus status)
    {
        return status switch
        {
            ExecutionStatus.Failed => LogLevel.Error,
            ExecutionStatus.PartiallySucceeded => LogLevel.Warning,
            ExecutionStatus.Cancelled => LogLevel.Warning,
            _ => LogLevel.Info
        };
    }

    private void HandleEmptyQueue(AbstractScript script)
    {
        _executionScript = script;
        _batchContext = ExecutionContext.CreateBatch(script.GetType().Name, 0);
        _itemContexts = new List<ExecutionContext>();
        lock (_resultLock)
        {
            _itemResults.Clear();
            _succeededCount = 0;
            _failedCount = 0;
            _cancelledCount = 0;
            _skippedCount = 0;
            _partiallySucceededCount = 0;
        }

        ExecutionResult queueResult = ExecutionResult.Create(
            _batchContext,
            ExecutionStatus.Failed,
            new[] { "Очередь не содержит файлов для обработки." },
            errorCode: "queue-empty",
            cleanupState: CleanupState.Completed);
        lock (_resultLock)
        {
            _lastQueueResult = queueResult;
        }

        AppendJournalText(queueResult.Messages[0], _batchContext);
        WriteQueueEvent(
            "exec.queue.empty",
            LogLevel.Error,
            LogStatus.Failed,
            "Очередь не содержит файлов для обработки",
            _batchContext,
            new Dictionary<string, object?>
            {
                ["Total"] = 0,
                ["Status"] = ExecutionStatus.Failed.ToString(),
                ["ErrorCode"] = "queue-empty"
            },
            "queue-empty");

        script.IsProcessing = false;
        script.SavedStatusText = "Очередь пуста";
        script.SavedGlobalProgress = 0;
        script.RaiseStateChanged();
        RunOnUi(() =>
        {
            IsProcessing = false;
            IsLogExpanded = true;
            StatusText = "Очередь пуста";
            GlobalProgressValue = 0;
        });
    }

    private void HandleBatchException(AbstractScript script, List<FileQueueItem> filesList, Exception exception)
    {
        if (_batchContext is null)
        {
            _executionScript = script;
            _batchContext = ExecutionContext.CreateBatch(script.GetType().Name, 0);
        }

        _logService.Write(
            "exec.queue.failed",
            LogLevel.Error,
            LogStatus.Failed,
            "Очередь обработки прервана непредвиденной ошибкой",
            exception,
            SourceName,
            context: _batchContext.ToLogContext(),
            properties: LogProps
                .Create("ErrorCode", "QUEUE_EXECUTION_FAILED")
                .With("Retryable", true));

        int total = filesList.Count;
        for (int index = 0; index < _itemContexts.Count; index++)
        {
            ExecutionContext context = _itemContexts[index];
            bool hasResult;
            lock (_resultLock)
            {
                hasResult = _itemResults.ContainsKey(context.ItemId);
            }

            if (!hasResult && index < total)
            {
                ExecutionResult result = ExecutionResult.FromException(
                    context,
                    exception,
                    new[] { "Критическая ошибка выполнения очереди." },
                    errorCode: "queue-exception",
                    cleanupState: CleanupState.Unknown);
                RecordTerminalResult(filesList[index], index, total, context, result);
            }
        }

        FinalizeExecution(script, filesList);
    }

    private void HandleQueueStartFailure(
        AbstractScript script,
        List<FileQueueItem> filesList,
        Exception exception)
    {
        List<FileQueueItem> items = filesList.Count > 0 ? filesList : Files.ToList();
        int total = items.Count;
        EnsureQueueContexts(script, total);

        ExecutionContext batch = _batchContext!;
        _logService.Write(
            "exec.queue.start_failed",
            LogLevel.Error,
            LogStatus.Failed,
            "Запуск очереди обработки не выполнен",
            exception,
            SourceName,
            context: batch.ToLogContext(),
            properties: LogProps
                .Create("ErrorCode", "QUEUE_START_FAILED")
                .With("Retryable", true)
                .With("Total", total));

        for (int index = 0; index < total; index++)
        {
            ExecutionContext context = _itemContexts[index];
            bool hasResult;
            lock (_resultLock)
            {
                hasResult = _itemResults.ContainsKey(context.ItemId);
            }

            if (!hasResult)
            {
                ExecutionResult result = ExecutionResult.Failed(
                    context,
                    "Очередь не запущена из-за критической ошибки подготовки.",
                    errorCode: "queue-start-failed",
                    retryable: true,
                    cleanupState: CleanupState.NotStarted);
                RecordTerminalResult(items[index], index, total, context, result);
            }
        }

        FinalizeExecution(script, items);
        script.SavedGlobalProgress = 0;
        RunOnUi(() => GlobalProgressValue = 0);
    }

    /// <summary>
    /// Гарантирует наличие контекста очереди и элементов для отчётности о сбое запуска,
    /// чтобы терминальные события не потеряли корреляцию.
    /// </summary>
    private void EnsureQueueContexts(AbstractScript script, int total)
    {
        if (_batchContext is not null && _executionScript == script && _itemContexts.Count == total)
        {
            return;
        }

        _executionScript = script;
        _batchContext = ExecutionContext.CreateBatch(script.GetType().Name, total);
        _itemContexts = Enumerable
            .Range(0, total)
            .Select(index => _batchContext!.ForItem(index))
            .ToList();
        lock (_resultLock)
        {
            _itemResults.Clear();
            _succeededCount = 0;
            _failedCount = 0;
            _cancelledCount = 0;
            _skippedCount = 0;
            _partiallySucceededCount = 0;
            _lastQueueResult = null;
        }
    }

    private void AppendJournalText(string message, ExecutionContext? context)
    {
        AbstractScript? script = _executionScript;
        if (script is null)
        {
            return;
        }

        script.AppendToLog($"{message}{Environment.NewLine}");
        script.RaiseStateChanged();
    }

    private void AppendJournalMessages(ExecutionContext context, ExecutionResult result)
    {
        if (result.Messages.Count == 0)
        {
            AppendJournalText(
                result.Status == ExecutionStatus.Succeeded ? "Элемент завершён." : "Элемент не завершён.",
                context);
            return;
        }

        foreach (string message in result.Messages)
        {
            AppendJournalText(message, context);
        }
    }

    private void WriteQueueEvent(
        string eventId,
        LogLevel level,
        LogStatus status,
        string message,
        ExecutionContext context,
        IReadOnlyDictionary<string, object?>? properties,
        string? errorCode = null)
    {
        Dictionary<string, object?> merged = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, object?> pair in context.ToLogProperties())
        {
            merged[pair.Key] = pair.Value;
        }
        if (properties is not null)
        {
            foreach (KeyValuePair<string, object?> pair in properties)
            {
                merged[pair.Key] = pair.Value;
            }
        }

        if (!string.IsNullOrEmpty(errorCode))
        {
            merged["ErrorCode"] = errorCode;
        }

        _logService.Write(new LogEvent
        {
            EventId = eventId,
            Level = level,
            Status = status,
            Source = "WorkPanel",
            OperationId = context.OperationId,
            Message = message,
            ErrorCode = string.IsNullOrEmpty(errorCode) ? null : errorCode,
            Properties = merged
        });
    }

    private void WriteItemEvent(
        FileQueueItem fileItem,
        ExecutionContext context,
        string eventId,
        LogLevel level,
        LogStatus status,
        string message,
        ExecutionResult? result,
        double durationMs)
    {
        Dictionary<string, object?> properties = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, object?> pair in context.ToLogProperties())
        {
            properties[pair.Key] = pair.Value;
        }

        properties["FileName"] = SafeFileName(fileItem.FilePath);

        string? errorCode = null;
        double resolvedDuration = durationMs;
        if (result is not null)
        {
            errorCode = result.Status == ExecutionStatus.Succeeded ? null : result.ErrorCode;
            resolvedDuration = durationMs > 0 ? durationMs : result.DurationMs;

            properties["Status"] = result.Status.ToString();
            properties["OutputExists"] = result.OutputExists;
            properties["Retryable"] = result.Retryable;
            properties["CleanupState"] = result.CleanupState.ToString();
            properties["MessageCount"] = result.Messages.Count;
            properties["DurationMs"] = resolvedDuration;
            if (!string.IsNullOrEmpty(errorCode))
            {
                properties["ErrorCode"] = errorCode;
            }

            if (result.ExitCode is not null)
            {
                properties["ExitCode"] = result.ExitCode.Value;
            }
        }

        LogEvent logEvent = new LogEvent
        {
            EventId = eventId,
            Level = level,
            Status = status,
            Source = "WorkPanel",
            OperationId = context.OperationId,
            ItemId = context.ItemId,
            Message = message,
            DurationMs = resolvedDuration > 0 ? resolvedDuration : null,
            ErrorCode = errorCode,
            ExitCode = result?.ExitCode,
            Properties = properties,
            Exception = result?.ExceptionInfo
        };
        _logService.Write(logEvent);
    }

    private static string ItemEventId(ExecutionStatus status)
    {
        return status switch
        {
            ExecutionStatus.Succeeded => "exec.item.succeeded",
            ExecutionStatus.Failed => "exec.item.failed",
            ExecutionStatus.Cancelled => "exec.item.cancelled",
            ExecutionStatus.Skipped => "exec.item.skipped",
            ExecutionStatus.PartiallySucceeded => "exec.item.partial",
            _ => "exec.item.failed"
        };
    }

    private static LogLevel ItemLevel(ExecutionStatus status)
    {
        return status switch
        {
            ExecutionStatus.Failed => LogLevel.Error,
            ExecutionStatus.PartiallySucceeded => LogLevel.Warning,
            _ => LogLevel.Info
        };
    }

    private static string SafeFileName(string path)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out Uri? uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return "media";
        }

        string name = Path.GetFileName(path);
        return string.IsNullOrWhiteSpace(name) ? "item" : name;
    }

    private void RunOnUi(Action action)
    {
        var dispatcher = App.CurrentMainWindow?.DispatcherQueue;
        if (dispatcher is null || dispatcher.HasThreadAccess)
        {
            action();
            return;
        }

        if (!dispatcher.TryEnqueue(() => action()))
        {
            action();
        }
    }

    private void OnScriptStateChanged(object? sender, EventArgs e)
    {
        RunOnUi(() =>
        {
            if (ActiveScript == null) return;
            if (sender is AbstractScript source && !ReferenceEquals(source, ActiveScript)) return;

            string savedLogText = ActiveScript.SavedLogText;
            if (!string.Equals(LogText, savedLogText, StringComparison.Ordinal))
            {
                LogText = savedLogText;
            }
            StatusText = ActiveScript.SavedStatusText;
            GlobalProgressValue = ActiveScript.SavedGlobalProgress;
            IsProcessing = ActiveScript.IsProcessing;
            IsStartButtonEnabled = !IsProcessing && CheckDependencies();
        });
    }

    /// <summary>
    /// Определяет, нужно ли отправлять промежуточное обновление прогресса в UI.
    /// Ограничивает частоту обновлений (~6 Гц), сохраняя первое и изменившие смысл обновления.
    /// Метрики (FPS, битрейт) тоже участвуют в решении, иначе их обновления терялись бы.
    /// </summary>
    private bool ShouldEmitProgress(
        int fileIndex,
        double percent,
        string msg,
        double? fps = null,
        string? bitrate = null)
    {
        lock (_progressLock)
        {
            int now = Environment.TickCount;
            bool isFirst = !_lastProgressEmit.TryGetValue(fileIndex, out var last);
            bool changed = isFirst ||
                Math.Abs(percent - last.Percent) > 0.0001 ||
                !string.Equals(last.Msg, msg ?? "", StringComparison.Ordinal) ||
                (fps.HasValue && Math.Abs(fps.Value - last.Fps) > 0.0001) ||
                !string.Equals(last.Bitrate ?? "", bitrate ?? "", StringComparison.Ordinal);
            bool elapsed = isFirst || (uint)(now - last.Tick) > 150;
            bool heartbeat = isFirst || (uint)(now - last.Tick) > 1000;

            if ((changed && (isFirst || elapsed)) || heartbeat)
            {
                _lastProgressEmit[fileIndex] = (now, percent, msg ?? "", fps ?? 0.0, bitrate ?? "");
                return true;
            }

            return false;
        }
    }

    private void UpdateFileStatus(
        FileQueueItem fileItem,
        string status,
        double progress,
        FileProcessingState? state = null,
        bool syncScriptState = true)
    {
        RunOnUi(() =>
        {
            fileItem.Status = status;
            fileItem.Progress = progress;
            if (state.HasValue)
            {
                fileItem.State = state.Value;
            }
        });

        if (syncScriptState)
        {
            (_executionScript ?? ActiveScript)?.RaiseStateChanged();
        }
    }

    private readonly object _progressLock = new();

    private void UpdateProgressState(
        int fileIndex,
        int totalCount,
        string status,
        double filePercent,
        double? fps = null,
        string? bitrate = null,
        bool syncScriptState = true)
    {
        AbstractScript? script = _executionScript ?? ActiveScript;
        if (script == null || totalCount <= 0) return;
        if (fileIndex >= 0 && fileIndex < _itemContexts.Count)
        {
            lock (_resultLock)
            {
                if (_itemResults.ContainsKey(_itemContexts[fileIndex].ItemId))
                {
                    return;
                }
            }
        }

        double overallPercent;
        int finishedCount;
        string etaStr = "-";
        double? displayFps = null;
        string? displayBitrate = null;
        string metricsText;

        lock (_progressLock)
        {
            // 1. Обновляем индивидуальный прогресс файла в словаре (инкрементально)
            double oldPercent = _filesProgress.TryGetValue(fileIndex, out var oldVal) ? oldVal : 0.0;
            _filesProgress[fileIndex] = filePercent;
            _progressSumTotal += filePercent - oldPercent;

            if (filePercent >= 100.0)
            {
                if (_finishedIndices.Add(fileIndex))
                {
                    _finishedCountIndex++;
                }
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
            overallPercent = (_progressSumTotal / (totalCount * 100.0)) * 100.0;
            overallPercent = Math.Min(Math.Max(overallPercent, 0.0), 100.0);

            // 3. Рассчитываем общее оставшееся время (ETA) для очереди
            double elapsedSeconds = (DateTime.UtcNow - _startTime).TotalSeconds;

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

            finishedCount = _finishedCountIndex;

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

            metricsText = "";
            if (displayFps.HasValue)
            {
                metricsText += $" | {displayFps.Value:F0} FPS";
            }
            if (!string.IsNullOrEmpty(displayBitrate))
            {
                metricsText += $" | {displayBitrate}";
            }

            // Публикуем состояние под тем же lock: при параллельной обработке
            // более старый поток иначе мог бы записать данные после более новых.
            script.SavedStatusText =
                $"Выполнение: готово {finishedCount} из {totalCount} ({overallPercent:F1}%){metricsText} | Осталось: {etaStr}";
            script.SavedGlobalProgress = overallPercent;
        }

        if (syncScriptState)
        {
            script.RaiseStateChanged();
        }
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
                    RunOnUi(() =>
                    {
                        item.MediaInfo = structure;
                    });
                }
            }
            catch (Exception ex)
            {
                _logService.Write("media.probe.restore_failed", LogLevel.Warning, LogStatus.Failed, $"Фоновый анализ файла '{LogProps.FileName(item.FilePath)}' при восстановлении не выполнен", ex, SourceName, properties: LogProps.Create("FileName", LogProps.FileName(item.FilePath)).With("ErrorCode", "RESTORE_PROBE_FAILED"));
            }
        });
    }

    private void OnFilesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateOutputPathPlaceholder();
    }

    public void UpdateOutputPathPlaceholder()
    {
        if (ActiveScript == null)
        {
            OutputPathPlaceholder = "По умолчанию (в папку с исходными файлами)";
            return;
        }

        string baseDir = "";
        if (ActiveScript is Scripts.MediaDownloaderScript)
        {
            baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
        else if (Files != null && Files.Count > 0)
        {
            try
            {
                var firstFile = Files[0].FilePath;
                if (!string.IsNullOrEmpty(firstFile))
                {
                    if (firstFile.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        firstFile.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                    }
                    else
                    {
                        baseDir = Path.GetDirectoryName(Path.GetFullPath(firstFile)) ?? "";
                    }
                }
            }
            catch
            {
                // Игнорируем
            }
        }

        if (string.IsNullOrEmpty(baseDir))
        {
            OutputPathPlaceholder = "По умолчанию (в папку с исходными файлами)";
            return;
        }

        if (_settingsManager.UseAutoSubfolder)
        {
            string subfolderName = _settingsManager.DefaultOutputSubfolder;
            if (string.IsNullOrWhiteSpace(subfolderName))
            {
                subfolderName = "KTools_Result";
            }
            baseDir = Path.Combine(baseDir, subfolderName);
        }

        OutputPathPlaceholder = $"По умолчанию: {baseDir}";
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
