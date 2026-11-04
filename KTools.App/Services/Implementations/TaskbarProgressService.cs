// -*- coding: utf-8 -*-
using System;
using System.Runtime.InteropServices;

using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Services.Contracts;

namespace KTools_App.Services.Implementations;

/// <summary>
/// Прогресс обработки в иконке на панели задач через Win32 ITaskbarList3.
/// Обновляется из того же места, что и полоса прогресса в интерфейсе,
/// с внутренним троттлингом не чаще ~10 раз в секунду.
/// Не бросает исключений: при отсутствии окна или сбое COM вызовы молча пропускаются.
/// </summary>
public class TaskbarProgressService : ITaskbarProgressService
{
    private const string TaskbarInitFailedEventId = "taskbar.init.failed";
    private const string TaskbarUpdateFailedEventId = "taskbar.update.failed";
    private const string SourceName = nameof(TaskbarProgressService);

    // Точность передачи в таскбар: 1000 шагов на 100%.
    private const ulong TaskbarTotal = 1000UL;

    // Троттлинг обновлений: чаще 100 мс шлёт только смена состояния и терминальные значения.
    private const int ThrottleMs = 100;

    private readonly IWindowHandleProvider _handleProvider;
    private readonly ILogService _logService;
    private readonly object _gate = new();
    private readonly Func<int> _tickProvider;

    private object? _taskbarInstance;
    private bool _initialized;
    private bool _unavailable;
    private bool _initFailureLogged;
    private bool _updateFailureLogged;
    private int _lastEmitTick;
    private double _lastPercent = double.NaN;
    private TaskbarState _lastState = TaskbarState.NoProgress;
    private bool _hasEmitted;

    /// <summary>
    /// Инициализирует новый экземпляр службы прогресса панели задач.
    /// </summary>
    public TaskbarProgressService(
        IWindowHandleProvider handleProvider,
        ILogService logService)
        : this(handleProvider, logService, () => Environment.TickCount)
    {
    }

    /// <summary>
    /// Конструктор с подменой часов для детерминированных тестов троттлинга.
    /// </summary>
    public TaskbarProgressService(
        IWindowHandleProvider handleProvider,
        ILogService logService,
        Func<int> tickProvider)
    {
        _handleProvider = handleProvider ?? throw new ArgumentNullException(nameof(handleProvider));
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _tickProvider = tickProvider ?? throw new ArgumentNullException(nameof(tickProvider));
    }

    /// <inheritdoc />
    public void SetProgress(double percent)
    {
        Emit(ClampPercent(percent), TaskbarState.Normal, force: false);
    }

    /// <inheritdoc />
    public void SetError(double percent)
    {
        Emit(ClampPercent(percent), TaskbarState.Error, force: true);
    }

    /// <inheritdoc />
    public void Clear()
    {
        Emit(0.0, TaskbarState.NoProgress, force: true);
    }

    /// <summary>
    /// Обрезает процент к диапазону 0-100, заменяя нечисловые значения нулём.
    /// </summary>
    internal static double ClampPercent(double percent)
    {
        if (double.IsNaN(percent) || double.IsInfinity(percent))
        {
            return 0.0;
        }

        if (percent < 0.0)
        {
            return 0.0;
        }

        if (percent > 100.0)
        {
            return 100.0;
        }

        return percent;
    }

    /// <summary>
    /// Переводит проценты в пару (выполнено, всего) для ITaskbarList3.
    /// </summary>
    internal static (ulong Completed, ulong Total) ToTaskbarValue(double percent)
    {
        double clamped = ClampPercent(percent);
        ulong completed = (ulong)Math.Round(clamped * (TaskbarTotal / 100.0), MidpointRounding.AwayFromZero);
        return (Math.Min(completed, TaskbarTotal), TaskbarTotal);
    }

    private void Emit(double percent, TaskbarState state, bool force)
    {
        int now;
        IntPtr hwnd;
        bool shouldApply;
        ulong completed = 0UL;

        lock (_gate)
        {
            if (_unavailable)
            {
                return;
            }

            now = _tickProvider();
            bool stateChanged = !_hasEmitted || state != _lastState;
            bool terminal = state != TaskbarState.Normal || percent >= 100.0 || percent <= 0.0;
            bool percentChanged = !_hasEmitted || Math.Abs(percent - _lastPercent) > 0.0001;
            bool throttled = !force && !stateChanged && !terminal && _hasEmitted && (uint)(now - _lastEmitTick) < (uint)ThrottleMs;

            if (throttled)
            {
                return;
            }

            // Повторный сброс в NoProgress без изменений не шлём в COM.
            if (state == TaskbarState.NoProgress && _hasEmitted && !stateChanged && !percentChanged)
            {
                return;
            }

            if (!_hasEmitted && state == TaskbarState.NoProgress)
            {
                _hasEmitted = true;
                _lastState = TaskbarState.NoProgress;
                _lastPercent = 0.0;
                return;
            }

            try
            {
                hwnd = _handleProvider.GetMainWindowHandle();
            }
            catch (Exception ex)
            {
                MarkUnavailable("Дескриптор главного окна для прогресса панели задач недоступен", ex, "TASKBAR_HANDLE_FAILED");
                return;
            }

            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            if (!EnsureInitialized())
            {
                return;
            }

            (ulong done, _) = ToTaskbarValue(percent);
            completed = done;
            shouldApply = true;

            _lastEmitTick = now;
            _lastPercent = percent;
            _lastState = state;
            _hasEmitted = true;
        }

        if (shouldApply)
        {
            ApplyProgress(hwnd, completed, TaskbarTotal, (int)state);
        }
    }

    private bool EnsureInitialized()
    {
        // Вызывается под _gate.
        if (_initialized)
        {
            return true;
        }

        try
        {
            object instance = new CTaskbarList();
            ((ITaskbarList3)instance).HrInit();
            _taskbarInstance = instance;
            _initialized = true;
            return true;
        }
        catch (Exception ex)
        {
            MarkUnavailable("Прогресс панели задач недоступен, индикатор очереди в иконке отключён", ex, "TASKBAR_INIT_FAILED");
            return false;
        }
    }

    /// <summary>
    /// Применяет состояние к панели задач. Виртуальный для подмены в тестах.
    /// Никогда не бросает исключений наружу.
    /// </summary>
    protected virtual void ApplyProgress(IntPtr hwnd, ulong completed, ulong total, int state)
    {
        try
        {
            object? instance;
            lock (_gate)
            {
                instance = _taskbarInstance;
            }

            if (instance is not ITaskbarList3 taskbar)
            {
                return;
            }

            taskbar.SetProgressValue(hwnd, completed, total);
            taskbar.SetProgressState(hwnd, (TaskbarProgressFlag)state);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (!_updateFailureLogged)
                {
                    _updateFailureLogged = true;
                    WriteUpdateFailure("Не удалось обновить прогресс панели задач", ex, "TASKBAR_UPDATE_FAILED");
                }
            }
        }
    }

    private void MarkUnavailable(string message, Exception exception, string errorCode)
    {
        // Вызывается под _gate.
        _unavailable = true;
        if (!_initFailureLogged)
        {
            _initFailureLogged = true;
            WriteInitFailure(message, exception, errorCode);
        }
    }

    private void WriteInitFailure(string message, Exception exception, string errorCode)
    {
        try
        {
            _logService.Write(
                TaskbarInitFailedEventId,
                LogLevel.Warning,
                LogStatus.Failed,
                message,
                exception,
                SourceName,
                properties: LogProps
                    .Create("ErrorCode", errorCode)
                    .With("Stage", "taskbar_progress"));
        }
        catch (Exception)
        {
            // Журнал недоступен: индикатор просто остаётся выключенным.
        }
    }

    private void WriteUpdateFailure(string message, Exception exception, string errorCode)
    {
        try
        {
            _logService.Write(
                TaskbarUpdateFailedEventId,
                LogLevel.Warning,
                LogStatus.Failed,
                message,
                exception,
                SourceName,
                properties: LogProps
                    .Create("ErrorCode", errorCode)
                    .With("Stage", "taskbar_progress"));
        }
        catch (Exception)
        {
            // Журнал недоступен: индикатор просто остаётся выключенным.
        }
    }

    private enum TaskbarState
    {
        NoProgress = 0,
        Normal = 2,
        Error = 4,
    }

    private enum TaskbarProgressFlag
    {
        NoProgress = 0,
        Indeterminate = 1,
        Normal = 2,
        Error = 4,
        Paused = 8,
    }

    [ComImport]
    [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class CTaskbarList
    {
    }

    [ComImport]
    [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);

        void MarkFullscreenWindow(
            IntPtr hwnd,
            [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);

        void SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
        void SetProgressState(IntPtr hwnd, TaskbarProgressFlag tbpFlags);
    }
}
