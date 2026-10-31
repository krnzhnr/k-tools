using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.Messaging;

using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Encoders;
using KTools_App.Infrastructure;
using KTools_App.Services.Contracts;
using KTools_App.Services.Implementations;
using KTools_App.ViewModels;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

using Polly;
using Polly.Extensions.Http;

namespace KTools_App;

/// <summary>
/// Точка входа приложения.
/// Конфигурирует DI-контейнер для внедрения зависимостей
/// и инициализирует главное окно приложения.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Глобальный провайдер служб (DI-контейнер).
    /// Используется для получения зависимостей во Views.
    /// </summary>
    internal static IServiceProvider Services { get; private set; }
        = null!;

    internal const string CrashShutdownReasonUnhandled = "crash.unhandled_exception";
    internal const string CrashShutdownReasonWindowClosed = "window.closed";

    /// <summary>
    /// Имя переменной окружения для явного переопределения минимального уровня журналирования.
    /// </summary>
    public const string MinLevelEnvironmentVariable = "KTOOLS_LOG_MIN_LEVEL";
    internal static readonly LogLevel ReleaseMinLevel = LogLevel.Info;
    internal static readonly TimeSpan ProcessTerminationTimeout = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan ShutdownFlushTimeout = TimeSpan.FromSeconds(1);

    private static ILogService? _logService;
    private static ISettingsManager? _settingsManager;
    private static CrashCoordinator? _crashCoordinator;
    private static int _servicesDisposed;
    private static readonly object ArgsWatcherGate = new();
    private static readonly object SettingsStageGate = new();
    private static readonly object ShutdownTaskGate = new();
    private static FileSystemWatcher? _argsWatcher;
    private static Func<SettingsPersistenceResult>? _settingsPersistenceStage;
    private static Task<CrashShutdownResult>? _activeShutdownTask;
    private static CancellationTokenSource? _startupBackgroundCts;
    private static readonly object StartupBackgroundGate = new();
    private static long _argsWatcherSequence;
    private static readonly TimeSpan ArgsWatcherDebounceDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Признак диагностической сборки, в которой по умолчанию включается уровень Debug.
    /// </summary>
    internal static bool IsDiagnosticBuild =>
#if DEBUG
        true;
#else
        false;
#endif

    /// <summary>
    /// Определяет минимальный уровень журналирования при запуске.
    /// Release-сборка по умолчанию ограничивается уровнем Info, чтобы Debug-диагностика
    /// не попадала в пользовательский журнал; диагностическая сборка и явное
    /// переопределение переменной KTOOLS_LOG_MIN_LEVEL включают Debug.
    /// </summary>
    public static LogLevel ResolveStartupLogLevel(bool diagnosticBuild, string? rawOverride)
    {
        return TryParseLogLevel(rawOverride, out LogLevel level)
            ? level
            : diagnosticBuild ? LogLevel.Debug : ReleaseMinLevel;
    }

    private static bool TryParseLogLevel(string? raw, out LogLevel level)
    {
        level = ReleaseMinLevel;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        switch (raw.Trim().ToLowerInvariant())
        {
            case "debug":
            case "dbg":
            case "trace":
                level = LogLevel.Debug;
                return true;
            case "info":
            case "information":
                level = LogLevel.Info;
                return true;
            case "warning":
            case "warn":
                level = LogLevel.Warning;
                return true;
            case "error":
                level = LogLevel.Error;
                return true;
            case "fatal":
            case "critical":
                level = LogLevel.Fatal;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Глобальная ссылка на главное окно приложения.
    /// Необходима для инициализации системных диалогов
    /// (FolderPicker, FilePicker) через COM Interop.
    /// </summary>
    public static Window? CurrentMainWindow { get; private set; }

    /// <summary>
    /// Глобальная ссылка на DispatcherQueue UI-потока.
    /// </summary>
    public static Microsoft.UI.Dispatching.DispatcherQueue? UiDispatcherQueue { get; private set; }

    /// <summary>
    /// Инициализирует singleton-объект приложения
    /// и настраивает DI-контейнер.
    /// </summary>
    public App()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        InitializeComponent();
        Services = ConfigureServices();
        AppStartupPipeline.Run(
            Services.GetRequiredService<ILogService>(),
            () =>
            {
                _logService = Services.GetRequiredService<ILogService>();
                RegisterCrashHandlers(Services.GetRequiredService<CrashCoordinator>());
            },
            () =>
            {
                _settingsManager = Services.GetRequiredService<ISettingsManager>();
                return _settingsManager;
            });
    }

    private void RegisterCrashHandlers(CrashCoordinator coordinator)
    {
        _crashCoordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

        UnhandledException += (_, args) =>
        {
            args.Handled = coordinator.TryHandleWinUiUnhandled(
                args.Exception,
                "App.WinUi",
                BeginUnhandledShutdown,
                out CrashWriteResult? _);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            CrashCoordinator? current = _crashCoordinator;
            Exception? exception = args.ExceptionObject as Exception;
            if (current is null)
            {
                WriteEmergencyFallback(exception, args.IsTerminating);
                return;
            }

            current.RecordAppDomainTerminating(
                exception,
                args.IsTerminating,
                current.LastControlledShutdownCrashId);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashCoordinator? current = _crashCoordinator;
            if (current is not null && current.TryRecordUnobservedTaskException(args.Exception, out CrashWriteResult _))
            {
                args.SetObserved();
            }
        };
    }

    internal static bool BeginUnhandledShutdown()
    {
        return TryBeginControlledShutdown(CrashShutdownReasonUnhandled);
    }

    public static bool TryBeginControlledShutdown(string reason)
    {
        return StartControlledShutdown(reason, out _);
    }

    /// <summary>
    /// Асинхронно инициирует контролируемое завершение работы приложения с ограничением по времени.
    /// Не блокирует вызывающий поток (включая UI-поток), позволяя WinUI своевременно обрабатывать
    /// очередь сообщений и завершать процесс без подвисания интерфейса.
    /// </summary>
    /// <param name="reason">Причина завершения работы приложения.</param>
    /// <param name="timeout">Максимальное время ожидания завершения стадий.</param>
    /// <returns>True, если контролируемое завершение успешно завершилось в рамках таймаута; иначе false.</returns>
    public static async Task<bool> TryBeginControlledShutdownAsync(string reason, TimeSpan timeout)
    {
        if (!StartControlledShutdown(reason, out Task<CrashShutdownResult>? task))
        {
            return false;
        }

        if (task is null)
        {
            return IsControlledShutdownStarted;
        }

        TimeSpan effectiveTimeout = timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(10) : timeout;
        try
        {
            Task completedTask = await Task.WhenAny(task, Task.Delay(effectiveTimeout)).ConfigureAwait(false);
            if (!ReferenceEquals(completedTask, task))
            {
                return false;
            }

            CrashShutdownResult result = await task.ConfigureAwait(false);
            return result.Succeeded;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool TryBeginControlledShutdownAndWait(string reason, TimeSpan timeout)
    {
        if (!StartControlledShutdown(reason, out Task<CrashShutdownResult>? task))
        {
            return false;
        }

        if (task is null)
        {
            return IsControlledShutdownStarted;
        }

        try
        {
            return task.Wait(timeout) && task.Result.Succeeded;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool StartControlledShutdown(string reason, out Task<CrashShutdownResult>? task)
    {
        task = null;
        CrashCoordinator? coordinator = _crashCoordinator;
        if (coordinator is null)
        {
            return false;
        }

        bool accepted;
        try
        {
            accepted = coordinator.TryBeginControlledShutdown(
                reason,
                BeginProcessIntakeStop,
                out CrashShutdownStartResult startResult);
            if (!accepted)
            {
                WriteShutdownFailureEvent(
                    "Контролируемое завершение не запущено",
                    null,
                    startResult.ErrorCode);
                return false;
            }

            if (!startResult.Initiated)
            {
                lock (ShutdownTaskGate)
                {
                    task = _activeShutdownTask;
                }

                return true;
            }
        }
        catch (Exception ex)
        {
            WriteShutdownFailureEvent("При запуске контролируемого завершения возникло исключение", ex, "shutdown-start-exception");
            return false;
        }

        task = RunControlledShutdownTask(coordinator, reason);
        lock (ShutdownTaskGate)
        {
            _activeShutdownTask = task;
        }

        return true;
    }

    private static Task<CrashShutdownResult> RunControlledShutdownTask(
        CrashCoordinator coordinator,
        string reason)
    {
        Task<CrashShutdownResult> task;
        try
        {
            task = Task.Run(() => RunControlledShutdownStagesAsync(coordinator, reason));
        }
        catch (Exception ex)
        {
            RecordShutdownFailure(coordinator, ex);
            return Task.FromResult(BuildFaultedShutdownResult());
        }

        _ = task.ContinueWith(
            completed => CompleteControlledShutdown(coordinator, completed),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return task;
    }

    internal static void RegisterShutdownSettingsStage(Func<SettingsPersistenceResult> stage)
    {
        if (stage is null)
        {
            return;
        }

        lock (SettingsStageGate)
        {
            _settingsPersistenceStage ??= stage;
        }
    }

    private static bool BeginProcessIntakeStop()
    {
        if (!ActiveProcessTracker.TryBeginShutdown())
        {
            return false;
        }

        return !ActiveProcessTracker.IsAcceptingRegistrations;
    }

    private static Task<CrashShutdownResult> RunControlledShutdownStagesAsync(
        CrashCoordinator coordinator,
        string reason)
    {
        return coordinator.RunControlledShutdownStagesAsync(
            reason,
            stopOperations: _ => Task.FromResult(BeginProcessIntakeStop()),
            stopWatcher: _ => Task.FromResult(StopArgsWatcher()),
            terminateProcesses: _ => Task.FromResult(KillTrackedProcesses()),
            flush: _ => Task.FromResult(TryFlushLog()),
            disposeServices: () => Task.FromResult(DisposeServices()),
            requestExit: RequestExit,
            cancellationToken: CancellationToken.None,
            persistSettings: RunSettingsPersistenceStage,
            writeTerminalEvent: WriteTerminalShutdownEvent);
    }

    private static void CompleteControlledShutdown(
        CrashCoordinator coordinator,
        Task<CrashShutdownResult> task)
    {
        CrashShutdownResult result;
        try
        {
            result = task.Status == TaskStatus.RanToCompletion
                ? task.Result
                : BuildFaultedShutdownResult();
        }
        catch (Exception ex)
        {
            RecordShutdownFailure(coordinator, ex);
            return;
        }

        if (task.IsFaulted)
        {
            RecordShutdownFailure(coordinator, task.Exception);
            return;
        }

        if (!CrashCoordinator.IsShutdownOutcomeFailed(result))
        {
            return;
        }

        RecordShutdownStageFailure(coordinator, result);
    }

    private static CrashShutdownResult BuildFaultedShutdownResult()
    {
        return new CrashShutdownResult(
            shutdownRequested: true,
            operationsStopped: false,
            settingsPersisted: false,
            processesVerified: false,
            watcherStopped: false,
            flushSucceeded: false,
            servicesDisposed: false,
            exitRequested: false,
            CrashShutdownResult.ErrorStageFailed,
            ProcessTerminationSummary.Unavailable(CrashShutdownResult.ErrorStageFailed));
    }

    private static void RecordShutdownFailure(CrashCoordinator coordinator, Exception? exception)
    {
        WriteShutdownFailureEvent("Сбой выполнения задачи контролируемого завершения приложения", exception, CrashShutdownResult.ErrorStageFailed);
        try
        {
            coordinator.Record(coordinator.CreateRelatedRecord(
                "App.Shutdown",
                exception,
                "Задача контролируемого завершения не выполнена",
                LogLevel.Fatal,
                CrashEventKind.ControlledShutdown,
                emergencyRequired: true,
                coordinator.LastControlledShutdownCrashId));
        }
        catch (Exception)
        {
        }

        DisposeServices();
        RequestExit();
    }

    private static void RecordShutdownStageFailure(CrashCoordinator coordinator, CrashShutdownResult result)
    {
        string errorCode = string.IsNullOrEmpty(result.ErrorCode)
            ? CrashShutdownResult.ErrorStageFailed
            : result.ErrorCode!;
        Dictionary<string, object?> properties = CrashCoordinator.BuildStageProperties(result);
        properties["ErrorCode"] = errorCode;
        if (result.ProcessSummary is { } summary)
        {
            foreach (KeyValuePair<string, object?> pair in summary.ToLogProperties())
            {
                properties[pair.Key] = pair.Value;
            }
        }

        try
        {
            coordinator.Record(
                coordinator.CreateRelatedRecord(
                    "App.Shutdown",
                    null,
                    "Контролируемое завершение приложения завершилось с ошибками: " + errorCode,
                    LogLevel.Error,
                    CrashEventKind.ControlledShutdown,
                    emergencyRequired: true,
                    coordinator.LastControlledShutdownCrashId),
                properties);
        }
        catch (Exception)
        {
        }
    }

    private static Task<SettingsPersistenceResult> RunSettingsPersistenceStage(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        Func<SettingsPersistenceResult>? stage;
        lock (SettingsStageGate)
        {
            stage = _settingsPersistenceStage;
        }

        if (stage is null)
        {
            return Task.FromResult(SettingsPersistenceResult.StageUnavailable);
        }

        try
        {
            return Task.FromResult(stage());
        }
        catch (Exception)
        {
            return Task.FromResult(SettingsPersistenceResult.Failure("settings-persistence-exception"));
        }
    }

    private static Task WriteTerminalShutdownEvent(CrashShutdownResult result, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        bool pending = string.IsNullOrEmpty(result.ErrorCode)
            || string.Equals(result.ErrorCode, CrashShutdownResult.ErrorExitRequestPending, StringComparison.Ordinal);
        if (!result.ProcessesVerified)
        {
            ProcessTerminationSummary summary = result.ProcessSummary
                ?? ProcessTerminationSummary.Unavailable("process-summary-missing");
            Dictionary<string, object?> properties = summary.ToLogProperties();
            properties["ErrorCode"] = summary.ErrorCode ?? AppErrorCodes.ShutdownProcessFailure;
            WriteAppEvent(
                AppEventIds.ControlledShutdownProcessFailure,
                LogLevel.Error,
                LogStatus.Failed,
                "Активные процессы не подтверждены как завершённые при контролируемом завершении",
                null,
                properties);
        }

        if (!result.SettingsPersisted)
        {
            WriteAppEvent(
                AppEventIds.ControlledShutdownSettingsPersistenceFailed,
                LogLevel.Error,
                LogStatus.Failed,
                "Настройки не сохранены при контролируемом завершении",
                null,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Stage"] = "settings_persistence",
                    ["Persisted"] = false,
                    ["ErrorCode"] = string.IsNullOrEmpty(result.ErrorCode)
                        ? CrashShutdownResult.ErrorSettingsPersistenceFailed
                        : result.ErrorCode!
                });
        }

        if (!result.WatcherStopped)
        {
            WriteAppEvent(
                AppEventIds.ControlledShutdownWatcherFailure,
                LogLevel.Error,
                LogStatus.Failed,
                "Наблюдатель аргументов остановлен некорректно",
                null,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Stage"] = "watcher",
                    ["ErrorCode"] = CrashShutdownResult.ErrorWatcherStopFailed
                });
        }

        WriteAppEvent(
            CrashCoordinator.ControlledShutdownCompletedEventId,
            pending ? LogLevel.Info : LogLevel.Error,
            pending ? LogStatus.Succeeded : LogStatus.Failed,
            "Этапы контролируемого завершения выполнены до запроса выхода",
            null,
            CrashCoordinator.BuildStageProperties(result));
        return Task.CompletedTask;
    }

    private static ProcessTerminationSummary KillTrackedProcesses()
    {
        try
        {
            return ActiveProcessTracker.KillAll(ProcessTerminationTimeout);
        }
        catch (Exception ex)
        {
            ProcessTerminationSummary faulted = ProcessTerminationSummary.Faulted("termination-exception");
            Dictionary<string, object?> properties = faulted.ToLogProperties();
            properties["ErrorCode"] = AppErrorCodes.ShutdownProcessTerminationException;
            WriteAppEvent(
                AppEventIds.ControlledShutdownProcessFailure,
                LogLevel.Error,
                LogStatus.Failed,
                "Завершение процессов вызвало исключение при контролируемом завершении",
                ex,
                properties);
            return faulted;
        }
    }

    private static void WriteShutdownFailureEvent(string message, Exception? exception, string? errorCode)
    {
        WriteAppEvent(
            CrashCoordinator.ControlledShutdownFailedEventId,
            LogLevel.Error,
            LogStatus.Failed,
            message,
            exception,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Stage"] = "controlled_shutdown",
                ["Succeeded"] = false,
                ["Failed"] = true,
                ["ErrorCode"] = string.IsNullOrEmpty(errorCode) ? AppErrorCodes.ShutdownStageFailed : errorCode
            });
    }

    private static void WriteEmergencyFallback(Exception? exception, bool terminating)
    {
        try
        {
            EmergencyLogSink.Shared.Write(
                terminating ? "Критическое завершающее исключение AppDomain" : "Необработанное исключение AppDomain",
                exception,
                "App.Domain",
                "domain-" + Guid.NewGuid().ToString("N"),
                _crashCoordinator?.SessionId ?? Guid.Empty,
                LogLevel.Fatal);
        }
        catch (Exception)
        {
        }
    }

    private static bool TryFlushLog()
    {
        try
        {
            bool flushed = _logService?.Flush(ShutdownFlushTimeout) == true;
            if (!flushed)
            {
                WriteAppEvent(
                    AppEventIds.ControlledShutdownFlushFailure,
                    LogLevel.Error,
                    LogStatus.Failed,
                    "Сброс буфера журнала не завершён при контролируемом завершении",
                    null,
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["Stage"] = "flush",
                        ["ErrorCode"] = AppErrorCodes.ShutdownFlushIncomplete
                    });
            }

            return flushed;
        }
        catch (Exception ex)
        {
            WriteAppEvent(
                AppEventIds.ControlledShutdownFlushFailure,
                LogLevel.Error,
                LogStatus.Failed,
                "Сброс буфера журнала не удался при контролируемом завершении",
                ex,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Stage"] = "flush",
                    ["ErrorCode"] = AppErrorCodes.ShutdownFlushException
                });
            return false;
        }
    }

    internal static Task<bool> RequestExit()
    {
        if (Application.Current is null)
        {
            WriteShutdownFailureEvent(
                "У запроса выхода нет целевого приложения",
                null,
                ExitRequestExecutor.ErrorTargetMissing);
            return Task.FromResult(false);
        }

        Microsoft.UI.Dispatching.DispatcherQueue? queue = UiDispatcherQueue;
        Func<Action, bool>? enqueue = queue is null
            ? null
            : action =>
            {
                if (queue.HasThreadAccess)
                {
                    action();
                    return true;
                }

                return queue.TryEnqueue(() => action());
            };
        return ExitRequestExecutor.RequestAsync(
            enqueue,
            static () => Application.Current?.Exit(),
            ExitRequestExecutor.DefaultTimeout,
            static errorCode => WriteShutdownFailureEvent(
                "Запрос выхода не выполнен",
                null,
                errorCode));
    }

    internal static bool StopArgsWatcher()
    {
        CancelStartupBackgroundWork();
        Interlocked.Increment(ref _argsWatcherSequence);
        FileSystemWatcher? watcher;
        lock (ArgsWatcherGate)
        {
            watcher = _argsWatcher;
            _argsWatcher = null;
        }

        if (watcher is null)
        {
            return true;
        }

        try
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static bool DisposeServices()
    {
        if (Interlocked.Exchange(ref _servicesDisposed, 1) != 0)
        {
            return true;
        }

        try
        {
            (Services as IDisposable)?.Dispose();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static bool IsControlledShutdownStarted =>
        _crashCoordinator?.IsControlledShutdownStarted == true;

    private static void WriteAppEvent(
        AppEventId eventId,
        LogLevel level,
        LogStatus status,
        string message,
        Exception? exception,
        IReadOnlyDictionary<string, object?> properties)
    {
        try
        {
            _logService?.Write(
                eventId.Value,
                level,
                status,
                message,
                exception,
                "App",
                context: null,
                properties: properties);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Конфигурирует контейнер внедрения зависимостей,
    /// регистрируя все сервисы, синглтоны ядра и ViewModels.
    /// </summary>
    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // 0. Регистрация HttpClient с политиками Polly
        services.AddHttpClient("DefaultClient")
            .AddPolicyHandler(HttpPolicyExtensions
                .HandleTransientHttpError()
                .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt))));

        services.AddSingleton<LogService>();
        services.AddSingleton<ILogService>(provider => provider.GetRequiredService<LogService>());
        services.AddSingleton<EmergencyLogSink>();
        services.AddSingleton<CrashCoordinator>(provider => new CrashCoordinator(
            provider.GetRequiredService<EmergencyLogSink>(),
            provider.GetRequiredService<ILogService>()));
        services.AddSingleton<ICrashCoordinator>(provider => provider.GetRequiredService<CrashCoordinator>());
        services.AddSingleton<ISystemInfoService, SystemInfoService>();
        services.AddSingleton<IPathManager, PathManager>();
        services.AddSingleton<ISettingsManager, SettingsManager>();
        services.AddSingleton<IDependencyManager, DependencyManager>();
        services.AddSingleton<IScriptRegistry, ScriptRegistry>();

        services.AddSingleton<IHardwareCapabilityCache, HardwareCapabilityCache>();
        services.AddSingleton<IVideoEncoder, NvencEncoder>();
        services.AddSingleton<IVideoEncoder, CpuEncoder>();
        services.AddSingleton<VideoEncoderRegistry>();

        // Infrastructure-сервисы (Runner'ы)
        services.AddSingleton<IFFmpegRunner, FFmpegRunner>();
        services.AddSingleton<IEac3toRunner, Eac3toRunner>();
        services.AddSingleton<IMkvmergeRunner, MkvmergeRunner>();
        services.AddSingleton<QaacRunner>();
        services.AddSingleton<DeeRunner>();
        services.AddSingleton<IMediaProbeService, MediaProbeService>();
        services.AddSingleton<IBitrateAnalyzerService, BitrateAnalyzerService>();
        services.AddSingleton<IDiskTypeDetectorService, DiskTypeDetectorService>();
        services.AddSingleton<IAudioWaveformService, AudioWaveformService>();
        services.AddSingleton<IVttParser, VttParser>();
        services.AddSingleton<IAssParser, AssParser>();
        services.AddSingleton<IWhisperModelManager, WhisperModelManager>();
        services.AddSingleton<IWhisperRunner, WhisperRunner>();

        // Регистрация скриптов обработки медиа
        services.AddTransient<Scripts.MetadataCleanupScript>();
        services.AddTransient<Scripts.VideoEncodingScript>();
        services.AddTransient<Scripts.ContainerConversionScript>();
        services.AddTransient<Scripts.AudioEncodingScript>();
        services.AddTransient<Scripts.AudioDownmixScript>();
        services.AddTransient<Scripts.AudioSpeedScript>();
        services.AddTransient<Scripts.AudioChannelsScript>();
        services.AddTransient<Scripts.AudioTransplantScript>();
        services.AddTransient<Scripts.BitrateViewerScript>();
        services.AddTransient<Scripts.MediaDownloaderScript>();
        services.AddTransient<Scripts.MkvAssemblyScript>();
        services.AddTransient<Scripts.StreamManagementScript>();
        services.AddTransient<Scripts.StreamReplacementScript>();
        services.AddTransient<Scripts.TrackExtractorScript>();
        services.AddTransient<Scripts.SubtitlesConvertScript>();
        services.AddTransient<Scripts.AudioShiftScript>();
        services.AddTransient<Scripts.SubtitleShiftScript>();
        services.AddTransient<Scripts.SpeechRecognitionScript>();

        // 2. Регистрация служб приложения
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IWindowHandleProvider, WindowHandleProvider>();
        services.AddSingleton<IUpdateService, UpdateService>();
        services.AddSingleton<IInstallLocationService, InstallLocationService>();
        services.AddSingleton<MainWindow>();

        // 3. Регистрация ViewModels
        services.AddSingleton<MainViewModel>();
        services.AddTransient<HomeViewModel>();
        services.AddTransient<WorkPanelViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddTransient<LogViewModel>();
        services.AddSingleton<DependencySetupViewModel>();
        services.AddTransient<TrackSelectionViewModel>();
        services.AddTransient<ScriptSettingsViewModel>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Вызывается при запуске приложения.
    /// Инициализирует логирование, настройки и главное окно.
    /// </summary>
    protected override void OnLaunched(
        LaunchActivatedEventArgs args)
    {
        try
        {
            // Инициализируем логирование при старте приложения
            ILogService logService = _logService ?? Services.GetRequiredService<ILogService>();
            LogLevel startupMinLevel = ResolveStartupLogLevel(
                IsDiagnosticBuild,
                Environment.GetEnvironmentVariable(MinLevelEnvironmentVariable));
            logService.MinLevel = startupMinLevel;
            WriteAppEvent(
                AppEventIds.DiagnosticsLoggingArmed,
                LogLevel.Info,
                LogStatus.Changed,
                "Минимальный уровень журналирования установлен",
                null,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Stage"] = "logging",
                    ["Level"] = logService.MinLevel.ToString()
                });

            bool isAdmin = false;
            try
            {
                using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    var principal = new System.Security.Principal.WindowsPrincipal(identity);
                    isAdmin = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
            }
            catch (Exception ex)
            {
                WriteAppEvent(
                    AppEventIds.AdminCheckFailed,
                    LogLevel.Error,
                    LogStatus.Failed,
                    "Не удалось проверить статус администратора",
                    ex,
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["Stage"] = "administrator_check",
                        ["ErrorCode"] = AppErrorCodes.AdminCheckFailed
                    });
            }

            WriteAppEvent(
                AppEventIds.Started,
                LogLevel.Info,
                LogStatus.Running,
                "Запуск приложения начат",
                null,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["IsAdmin"] = isAdmin,
                    ["Platform"] = Environment.Is64BitProcess ? "x64" : "x86",
                    ["Version"] = LogService.ResolveApplicationVersion()
                });

            // Характеристики системы собираются в фоне после window.Activate()
            // (см. QueueSystemInfoSnapshotInBackground): опрос дисков до первого кадра запрещён.
            WriteAppEvent(
                AppEventIds.SettingsResolved,
                LogLevel.Info,
                LogStatus.Succeeded,
                "Служба настроек получена",
                null,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Stage"] = "settings"
                });

            // При первом запуске автоматически инициализируем
            // все настройки по умолчанию
            WriteAppEvent(
                AppEventIds.DefaultSettingsInitializationStarted,
                LogLevel.Info,
                LogStatus.Running,
                "Инициализация настроек по умолчанию начата",
                null,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Stage"] = "default_settings"
                });
            _ = Services.GetRequiredService<IScriptRegistry>().Scripts;
            // PERF-05: проба NVENC отложена на 10 с после старта, результат кэшируется на диске.
            // При раннем закрытии опрос не запускается: проверяем останов перед запуском.
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                    if (IsControlledShutdownStarted || Volatile.Read(ref _servicesDisposed) != 0)
                    {
                        return;
                    }

                    await Services.GetRequiredService<KTools_App.Encoders.IHardwareCapabilityCache>().InitializeAsync().ConfigureAwait(false);

                    // Проба завершена (из кэша или опросом): схема видео-скрипта,
                    // построенная ранее только с CPU, устарела. Уведомляем интерфейс
                    // для пересборки через существующий механизм смены набора ключей.
                    try
                    {
                        var dispatcher = UiDispatcherQueue;
                        if (dispatcher != null)
                        {
                            dispatcher.TryEnqueue(() =>
                            {
                                try
                                {
                                    WeakReferenceMessenger.Default.Send(new HardwareCapabilitiesChangedMessage());
                                }
                                catch (Exception)
                                {
                                }
                            });
                        }
                        else
                        {
                            WeakReferenceMessenger.Default.Send(new HardwareCapabilitiesChangedMessage());
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
                catch (Exception)
                {
                    // Отложенная проба не должна ронять фоновую задачу: сбои уже залогированы внутри кэша.
                }
            });

            // Интеграция контекстного меню обновляется в фоне после window.Activate()
            // (см. QueueShellIntegrationRefreshInBackground): реестр shell до первого кадра не читается.

            // Создаём и активируем главное окно
            var window = Services.GetRequiredService<MainWindow>();
            CurrentMainWindow = window;
            // Инициализируем провайдер дескриптора окна
            var handleProvider = Services
                .GetRequiredService<IWindowHandleProvider>();
            if (handleProvider is WindowHandleProvider provider)
            {
                provider.SetMainWindow(window);
            }

            UiDispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

            window.Activate();

            // Зафиксированный порядок старта (PERF-01): логгер → настройки → окно → Activate() →
            // фон (система, shell, pending-args, watcher). До первого кадра на UI-потоке выполняются
            // только дешёвые шаги; опрос дисков, реестр shell и чтение PendingArgs уходят в фон,
            // поэтому холодный старт не зависит от спящих дисков и мусора в папке PendingArgs.
            CancellationToken startupBackgroundToken = EnsureStartupBackgroundCancellation(window);
            QueueSystemInfoSnapshotInBackground(startupBackgroundToken);
            QueueShellIntegrationRefreshInBackground(startupBackgroundToken);
            QueueStaleCopyCheckInBackground(startupBackgroundToken);
            QueueStaleSetupCleanupInBackground(startupBackgroundToken);

            // Обрабатываем собственные параметры запуска
            var ownArgs = Environment.GetCommandLineArgs();
            var (script, filesList) = ParseCommandLineArray(ownArgs);
            if (!string.IsNullOrEmpty(script) || filesList.Count > 0)
            {
                WriteAppEvent(
                    AppEventIds.OwnArgumentsReceived,
                    LogLevel.Info,
                    LogStatus.Running,
                    "Получены аргументы собственного процесса",
                    null,
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["Count"] = filesList.Count,
                        ["Reason"] = string.IsNullOrEmpty(script) ? "files-only" : "script-and-files"
                    });
                WeakReferenceMessenger.Default.Send(new ShellActivationMessage(script, filesList));
            }

            // Обрабатываем накопленные аргументы от других экземпляров из папки PendingArgs.
            // Метод только ставит задачу в фон и возвращается сразу; доставка через диспетчер сохраняется.
            ProcessPendingArgsFiles();

            // Запускаем отслеживание новых файлов аргументов (события с дебаунсом уходят в фон).
            StartArgsWatcher();

            WriteDiagnosticsStatus(logService);
        }
        catch (Exception ex)
        {
            WriteAppEvent(
                AppEventIds.StartupFailed,
                LogLevel.Fatal,
                LogStatus.Failed,
                "Запуск приложения не выполнен",
                ex,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Stage"] = "startup",
                    ["ErrorCode"] = AppErrorCodes.StartupFailed
                });
            throw;
        }
    }

    /// <summary>
    /// Публикует снимок счётчиков журналирования, чтобы потери и отказы записи
    /// были наблюдаемы в production без доступа к внутреннему состоянию сервиса.
    /// </summary>
    private static void WriteDiagnosticsStatus(ILogService logService)
    {
        LogServiceStatus status = logService.Status;
        WriteAppEvent(
            AppEventIds.DiagnosticsStatus,
            LogLevel.Info,
            LogStatus.Succeeded,
            "Снимок счётчиков журналирования",
            null,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Stage"] = "logging",
                ["Level"] = status.MinLevel.ToString(),
                ["QueueLength"] = status.QueuedEvents,
                ["QueuedBytes"] = status.QueuedBytes,
                ["DroppedCount"] = status.DroppedEvents,
                ["RejectedCount"] = status.RejectedEvents,
                ["WriteErrors"] = status.WriteErrors,
                ["RotationErrors"] = status.RotationErrors,
                ["SubscriberErrors"] = status.SubscriberErrors,
                ["SubscriberDropped"] = status.SubscriberDropped,
                ["DisposeErrors"] = status.DisposeErrors,
                ["ReadErrors"] = status.ReadErrors
            });
    }

    /// <summary>
    /// Сканирует директорию PendingArgs и обрабатывает все перенаправленные аргументы.
    /// Сигнатура сохранена: метод только ставит обработку в очередь фона и сразу возвращается,
    /// поэтому безопасен для вызова из UI-потока и из обработчика активации второго экземпляра.
    /// </summary>
    public static void ProcessPendingArgsFiles()
    {
        CancellationToken token = GetStartupBackgroundToken();
        try
        {
            _ = Task.Run(() => ProcessPendingArgsFilesAsync(token));
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Асинхронно сканирует директорию PendingArgs и обрабатывает перенаправленные аргументы.
    /// Ожидание занятых файлов выполняется через Task.Delay с отменой, без блокировки потока.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены фоновой обработки при закрытии приложения.</param>
    internal static async Task ProcessPendingArgsFilesAsync(CancellationToken cancellationToken)
    {
        if (UiDispatcherQueue == null)
        {
            WriteAppEvent(
                AppEventIds.PendingArgsDeferred,
                LogLevel.Info,
                LogStatus.Skipped,
                "Обработка отложенных аргументов перенесена до готовности диспетчера интерфейса",
                null,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Stage"] = "pending_args"
                });
            return;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = PendingArgsChannel.ResolveDirectory();
            if (!Directory.Exists(directory))
            {
                return;
            }

            int pruned = PendingArgsChannel.Prune(directory, PendingArgsChannel.MaxAge, PendingArgsChannel.MaxRetainedFiles);
            if (pruned > 0)
            {
                WriteAppEvent(
                    AppEventIds.PendingArgsPruned,
                    LogLevel.Info,
                    LogStatus.Succeeded,
                    "Просроченные файлы отложенных аргументов удалены",
                    null,
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["Count"] = pruned,
                        ["Stage"] = "pending_args"
                    });
            }

            foreach (string file in PendingArgsChannel.EnumeratePendingFiles(directory, PendingArgsChannel.MaxRetainedFiles))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string[]? args = await TryReadPendingArgsFileAsync(file, cancellationToken).ConfigureAwait(false);
                if (args == null || args.Length == 0)
                {
                    continue;
                }

                try
                {
                    var (script, filesList) = ParseCommandLineArray(args);
                    if (UiDispatcherQueue is Microsoft.UI.Dispatching.DispatcherQueue dispatcherQueue)
                    {
                        bool enqueued = dispatcherQueue.TryEnqueue(() =>
                        {
                            WriteAppEvent(
                                AppEventIds.PendingArgsProcessed,
                                LogLevel.Info,
                                LogStatus.Succeeded,
                                "Отложенные аргументы обработаны",
                                null,
                                new Dictionary<string, object?>(StringComparer.Ordinal)
                                {
                                    ["Count"] = filesList.Count,
                                    ["Reason"] = string.IsNullOrEmpty(script) ? "files-only" : "script-and-files"
                                });
                            BringMainWindowToFront();
                            if (!string.IsNullOrEmpty(script) || filesList.Count > 0)
                            {
                                WeakReferenceMessenger.Default.Send(new ShellActivationMessage(script, filesList));
                            }
                        });
                        if (!enqueued)
                        {
                            WriteAppEvent(
                                AppEventIds.PendingArgsDispatchFailed,
                                LogLevel.Error,
                                LogStatus.Failed,
                                "Не удалось передать отложенные аргументы",
                                null,
                                new Dictionary<string, object?>(StringComparer.Ordinal)
                                {
                                    ["Stage"] = "pending_args",
                                    ["ErrorCode"] = AppErrorCodes.PendingArgsDispatchFailed
                                });
                        }
                    }
                }
                catch (Exception ex)
                {
                    WriteAppEvent(
                        AppEventIds.PendingArgsParseFailed,
                        LogLevel.Error,
                        LogStatus.Failed,
                        "Не удалось разобрать отложенные аргументы",
                        ex,
                        new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["Stage"] = "pending_args",
                            ["ErrorCode"] = AppErrorCodes.PendingArgsParseFailed
                        });
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Отмена при закрытии приложения — штатный путь, отдельно не журналируется.
        }
        catch (Exception ex)
        {
            WriteAppEvent(
                AppEventIds.PendingArgsDirectoryFailed,
                LogLevel.Error,
                LogStatus.Failed,
                "Нет доступа к каталогу отложенных аргументов",
                ex,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Stage"] = "pending_args",
                    ["ErrorCode"] = AppErrorCodes.PendingArgsDirectoryFailed
                });
        }
    }

    /// <summary>
    /// Читает файл отложенных аргументов с ограниченными повторными попытками.
    /// Паузы между попытками поток не блокируют: используется Task.Delay с отменой.
    /// </summary>
    /// <param name="file">Путь к файлу отложенных аргументов.</param>
    /// <param name="cancellationToken">Токен отмены при закрытии приложения.</param>
    /// <returns>Строки аргументов либо null, если файл недоступен или обработка отменена.</returns>
    private static async Task<string[]?> TryReadPendingArgsFileAsync(string file, CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!File.Exists(file))
                {
                    return null;
                }

                string[] args = await File.ReadAllLinesAsync(file, cancellationToken).ConfigureAwait(false);
                PendingArgsChannel.TryDelete(file);
                return args;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (IOException ex)
            {
                WriteAppEvent(
                    AppEventIds.PendingArgsRetry,
                    LogLevel.Warning,
                    LogStatus.RetryScheduled,
                    "Файл отложенных аргументов занят",
                    ex,
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["Attempt"] = attempt + 1,
                        ["Stage"] = "pending_args"
                    });
                if (attempt < 2)
                {
                    try
                    {
                        await Task.Delay((attempt + 1) * 150, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return null;
                    }
                }
            }
            catch (Exception ex)
            {
                WriteAppEvent(
                    AppEventIds.PendingArgsReadFailed,
                    LogLevel.Error,
                    LogStatus.Failed,
                    "Не удалось прочитать файл отложенных аргументов",
                    ex,
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["Stage"] = "pending_args",
                        ["ErrorCode"] = AppErrorCodes.PendingArgsReadFailed
                    });
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Ставит сбор характеристик системы в очередь фона после первого кадра.
    /// Тяжёлый опрос дисков UI-поток не блокирует.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены при закрытии приложения.</param>
    private static void QueueSystemInfoSnapshotInBackground(CancellationToken cancellationToken)
    {
        ISystemInfoService service;
        try
        {
            service = Services.GetRequiredService<ISystemInfoService>();
        }
        catch (Exception ex)
        {
            WriteSystemInfoFailed(ex);
            return;
        }

        try
        {
            _ = Task.Run(() => WriteSystemSnapshotInBackgroundAsync(service, cancellationToken));
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Собирает характеристики системы в фоне.
    /// Совместимость с блоком 02: при наличии раздельных методов быстрой и расширенной частей
    /// используются они, иначе вызывается существующий общий метод. Набор событий журнала
    /// в обоих вариантах одинаковый, меняется только поток выполнения.
    /// </summary>
    /// <param name="service">Сервис характеристик системы.</param>
    /// <param name="cancellationToken">Токен отмены при закрытии приложения.</param>
    private static async Task WriteSystemSnapshotInBackgroundAsync(ISystemInfoService service, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await TryWriteSplitSystemSnapshotAsync(service, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            await Task.Run(() => service.LogSystemCharacteristics(), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Отмена при закрытии приложения — штатный путь, отдельно не журналируется.
        }
        catch (Exception ex)
        {
            WriteSystemInfoFailed(ex);
        }
    }

    /// <summary>
    /// Пытается собрать снимок раздельными методами блока 02 через позднее связывание,
    /// чтобы порядок слияния блоков 01 и 02 не влиял на компиляцию.
    /// </summary>
    /// <param name="service">Сервис характеристик системы.</param>
    /// <param name="cancellationToken">Токен отмены фоновой операции.</param>
    /// <returns>True, если раздельные методы найдены и выполнены; иначе false.</returns>
    private static async Task<bool> TryWriteSplitSystemSnapshotAsync(ISystemInfoService service, CancellationToken cancellationToken)
    {
        System.Reflection.MethodInfo? essential = service.GetType().GetMethod(
            "LogEssentialCharacteristics",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
            null,
            Type.EmptyTypes,
            null);
        System.Reflection.MethodInfo? extended = service.GetType().GetMethod(
            "LogExtendedCharacteristicsAsync",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
            null,
            new[] { typeof(CancellationToken) },
            null);
        if (essential is null || extended is null || !typeof(Task).IsAssignableFrom(extended.ReturnType))
        {
            return false;
        }

        try
        {
            essential.Invoke(service, null);
        }
        catch (System.Reflection.TargetInvocationException ex)
        {
            WriteSystemInfoFailed(ex.InnerException ?? ex);
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            object? result = extended.Invoke(service, new object[] { cancellationToken });
            if (result is Task task)
            {
                await task.ConfigureAwait(false);
            }
        }
        catch (System.Reflection.TargetInvocationException ex)
        {
            WriteSystemInfoFailed(ex.InnerException ?? ex);
        }

        return true;
    }

    /// <summary>
    /// Журналирует сбой сбора характеристик системы единым событием.
    /// </summary>
    /// <param name="exception">Перехваченное исключение.</param>
    private static void WriteSystemInfoFailed(Exception exception)
    {
        WriteAppEvent(
            AppEventIds.SystemInfoFailed,
            LogLevel.Error,
            LogStatus.Failed,
            "Не удалось получить характеристики системы",
            exception,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Stage"] = "system_info",
                ["ErrorCode"] = AppErrorCodes.SystemInfoFailed
            });
    }

    /// <summary>
    /// Ставит обновление интеграции контекстного меню в очередь фона после первого кадра.
    /// Реестр shell опрашивается вне UI-потока, сбои обрабатываются best-effort.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены при закрытии приложения.</param>
    private static void QueueShellIntegrationRefreshInBackground(CancellationToken cancellationToken)
    {
        try
        {
            _ = Task.Run(() => RefreshShellIntegrationInBackground(cancellationToken));
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Проверяет в фоне, не запущена ли неосновная копия приложения, и предлагает перейти к основной установке.
    /// Тихие обновления ставятся в зарегистрированную папку, поэтому старая копия из другой папки
    /// при повторном запуске снова предлагает то же обновление. Проверка пропускается в диагностических сборках.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены при закрытии приложения.</param>
    private static void QueueStaleCopyCheckInBackground(CancellationToken cancellationToken)
    {
        if (IsDiagnosticBuild)
        {
            return;
        }

        try
        {
            _ = Task.Run(() => CheckStaleCopyInBackgroundAsync(cancellationToken), cancellationToken);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Удаляет в фоне устаревшие файлы собственных установщиков из временной папки.
    /// Каждый автоапдейт оставляет там копию установщика, без чистки они копятся гигабайтами.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены при закрытии приложения.</param>
    private static void QueueStaleSetupCleanupInBackground(CancellationToken cancellationToken)
    {
        try
        {
            _ = Task.Run(() =>
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Services.GetRequiredService<IUpdateService>().CleanupStaleSetupFiles();
                }
                catch (Exception)
                {
                    // Чистка мусора не должна ронять фоновую задачу: сбои уже залогированы внутри службы.
                }
            }, cancellationToken);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Ждёт settling главного окна и показывает диалог перехода, если текущий запуск выполнен из неосновной копии.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены при закрытии приложения.</param>
    private static async Task CheckStaleCopyInBackgroundAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            IInstallLocationService installLocationService = Services.GetRequiredService<IInstallLocationService>();
            StaleCopyInfo? staleCopy = installLocationService.DetectStaleCopy();
            if (staleCopy is null || !staleCopy.ShouldWarn)
            {
                return;
            }

            WriteAppEvent(
                AppEventIds.StaleCopyDetected,
                LogLevel.Warning,
                LogStatus.Changed,
                "Обнаружен запуск из неосновной копии приложения",
                null,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Stage"] = "stale_copy",
                    ["Version"] = staleCopy.RegisteredVersion,
                    ["Reason"] = "UpdateTargetMismatch"
                });

            IDialogService dialogService = Services.GetRequiredService<IDialogService>();
            bool openMain = await dialogService.ShowConfirmationAsync(
                "Запущена не основная копия",
                $"Вы запустили копию версии {staleCopy.CurrentVersion} из папки:{Environment.NewLine}{staleCopy.CurrentDirectory}"
                    + $"{Environment.NewLine}{Environment.NewLine}Основная установка — версия {staleCopy.RegisteredVersion} в папке:{Environment.NewLine}{staleCopy.RegisteredDirectory}"
                    + $"{Environment.NewLine}{Environment.NewLine}Обновления устанавливаются в основную папку, поэтому эта копия обновляться не будет. Открыть основную копию и закрыть эту?",
                "Открыть основную",
                "Продолжить здесь").ConfigureAwait(false);

            if (!openMain)
            {
                WriteAppEvent(
                    AppEventIds.StaleCopySkipped,
                    LogLevel.Info,
                    LogStatus.Skipped,
                    "Пользователь продолжил работу в неосновной копии",
                    null,
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["Stage"] = "stale_copy",
                        ["Reason"] = "UserDeclined"
                    });
                return;
            }

            if (installLocationService.TryLaunchRegisteredCopy(staleCopy))
            {
                WriteAppEvent(
                    AppEventIds.StaleCopyRedirected,
                    LogLevel.Info,
                    LogStatus.Succeeded,
                    "Выполнено перенаправление в основную копию приложения",
                    null,
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["Stage"] = "stale_copy",
                        ["Version"] = staleCopy.RegisteredVersion
                    });
                RequestExitOnUiThread();
                return;
            }

            WriteAppEvent(
                AppEventIds.StaleCopyRedirectFailed,
                LogLevel.Error,
                LogStatus.Failed,
                "Не удалось запустить основную копию приложения",
                null,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Stage"] = "stale_copy",
                    ["ErrorCode"] = AppErrorCodes.StaleCopyRedirectFailed
                });
            await dialogService.ShowMessageAsync(
                "Не удалось открыть основную копию",
                $"Запустите вручную файл:{Environment.NewLine}{staleCopy.RegisteredExecutablePath}").ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Отмена при закрытии приложения — штатный путь для фоновой проверки.
        }
        catch (Exception ex)
        {
            WriteAppEvent(
                AppEventIds.StaleCopyCheckFailed,
                LogLevel.Error,
                LogStatus.Failed,
                "Проверка неосновной копии не выполнена",
                ex,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Stage"] = "stale_copy",
                    ["ErrorCode"] = AppErrorCodes.StaleCopyCheckFailed
                });
        }
    }

    /// <summary>
    /// Завершает текущую копию на UI-потоке после запуска основной:
    /// место первичного экземпляра освобождается, и основная копия с флагом захвата занимает его.
    /// </summary>
    private static void RequestExitOnUiThread()
    {
        try
        {
            var queue = CurrentMainWindow?.DispatcherQueue ?? UiDispatcherQueue;
            if (queue != null && !queue.HasThreadAccess)
            {
                queue.TryEnqueue(() =>
                {
                    try
                    {
                        Application.Current.Exit();
                    }
                    catch (Exception enqueueEx)
                    {
                        WriteAppEvent(
                            AppEventIds.StaleCopyRedirectFailed,
                            LogLevel.Warning,
                            LogStatus.PartiallySucceeded,
                            "Основная копия запущена, но завершить текущую копию автоматически не удалось, закройте её вручную",
                            enqueueEx,
                            new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["Stage"] = "stale_copy",
                                ["ErrorCode"] = AppErrorCodes.StaleCopyCheckFailed
                            });
                    }
                });
                return;
            }

            Application.Current.Exit();
        }
        catch (Exception ex)
        {
            WriteAppEvent(
                AppEventIds.StaleCopyRedirectFailed,
                LogLevel.Warning,
                LogStatus.PartiallySucceeded,
                "Основная копия запущена, но завершить текущую копию автоматически не удалось, закройте её вручную",
                ex,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Stage"] = "stale_copy",
                    ["ErrorCode"] = AppErrorCodes.StaleCopyCheckFailed
                });
        }
    }

    /// <summary>
    /// Обновляет ключи контекстного меню в реестре, если интеграция включена.
    /// Выполняется в фоне; ошибки на старт приложения не влияют.
    /// Неосновная копия контекстное меню на себя не перерегистрирует.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены при закрытии приложения.</param>
    private static void RefreshShellIntegrationInBackground(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Services.GetRequiredService<IInstallLocationService>().DetectStaleCopy() is not null)
            {
                WriteAppEvent(
                    AppEventIds.ShellIntegrationSkippedStaleCopy,
                    LogLevel.Info,
                    LogStatus.Skipped,
                    "Перерегистрация контекстного меню пропущена: запуск выполнен из неосновной копии",
                    null,
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["Stage"] = "shell_integration",
                        ["Reason"] = "StaleCopy"
                    });
                return;
            }

            ISettingsManager settingsManager = _settingsManager ?? Services.GetRequiredService<ISettingsManager>();
            if (settingsManager.GetSetting("Shell", "IsContextMenuEnabled", false))
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                {
                    var scriptRegistry = Services.GetRequiredService<IScriptRegistry>();
                    var scripts = scriptRegistry.Scripts.Select(s => s.Name).ToList();
                    if (ShellIntegration.NeedsUpdate(exePath, scripts))
                    {
                        ShellIntegration.Register(exePath, scripts);
                        WriteAppEvent(
                            AppEventIds.ShellIntegrationUpdated,
                            LogLevel.Info,
                            LogStatus.Succeeded,
                            "Интеграция с оболочкой обновлена",
                            null,
                            new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["Stage"] = "shell_integration"
                            });
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Отмена при закрытии приложения — штатный путь для фонового обновления.
        }
        catch (Exception ex)
        {
            WriteAppEvent(
                AppEventIds.ShellIntegrationFailed,
                LogLevel.Error,
                LogStatus.Failed,
                "Не удалось обновить интеграцию с оболочкой",
                ex,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Stage"] = "shell_integration",
                    ["ErrorCode"] = AppErrorCodes.ShellIntegrationFailed
                });
        }
    }

    /// <summary>
    /// Создаёт токен отмены фоновых стартовых работ и связывает его с закрытием главного окна.
    /// </summary>
    /// <param name="window">Главное окно приложения.</param>
    /// <returns>Токен отмены фоновых работ.</returns>
    private static CancellationToken EnsureStartupBackgroundCancellation(Window window)
    {
        CancellationTokenSource fresh = new();
        CancellationTokenSource? previous;
        lock (StartupBackgroundGate)
        {
            previous = _startupBackgroundCts;
            _startupBackgroundCts = fresh;
        }

        DisposeCancellationSource(previous);
        window.Closed += (_, _) => CancelStartupBackgroundWork();
        return fresh.Token;
    }

    /// <summary>
    /// Возвращает токен отмены фоновых стартовых работ либо пустой токен, если старт ещё не настроен.
    /// </summary>
    private static CancellationToken GetStartupBackgroundToken()
    {
        lock (StartupBackgroundGate)
        {
            return _startupBackgroundCts?.Token ?? CancellationToken.None;
        }
    }

    /// <summary>
    /// Отменяет фоновые стартовые работы при закрытии приложения.
    /// </summary>
    private static void CancelStartupBackgroundWork()
    {
        CancellationTokenSource? current;
        lock (StartupBackgroundGate)
        {
            current = _startupBackgroundCts;
            _startupBackgroundCts = null;
        }

        if (current is null)
        {
            return;
        }

        try
        {
            current.Cancel();
        }
        catch (Exception)
        {
        }
        finally
        {
            DisposeCancellationSource(current);
        }
    }

    /// <summary>
    /// Освобождает источник токенов отмены без распространения исключений.
    /// </summary>
    /// <param name="source">Источник токенов отмены.</param>
    private static void DisposeCancellationSource(CancellationTokenSource? source)
    {
        if (source is null)
        {
            return;
        }

        try
        {
            source.Dispose();
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Запускает FileSystemWatcher для отслеживания новых файлов аргументов в директории PendingArgs.
    /// Обработчики только планируют задачу с дебаунсом и сразу возвращаются: поток наблюдателя не блокируется.
    /// </summary>
    private static void StartArgsWatcher()
    {
        try
        {
            string directory = PendingArgsChannel.ResolveDirectory();
            Directory.CreateDirectory(directory);
            FileSystemWatcher watcher = new(directory, PendingArgsChannel.FileSearchPattern)
            {
                EnableRaisingEvents = true
            };
            watcher.Created += (_, _) => SchedulePendingArgsProcessing();
            watcher.Changed += (_, _) => SchedulePendingArgsProcessing();
            lock (ArgsWatcherGate)
            {
                _argsWatcher = watcher;
            }
        }
        catch (Exception ex)
        {
            WriteAppEvent(
                AppEventIds.PendingArgsWatcherFailed,
                LogLevel.Error,
                LogStatus.Failed,
                "Не удалось запустить наблюдатель отложенных аргументов",
                ex,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Stage"] = "pending_args",
                    ["ErrorCode"] = AppErrorCodes.PendingArgsWatcherFailed
                });
        }
    }

    /// <summary>
    /// Планирует обработку отложенных аргументов с дебаунсом: быстрая серия событий
    /// наблюдателя сливается в один фоновый проход, поток наблюдателя не блокируется.
    /// </summary>
    private static void SchedulePendingArgsProcessing()
    {
        long sequence = Interlocked.Increment(ref _argsWatcherSequence);
        CancellationToken startupToken = GetStartupBackgroundToken();
        try
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(ArgsWatcherDebounceDelay, startupToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (Interlocked.Read(ref _argsWatcherSequence) != sequence)
                {
                    // Вытеснено более новым событием — этот проход пропускается.
                    return;
                }

                await ProcessPendingArgsFilesAsync(startupToken).ConfigureAwait(false);
            });
        }
        catch (Exception)
        {
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;

    /// <summary>
    /// Выводит главное окно приложения на передний план, восстанавливая его из свернутого состояния при необходимости.
    /// </summary>
    public static void BringMainWindowToFront()
    {
        if (CurrentMainWindow == null) return;

        try
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(CurrentMainWindow);
            if (hwnd != IntPtr.Zero)
            {
                if (IsIconic(hwnd))
                {
                    ShowWindow(hwnd, SW_RESTORE);
                }
                else
                {
                    ShowWindow(hwnd, SW_SHOW);
                }
                SetForegroundWindow(hwnd);
            }

            CurrentMainWindow.Activate();
        }
        catch (Exception ex)
        {
            WriteAppEvent(
                AppEventIds.WindowActivationFailed,
                LogLevel.Error,
                LogStatus.Failed,
                "Не удалось активировать главное окно",
                ex,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Stage"] = "window_activation",
                    ["ErrorCode"] = AppErrorCodes.WindowActivationFailed
                });
        }
    }

    /// <summary>
    /// Обрабатывает аргументы запуска приложения (активации) и перенаправляет их через Messenger.
    /// </summary>
    public static void HandleActivation(AppActivationArguments args)
    {
        ProcessPendingArgsFiles();
    }

    public static (string? Script, List<string> Files) ParseCommandLineArray(string[] args)
    {
        string? script = null;
        var files = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--script", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                script = args[i + 1];
                i++;
            }
            else
            {
                string path = args[i];
                path = path.Trim('\"');

                // Игнорируем сам исполняемый файл или сборку приложения (сравниваем имя без расширения, чтобы отфильтровать и .exe, и .dll)
                string fileNameWithoutExt = System.IO.Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                string currentExeNameWithoutExt = System.IO.Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "ktools.app").ToLowerInvariant();
                if (fileNameWithoutExt == currentExeNameWithoutExt || fileNameWithoutExt == "ktools.app" || fileNameWithoutExt == "ktools_app")
                {
                    continue;
                }

                if (System.IO.File.Exists(path) || System.IO.Directory.Exists(path))
                {
                    files.Add(path);
                }
            }
        }
        return (script, files);
    }

    public static string[] SplitCommandLine(string commandLine)
    {
        var args = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine)) return args.ToArray();

        var inQuotes = false;
        var current = new StringBuilder();
        for (int i = 0; i < commandLine.Length; i++)
        {
            char c = commandLine[i];
            if (c == '\"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ' ' && !inQuotes)
            {
                if (current.Length > 0)
                {
                    args.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }
        if (current.Length > 0)
        {
            args.Add(current.ToString());
        }
        return args.ToArray();
    }
}

/// <summary>
/// Зафиксированный порядок стартовой инициализации приложения:
/// журналирование поднимается первым, глобальные обработчики сбоя
/// регистрируются до разрешения менеджера настроек, а менеджер настроек
/// настраивает фактический каталог журнала до первого бизнес-события.
/// </summary>
public static class AppStartupPipeline
{
    public const string LoggerStage = "logger-resolved";
    public const string CrashHandlersStage = "crash-handlers-registered";
    public const string SettingsStage = "settings-resolved";

    public static IReadOnlyList<string> Run(
        ILogService logger,
        Action registerCrashHandlers,
        Func<ISettingsManager> resolveSettings)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(registerCrashHandlers);
        ArgumentNullException.ThrowIfNull(resolveSettings);

        List<string> completed = new(3) { LoggerStage };
        registerCrashHandlers();
        completed.Add(CrashHandlersStage);
        resolveSettings();
        completed.Add(SettingsStage);
        return completed;
    }
}

/// <summary>
/// Сообщение активации через командную строку/Проводник.
/// </summary>
public sealed class ShellActivationMessage
{
    public string? ScriptTag { get; }
    public List<string> Files { get; }

    public ShellActivationMessage(string? scriptTag, List<string> files)
    {
        ScriptTag = scriptTag;
        Files = files;
    }
}

/// <summary>
/// Сообщение о завершении опроса аппаратных возможностей кодирования (NVENC).
/// Схема настроек видео-скрипта строится до завершения фоновой пробы,
/// поэтому интерфейс пересобирается по этому сообщению.
/// </summary>
public sealed class HardwareCapabilitiesChangedMessage
{
}

/// <summary>
/// Ограниченный по времени исполнитель запроса выхода из приложения.
/// Возвращает фактический результат: успех возможен только когда запрос
/// поставлен в очередь диспетчера и действие выхода выполнено без исключения.
/// </summary>
public static class ExitRequestExecutor
{
    public const string ErrorTargetMissing = "exit-target-missing";
    public const string ErrorEnqueueFailed = "exit-enqueue-failed";
    public const string ErrorExecuteFailed = "exit-execute-failed";
    public const string ErrorTimeout = "exit-timeout";

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    public static async Task<bool> RequestAsync(
        Func<Action, bool>? enqueue,
        Action exit,
        TimeSpan timeout,
        Action<string>? onFailure)
    {
        if (exit is null)
        {
            Report(onFailure, ErrorTargetMissing);
            return false;
        }

        TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (enqueue is null)
        {
            if (!TryExecute(exit, completion))
            {
                Report(onFailure, ErrorExecuteFailed);
                return false;
            }
        }
        else
        {
            bool enqueued;
            try
            {
                enqueued = enqueue(() => TryExecute(exit, completion));
            }
            catch (Exception)
            {
                Report(onFailure, ErrorEnqueueFailed);
                return false;
            }

            if (!enqueued)
            {
                Report(onFailure, ErrorEnqueueFailed);
                return false;
            }
        }

        Task completed;
        try
        {
            completed = await Task.WhenAny(completion.Task, Task.Delay(timeout)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            Observe(completion.Task);
            Report(onFailure, ErrorTimeout);
            return false;
        }

        if (!ReferenceEquals(completed, completion.Task))
        {
            Observe(completion.Task);
            Report(onFailure, ErrorTimeout);
            return false;
        }

        try
        {
            return await completion.Task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            Report(onFailure, ErrorExecuteFailed);
            return false;
        }
    }

    private static bool TryExecute(Action exit, TaskCompletionSource<bool> completion)
    {
        try
        {
            exit();
            completion.TrySetResult(true);
            return true;
        }
        catch (Exception)
        {
            completion.TrySetException(new InvalidOperationException(ErrorExecuteFailed));
            return false;
        }
    }

    private static void Observe(Task task)
    {
        _ = task.ContinueWith(
            static observed => _ = observed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void Report(Action<string>? onFailure, string errorCode)
    {
        try
        {
            onFailure?.Invoke(errorCode);
        }
        catch (Exception)
        {
        }
    }
}
