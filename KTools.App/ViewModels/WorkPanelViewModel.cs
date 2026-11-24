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

    /// <summary>
    /// Группа настроек для запоминаемых выходных путей (ишью #42).
    /// Хранение напрямую через ISettingsManager без типизированных свойств.
    /// Пути лежат в общей группе: нормализация в SettingsManager удаляет
    /// лишние ключи из групп Script_*, там хранить нельзя.
    /// </summary>
    public const string DefaultOutputPathSettingsGroup = "General";

    /// <summary>
    /// Legacy-ключ общего выходного пути по умолчанию в настройках.
    /// Используется только для миграции существующих установок:
    /// старое общее значение подхватывается каждым скриптом,
    /// дальше пути живут независимо по ключам <see cref="GetScriptOutputPathKey"/>.
    /// </summary>
    public const string DefaultOutputPathSettingsKey = "DefaultOutputPath";

    /// <summary>
    /// Возвращает ключ запоминаемого выходного пути для скрипта.
    /// Пустое имя — legacy-ключ (миграция); иначе "DefaultOutputPath_" + имя
    /// с заменами "/", "\", " ", "→" на "_" (как в SettingsManager.GetSafeGroupName,
    /// но без префикса Script_). Хранение — в группе General, см. выше.
    /// </summary>
    public static string GetScriptOutputPathKey(string? scriptName)
    {
        if (string.IsNullOrWhiteSpace(scriptName))
        {
            return DefaultOutputPathSettingsKey;
        }

        return "DefaultOutputPath_" + scriptName
            .Replace("/", "_")
            .Replace("\\", "_")
            .Replace(" ", "_")
            .Replace("→", "_");
    }

    /// <summary>
    /// Ключ переключателя «Запоминать выходные пути» в настройках (ишью #42).
    /// По умолчанию включён — путь сохраняется и восстанавливается;
    /// при выключенном значении работают только внутрисессионные значения.
    /// </summary>
    public const string RememberOutputPathsSettingsKey = "RememberOutputPaths";

    private bool _isRestoringOutputPath;
    private readonly INavigationService _navigationService;
    private readonly IDialogService _dialogService;
    private readonly ISettingsManager _settingsManager;
    private readonly ILogService _logService;
    private readonly IDependencyManager _dependencyManager;
    private readonly IMediaProbeService _mediaProbeService;

    private ObservableCollection<FileQueueItem> _files = new();
    /// <summary>
    /// Изолированное состояние одной запущенной очереди.
    /// Позволяет выполнять несколько скриптов параллельно: каждый запуск
    /// держит собственные прогресс, счётчики и контексты, а не общие поля модели.
    /// </summary>
    private sealed class QueueExecutionSession
    {
        public QueueExecutionSession(AbstractScript script, List<FileQueueItem> files)
        {
            Script = script;
            Files = files;
            BatchContext = ExecutionContext.CreateBatch(script.GetType().Name, files.Count);
            ItemContexts = files
                .Select((_, index) => BatchContext.ForItem(index))
                .ToList();
        }

        public AbstractScript Script { get; }

        public List<FileQueueItem> Files { get; }

        public DateTime StartTime { get; set; } = DateTime.UtcNow;

        public Dictionary<int, double> FilesProgress { get; } = new();

        public double ProgressSumTotal { get; set; }

        public int FinishedCountIndex { get; set; }

        public Dictionary<int, (int Tick, double Percent, string Msg, double Fps, string Bitrate)> LastProgressEmit { get; } = new();

        public HashSet<int> FinishedIndices { get; } = new();

        public Dictionary<int, double> ActiveFps { get; } = new();

        public Dictionary<int, string> ActiveBitrates { get; } = new();

        public object ResultLock { get; } = new();

        public Dictionary<string, ExecutionResult> ItemResults { get; } = new(StringComparer.Ordinal);

        public List<ExecutionContext> ItemContexts { get; }

        public ExecutionContext BatchContext { get; }

        public int SucceededCount { get; set; }

        public int FailedCount { get; set; }

        public int CancelledCount { get; set; }

        public int SkippedCount { get; set; }

        public int PartiallySucceededCount { get; set; }

        public ExecutionResult? LastQueueResult { get; set; }

        public object ProgressLock { get; } = new();
    }

    private readonly ConcurrentDictionary<AbstractScript, QueueExecutionSession> _sessions = new();

    private QueueExecutionSession? ActiveSession
    {
        get
        {
            AbstractScript? active = ActiveScript;
            if (active is null)
            {
                return null;
            }

            return _sessions.TryGetValue(active, out QueueExecutionSession? session) ? session : null;
        }
    }

    private Dictionary<string, List<int>> _selectedTracks = new();
    private Dictionary<string, List<int>> _selectedAttachments = new();

    public string? CurrentOperationId => ActiveSession?.BatchContext.OperationId;

    public ExecutionResult? LastQueueResult
    {
        get
        {
            QueueExecutionSession? session = ActiveSession;
            if (session is null)
            {
                return null;
            }

            lock (session.ResultLock)
            {
                return session.LastQueueResult;
            }
        }
    }

    public IReadOnlyList<ExecutionResult> TypedItemResults
    {
        get
        {
            QueueExecutionSession? session = ActiveSession;
            if (session is null)
            {
                return Array.Empty<ExecutionResult>();
            }

            lock (session.ResultLock)
            {
                return session.ItemResults.Values
                    .OrderBy(result => result.Context.ItemIndex)
                    .ToArray();
            }
        }
    }

    public int SucceededCount
    {
        get
        {
            QueueExecutionSession? session = ActiveSession;
            if (session is null)
            {
                return 0;
            }

            lock (session.ResultLock)
            {
                return session.SucceededCount;
            }
        }
    }

    public int FailedCount
    {
        get
        {
            QueueExecutionSession? session = ActiveSession;
            if (session is null)
            {
                return 0;
            }

            lock (session.ResultLock)
            {
                return session.FailedCount;
            }
        }
    }

    public int CancelledCount
    {
        get
        {
            QueueExecutionSession? session = ActiveSession;
            if (session is null)
            {
                return 0;
            }

            lock (session.ResultLock)
            {
                return session.CancelledCount;
            }
        }
    }

    public int SkippedCount
    {
        get
        {
            QueueExecutionSession? session = ActiveSession;
            if (session is null)
            {
                return 0;
            }

            lock (session.ResultLock)
            {
                return session.SkippedCount;
            }
        }
    }

    public int PartiallySucceededCount
    {
        get
        {
            QueueExecutionSession? session = ActiveSession;
            if (session is null)
            {
                return 0;
            }

            lock (session.ResultLock)
            {
                return session.PartiallySucceededCount;
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
    /// Смена значения (обзор, перетаскивание, ручной ввод) сохраняется в настройки,
    /// очистка поля затирает сохранённое значение.
    /// </summary>
    [ObservableProperty]
    public partial string OutputPath { get; set; } = string.Empty;

    /// <summary>
    /// Сохраняет смену выходного пути в настройки скрипта (ишью #42).
    /// Во время восстановления из настроек запись пропускается, чтобы временная
    /// недоступность папки (сеть, отключённый диск) не стирала сохранённое значение.
    /// </summary>
    partial void OnOutputPathChanged(string value)
    {
        string path = value ?? string.Empty;
        if (ActiveScript != null)
        {
            ActiveScript.SavedOutputPath = path;
        }

        if (_isRestoringOutputPath)
        {
            return;
        }

        PersistOutputPath(path, ActiveScript?.Name);
    }

    /// <summary>
    /// Записывает выходной путь скрипта в настройки напрямую через ISettingsManager.
    /// Пустое значение затирает сохранённое. Несуществующие пути на записи не отбрасываются:
    /// проверка существования выполняется только при восстановлении.
    /// </summary>
    private void PersistOutputPath(string? value, string? scriptName)
    {
        if (!IsOutputPathRememberingEnabled())
        {
            return;
        }

        string normalized = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        _settingsManager.SetSetting(DefaultOutputPathSettingsGroup, GetScriptOutputPathKey(scriptName), normalized);
    }

    /// <summary>
    /// Читает переключатель «Запоминать выходные пути» (ишью #42).
    /// Отсутствие ключа означает включённое поведение: так работают существующие установки.
    /// </summary>
    private bool IsOutputPathRememberingEnabled()
    {
        return _settingsManager.GetSetting(
            DefaultOutputPathSettingsGroup,
            RememberOutputPathsSettingsKey,
            true);
    }

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

    private readonly ITaskbarProgressService? _taskbarProgress;

    /// <summary>
    /// Инициализирует новый экземпляр WorkPanelViewModel с внедрением зависимостей.
    /// </summary>
    public WorkPanelViewModel(
        INavigationService navigationService,
        IDialogService dialogService,
        ISettingsManager settingsManager,
        ILogService logService,
        IDependencyManager dependencyManager,
        IMediaProbeService mediaProbeService,
        ITaskbarProgressService? taskbarProgressService = null)
    {
        _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _dependencyManager = dependencyManager ?? throw new ArgumentNullException(nameof(dependencyManager));
        _mediaProbeService = mediaProbeService ?? throw new ArgumentNullException(nameof(mediaProbeService));
        _taskbarProgress = taskbarProgressService;

        // Регистрация подписки на сообщение изменения выбора дорожек
        CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Register<Messages.TrackSelectedMessage>(this, (r, m) =>
        {
            _selectedTracks = m.SelectedTracks;
            _selectedAttachments = m.SelectedAttachments;
        });

        // Выключение запоминания путей возвращает поведение до внедрения сохранения:
        // текущий путь сбрасывается, файлы идут в папки своих исходников.
        CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Register<OutputPathRememberingChangedMessage>(this, (r, m) =>
        {
            if (!m.IsEnabled && !string.IsNullOrEmpty(OutputPath))
            {
                OutputPath = string.Empty;
            }
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
        RestoreScriptOutputPath(ActiveScript);

        StatusText =
            ActiveScript.SavedStatusText ?? "Ожидание запуска...";
        GlobalProgressValue = ActiveScript.SavedGlobalProgress;
        IsProcessing = ActiveScript.IsProcessing;
        IsStartButtonEnabled = !IsProcessing && CheckDependencies();
    }

    /// <summary>
    /// Восстанавливает запоминаемый выходной путь скрипта из настроек (ишью #42).
    /// Сохранённой папки больше нет — молча откат к дефолту без подстановки битого пути.
    /// Per-script запись отсутствует (первый запуск, новый скрипт) — откат к legacy-ключу
    /// (миграция существующих установок); legacy тоже нет — прежнее поведение в памяти скрипта.
    /// Явная очистка (пустая строка) — дефолт без воскрешения из памяти.
    /// </summary>
    private void RestoreScriptOutputPath(AbstractScript script)
    {
        string restored;
        if (!IsOutputPathRememberingEnabled())
        {
            // Тогл выключен: поведение до внедрения сохранения — никакого
            // запомненного пути нет, поле пустое, каждый файл пишется
            // в свою папку исходников.
            restored = string.Empty;
        }
        else
        {
            string? persisted = _settingsManager.GetSetting<string?>(
                DefaultOutputPathSettingsGroup,
                GetScriptOutputPathKey(script.Name),
                null);

            if (persisted is null)
            {
                // Per-script ключа нет: миграция — подхватить старое общее значение.
                persisted = _settingsManager.GetSetting<string?>(
                    DefaultOutputPathSettingsGroup,
                    DefaultOutputPathSettingsKey,
                    null);
            }

            if (!string.IsNullOrWhiteSpace(persisted))
            {
                restored = Directory.Exists(persisted) ? persisted : string.Empty;
            }
            else if (persisted is null)
            {
                restored = script.SavedOutputPath ?? string.Empty;
            }
            else
            {
                restored = string.Empty;
            }
        }

        _isRestoringOutputPath = true;
        try
        {
            OutputPath = restored;
        }
        finally
        {
            _isRestoringOutputPath = false;
        }

        script.SavedOutputPath = restored;
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
                script.SavedStatusText = StatusText;
                script.SavedGlobalProgress = GlobalProgressValue;
            }

            script.SavedOutputPath = OutputPath;
            PersistOutputPath(OutputPath, script.Name);
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
    /// <summary>
    /// Асинхронная команда запуска обработки файлов.
    /// Параллельные запуски разрешены: каждый скрипт ведёт собственную сессию
    /// очереди, повторный старт того же скрипта блокируется его флагом IsProcessing.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task StartExecutionAsync(
        Dictionary<string, object>? settings)
    {
        AbstractScript? script = ActiveScript;
        if (script == null || script.IsProcessing) return;

        var filesList = new List<FileQueueItem>();
        QueueExecutionSession? session = null;
        try
        {
            filesList = script.GetProcessableFiles(Files.ToList());
            if (filesList.Count == 0)
            {
                HandleEmptyQueue(script);
                return;
            }

            session = PrepareExecutionState(script, filesList);

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
                    session,
                    activeSettings,
                    outPath);
            });
        }
        catch (Exception ex)
        {
            if (session is not null)
            {
                HandleBatchException(session, ex);
            }
            else
            {
                HandleQueueStartFailure(script, filesList, ex);
            }
        }
    }

    /// <summary>
    /// Инициализирует изолированную сессию очереди перед началом обработки.
    /// Сессия регистрируется по скрипту, поэтому параллельные запуски
    /// разных скриптов не затирают прогресс и счётчики друг друга.
    /// </summary>
    private QueueExecutionSession PrepareExecutionState(AbstractScript script, List<FileQueueItem> filesList)
    {
        var session = new QueueExecutionSession(script, filesList)
        {
            StartTime = DateTime.UtcNow
        };
        for (int i = 0; i < filesList.Count; i++)
        {
            session.FilesProgress[i] = 0.0;
        }

        _sessions[script] = session;

        if (ReferenceEquals(ActiveScript, script))
        {
            IsLogExpanded = false;
            IsProcessing = true;
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
        ReportTaskbarProgress(0.0);

        AppendJournalText(
            script,
            $"Начало обработки: {filesList.Count} файлов.",
            session.BatchContext);
        AppendJournalText(
            script,
            $"🚀 Запуск скрипта '{script.Name}' для {filesList.Count} файлов.",
            session.BatchContext);
        WriteQueueEvent(
            "exec.queue.started",
            LogLevel.Info,
            LogStatus.Running,
            "Очередь обработки запущена",
            session.BatchContext,
            new Dictionary<string, object?>
            {
                ["Total"] = filesList.Count,
                ["ScriptId"] = session.BatchContext.ScriptId
            });
        return session;
    }

    /// <summary>
    /// Чистое планирование параллельности очереди: сколько файлов гнать одновременно.
    /// Скрипты с лимитом разборки (разборка контейнера) читают свой тумблер,
    /// остальные параллельные — общий. Возвращает 1 для последовательной обработки.
    /// </summary>
    public static int ResolveMaxParallelism(
        bool supportsParallel,
        bool usesDemuxLimit,
        bool enableParallel,
        int maxParallelTasks,
        bool enableParallelDemux,
        int maxParallelDemuxTasks)
    {
        if (!supportsParallel)
        {
            return 1;
        }

        if (usesDemuxLimit)
        {
            return enableParallelDemux ? Math.Max(1, maxParallelDemuxTasks) : 1;
        }

        return enableParallel ? Math.Max(1, maxParallelTasks) : 1;
    }

    private async Task ProcessQueueAsync(
        QueueExecutionSession session,
        Dictionary<string, object> settings,
        string? outPath)
    {
        AbstractScript script = session.Script;
        List<FileQueueItem> filesList = session.Files;
        int total = filesList.Count;
        int maxParallel = ResolveMaxParallelism(
            script.SupportsParallel,
            script.UsesDemuxParallelLimit,
            _settingsManager.EnableParallel,
            _settingsManager.MaxParallelTasks,
            _settingsManager.EnableParallelDemux,
            _settingsManager.MaxParallelDemuxTasks);

        if (maxParallel > 1 && total > 1)
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
                ExecutionContext context = session.ItemContexts[index];
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await ProcessQueueItemAsync(
                            session,
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
                    session,
                    filesList[i],
                    settings,
                    outPath,
                    i,
                    total,
                    session.ItemContexts[i]);
            }
        }

        MarkUnprocessedItems(session);
        FinalizeExecution(session);
    }

    private async Task ProcessQueueItemAsync(
        QueueExecutionSession session,
        FileQueueItem fileItem,
        Dictionary<string, object> settings,
        string? outPath,
        int index,
        int total,
        ExecutionContext context)
    {
        AbstractScript script = session.Script;
        UpdateFileStatus(fileItem, "Обработка...", 0.0, FileProcessingState.Processing, null);
        UpdateProgressState(
            session,
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
                    if (percentValue < 100.0 && !ShouldEmitProgress(session, index, percentValue, msg, fps, bitrate))
                    {
                        return;
                    }

                    UpdateFileStatus(
                        fileItem,
                        msg,
                        percentValue,
                        FileProcessingState.Processing,
                        null);
                    UpdateProgressState(
                        session,
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
            RecordTerminalResult(session, fileItem, index, total, context, result);
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
            RecordTerminalResult(session, fileItem, index, total, context, result);
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

    private void MarkUnprocessedItems(QueueExecutionSession session)
    {
        List<FileQueueItem> filesList = session.Files;
        for (int index = 0; index < filesList.Count; index++)
        {
            ExecutionContext context = session.ItemContexts[index];
            bool hasResult;
            lock (session.ResultLock)
            {
                hasResult = session.ItemResults.ContainsKey(context.ItemId);
            }

            if (!hasResult)
            {
                ExecutionResult result = ExecutionResult.Cancelled(
                    context,
                    new[] { "Обработка отменена до запуска элемента." },
                    errorCode: "cancelled-before-start",
                    cleanupState: CleanupState.NotStarted);
                RecordTerminalResult(session, filesList[index], index, filesList.Count, context, result);
            }
        }
    }

    private void RecordTerminalResult(
        QueueExecutionSession session,
        FileQueueItem fileItem,
        int index,
        int total,
        ExecutionContext context,
        ExecutionResult result)
    {
        bool added;
        lock (session.ResultLock)
        {
            added = session.ItemResults.TryAdd(context.ItemId, result);
            if (added)
            {
                switch (result.Status)
                {
                    case ExecutionStatus.Succeeded:
                        session.SucceededCount++;
                        break;
                    case ExecutionStatus.Failed:
                        session.FailedCount++;
                        break;
                    case ExecutionStatus.Cancelled:
                        session.CancelledCount++;
                        break;
                    case ExecutionStatus.Skipped:
                        session.SkippedCount++;
                        break;
                    case ExecutionStatus.PartiallySucceeded:
                        session.PartiallySucceededCount++;
                        break;
                }
            }
        }

        if (!added)
        {
            return;
        }

        AppendJournalMessages(session.Script, context, result);
        WriteItemEvent(fileItem, context, ItemEventId(result.Status), ItemLevel(result.Status), result.LogStatus, "Элемент очереди завершён", result, result.DurationMs);
        RunOnUi(() => fileItem.ApplyExecutionResult(result));
        UpdateProgressState(
            session,
            index,
            total,
            $"Файл {index + 1} из {total} завершён",
            100.0,
            syncScriptState: false);
        session.Script.RaiseStateChanged();
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

    private void FinalizeExecution(QueueExecutionSession session)
    {
        AbstractScript script = session.Script;
        List<FileQueueItem> filesList = session.Files;
        int total = filesList.Count;
        int succeeded;
        int failed;
        int cancelled;
        int skipped;
        int partial;
        int terminal;
        lock (session.ResultLock)
        {
            succeeded = session.SucceededCount;
            failed = session.FailedCount;
            cancelled = session.CancelledCount;
            skipped = session.SkippedCount;
            partial = session.PartiallySucceededCount;
            terminal = session.ItemResults.Count;
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

        ExecutionContext batch = session.BatchContext;
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
                durationMs: (DateTime.UtcNow - session.StartTime).TotalMilliseconds);
            lock (session.ResultLock)
            {
                session.LastQueueResult = queueResult;
            }
            AppendJournalText(script, finalStatus, batch);
            if (aggregateStatus == ExecutionStatus.Succeeded)
            {
                AppendJournalText(script, "🎉 Все файлы успешно обработаны.", batch);
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

        bool isActive = ReferenceEquals(ActiveScript, script);
        RunOnUi(() =>
        {
            foreach (FileQueueItem item in filesList)
            {
                item.IsProcessing = false;
            }
            if (isActive)
            {
                IsProcessing = false;
                IsLogExpanded = hasProblems || aggregateStatus != ExecutionStatus.Succeeded;
                StatusText = finalStatus;
                GlobalProgressValue = allTerminal ? 100 : 0;
            }
        });
        if (!HasOtherActiveSessions(session))
        {
            ReportTaskbarFinished(failed > 0 || partial > 0, allTerminal ? 100.0 : 0.0);
        }
        script.RaiseStateChanged();
    }

    /// <summary>
    /// Проверяет, остались ли другие незавершённые сессии кроме указанной.
    /// Панель задач сбрасывается только после финиша последней очереди.
    /// </summary>
    private bool HasOtherActiveSessions(QueueExecutionSession finished)
    {
        foreach (var pair in _sessions)
        {
            if (!ReferenceEquals(pair.Value, finished) && pair.Value.Script.IsProcessing)
            {
                return true;
            }
        }

        return false;
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
        var session = new QueueExecutionSession(script, new List<FileQueueItem>());
        _sessions[script] = session;

        ExecutionResult queueResult = ExecutionResult.Create(
            session.BatchContext,
            ExecutionStatus.Failed,
            new[] { "Очередь не содержит файлов для обработки." },
            errorCode: "queue-empty",
            cleanupState: CleanupState.Completed);
        lock (session.ResultLock)
        {
            session.LastQueueResult = queueResult;
        }

        AppendJournalText(script, queueResult.Messages[0], session.BatchContext);
        WriteQueueEvent(
            "exec.queue.empty",
            LogLevel.Error,
            LogStatus.Failed,
            "Очередь не содержит файлов для обработки",
            session.BatchContext,
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
        _taskbarProgress?.Clear();
        script.RaiseStateChanged();
        if (ReferenceEquals(ActiveScript, script))
        {
            RunOnUi(() =>
            {
                IsProcessing = false;
                IsLogExpanded = true;
                StatusText = "Очередь пуста";
                GlobalProgressValue = 0;
            });
        }
    }

    private void HandleBatchException(QueueExecutionSession session, Exception exception)
    {
        AbstractScript script = session.Script;
        List<FileQueueItem> filesList = session.Files;
        _logService.Write(
            "exec.queue.failed",
            LogLevel.Error,
            LogStatus.Failed,
            "Очередь обработки прервана непредвиденной ошибкой",
            exception,
            SourceName,
            context: session.BatchContext.ToLogContext(),
            properties: LogProps
                .Create("ErrorCode", "QUEUE_EXECUTION_FAILED")
                .With("Retryable", true));

        int total = filesList.Count;
        for (int index = 0; index < session.ItemContexts.Count; index++)
        {
            ExecutionContext context = session.ItemContexts[index];
            bool hasResult;
            lock (session.ResultLock)
            {
                hasResult = session.ItemResults.ContainsKey(context.ItemId);
            }

            if (!hasResult && index < total)
            {
                ExecutionResult result = ExecutionResult.FromException(
                    context,
                    exception,
                    new[] { "Критическая ошибка выполнения очереди." },
                    errorCode: "queue-exception",
                    cleanupState: CleanupState.Unknown);
                RecordTerminalResult(session, filesList[index], index, total, context, result);
            }
        }

        FinalizeExecution(session);
    }

    private void HandleQueueStartFailure(
        AbstractScript script,
        List<FileQueueItem> filesList,
        Exception exception)
    {
        List<FileQueueItem> items = filesList.Count > 0 ? filesList : Files.ToList();
        QueueExecutionSession session = EnsureQueueSession(script, items);

        ExecutionContext batch = session.BatchContext;
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
                .With("Total", items.Count));

        int total = items.Count;
        for (int index = 0; index < total; index++)
        {
            ExecutionContext context = session.ItemContexts[index];
            bool hasResult;
            lock (session.ResultLock)
            {
                hasResult = session.ItemResults.ContainsKey(context.ItemId);
            }

            if (!hasResult)
            {
                ExecutionResult result = ExecutionResult.Failed(
                    context,
                    "Очередь не запущена из-за критической ошибки подготовки.",
                    errorCode: "queue-start-failed",
                    retryable: true,
                    cleanupState: CleanupState.NotStarted);
                RecordTerminalResult(session, items[index], index, total, context, result);
            }
        }

        FinalizeExecution(session);
        script.SavedGlobalProgress = 0;
        if (ReferenceEquals(ActiveScript, script))
        {
            RunOnUi(() => GlobalProgressValue = 0);
        }
    }

    /// <summary>
    /// Гарантирует наличие изолированной сессии для отчётности о сбое запуска,
    /// чтобы терминальные события не потеряли корреляцию.
    /// </summary>
    private QueueExecutionSession EnsureQueueSession(AbstractScript script, List<FileQueueItem> items)
    {
        if (_sessions.TryGetValue(script, out QueueExecutionSession? existing)
            && existing.Files.Count == items.Count)
        {
            return existing;
        }

        var session = new QueueExecutionSession(script, items);
        _sessions[script] = session;
        return session;
    }

    private void AppendJournalText(AbstractScript script, string message, ExecutionContext? context)
    {
        script.AppendToLog($"{message}{Environment.NewLine}");
        script.RaiseStateChanged();
    }

    private void AppendJournalMessages(AbstractScript script, ExecutionContext context, ExecutionResult result)
    {
        if (result.Messages.Count == 0)
        {
            AppendJournalText(
                script,
                result.Status == ExecutionStatus.Succeeded ? "Элемент завершён." : "Элемент не завершён.",
                context);
            return;
        }

        foreach (string message in result.Messages)
        {
            AppendJournalText(script, message, context);
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
        QueueExecutionSession session,
        int fileIndex,
        double percent,
        string msg,
        double? fps = null,
        string? bitrate = null)
    {
        lock (session.ProgressLock)
        {
            int now = Environment.TickCount;
            bool isFirst = !session.LastProgressEmit.TryGetValue(fileIndex, out var last);
            bool changed = isFirst ||
                Math.Abs(percent - last.Percent) > 0.0001 ||
                !string.Equals(last.Msg, msg ?? "", StringComparison.Ordinal) ||
                (fps.HasValue && Math.Abs(fps.Value - last.Fps) > 0.0001) ||
                !string.Equals(last.Bitrate ?? "", bitrate ?? "", StringComparison.Ordinal);
            bool elapsed = isFirst || (uint)(now - last.Tick) > 150;
            bool heartbeat = isFirst || (uint)(now - last.Tick) > 1000;

            if ((changed && (isFirst || elapsed)) || heartbeat)
            {
                session.LastProgressEmit[fileIndex] = (now, percent, msg ?? "", fps ?? 0.0, bitrate ?? "");
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
        AbstractScript? notifyScript = null)
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

        notifyScript?.RaiseStateChanged();
    }

    private void UpdateProgressState(
        QueueExecutionSession session,
        int fileIndex,
        int totalCount,
        string status,
        double filePercent,
        double? fps = null,
        string? bitrate = null,
        bool syncScriptState = true)
    {
        AbstractScript script = session.Script;
        if (totalCount <= 0) return;
        if (fileIndex >= 0 && fileIndex < session.ItemContexts.Count)
        {
            lock (session.ResultLock)
            {
                if (session.ItemResults.ContainsKey(session.ItemContexts[fileIndex].ItemId))
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

        lock (session.ProgressLock)
        {
            // 1. Обновляем индивидуальный прогресс файла в словаре (инкрементально)
            double oldPercent = session.FilesProgress.TryGetValue(fileIndex, out var oldVal) ? oldVal : 0.0;
            session.FilesProgress[fileIndex] = filePercent;
            session.ProgressSumTotal += filePercent - oldPercent;

            if (filePercent >= 100.0)
            {
                if (session.FinishedIndices.Add(fileIndex))
                {
                    session.FinishedCountIndex++;
                }
                session.ActiveFps.Remove(fileIndex);
                session.ActiveBitrates.Remove(fileIndex);
            }
            else
            {
                if (fps.HasValue)
                {
                    session.ActiveFps[fileIndex] = fps.Value;
                }
                if (!string.IsNullOrEmpty(bitrate))
                {
                    session.ActiveBitrates[fileIndex] = bitrate;
                }
            }

            // 2. Рассчитываем общий процент очереди (0-100%)
            overallPercent = (session.ProgressSumTotal / (totalCount * 100.0)) * 100.0;
            overallPercent = Math.Min(Math.Max(overallPercent, 0.0), 100.0);

            // 3. Рассчитываем общее оставшееся время (ETA) для очереди
            double elapsedSeconds = (DateTime.UtcNow - session.StartTime).TotalSeconds;

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

            finishedCount = session.FinishedCountIndex;

            // Находим первый активный файл для отображения его метрик
            int? targetIndex = null;
            foreach (var idx in session.FilesProgress.Keys)
            {
                if (!session.FinishedIndices.Contains(idx) && session.FilesProgress[idx] < 100.0)
                {
                    if (targetIndex == null || idx < targetIndex.Value)
                    {
                        targetIndex = idx;
                    }
                }
            }

            if (targetIndex.HasValue)
            {
                if (session.ActiveFps.TryGetValue(targetIndex.Value, out double f)) displayFps = f;
                if (session.ActiveBitrates.TryGetValue(targetIndex.Value, out string? b)) displayBitrate = b;
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

        ReportTaskbarProgress(overallPercent);

        if (syncScriptState)
        {
            script.RaiseStateChanged();
        }
    }

    /// <summary>
    /// Дублирует общий прогресс очереди в иконку панели задач (зелёное состояние).
    /// Служба сама троттлит вызовы и молча пропускает их без окна.
    /// </summary>
    private void ReportTaskbarProgress(double overallPercent)
    {
        _taskbarProgress?.SetProgress(overallPercent);
    }

    /// <summary>
    /// Синхронизирует иконку панели задач с итогом очереди:
    /// красное состояние только при ошибках элементов, иначе сброс индикатора
    /// (успех, отмена и пропуск без отказов не являются ошибкой).
    /// </summary>
    private void ReportTaskbarFinished(bool hasErrors, double finalPercent)
    {
        if (_taskbarProgress is null)
        {
            return;
        }

        if (hasErrors)
        {
            _taskbarProgress.SetError(finalPercent);
        }
        else
        {
            _taskbarProgress.Clear();
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
