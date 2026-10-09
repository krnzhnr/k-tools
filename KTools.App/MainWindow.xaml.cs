using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using CommunityToolkit.Mvvm.Messaging;

using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Services.Contracts;
using KTools_App.ViewModels;

using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

using Windows.Graphics;

namespace KTools_App;

/// <summary>
/// Главное окно приложения. Настраивает габариты 800x960 и управляет
/// отображением, предотвращая уменьшение окна меньше размеров по умолчанию.
/// Все комментарии и описание выполнены строго на русском языке.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const string SourceName = nameof(MainWindow);
    private const int WM_GETMINMAXINFO = 0x0024;
    private const int WM_NCHITTEST = 0x0084;
    private const int HTSYSMENU = 3;
    private const int HTCAPTION = 2;
    private static readonly TimeSpan ShutdownWaitTimeout = TimeSpan.FromSeconds(10);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    private delegate IntPtr SubclassProc(
        IntPtr hWnd,
        uint uMsg,
        IntPtr wParam,
        IntPtr lParam,
        uint uIdSubclass,
        IntPtr dwRefData);

    [DllImport(
        "comctl32.dll",
        CharSet = CharSet.Auto,
        EntryPoint = "SetWindowSubclass",
        ExactSpelling = true)]
    private static extern bool SetWindowSubclass(
        IntPtr hWnd,
        SubclassProc subclassProc,
        uint uIdSubclass,
        IntPtr dwRefData);

    [DllImport(
        "comctl32.dll",
        CharSet = CharSet.Auto,
        EntryPoint = "DefSubclassProc",
        ExactSpelling = true)]
    private static extern IntPtr DefSubclassProc(
        IntPtr hWnd,
        uint uMsg,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(
        string lpszFile,
        int nIconIndex,
        int cxIconSize,
        int cyIconSize,
        IntPtr[] phicon,
        uint[] piconid,
        uint nIcons,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private const int WM_SETICON = 0x0080;
    private const int ICON_SMALL = 0;
    private const int ICON_BIG = 1;

    private SubclassProc? _subclassProcDelegate;
    private readonly ILogService _logService;
    private readonly ISettingsManager _settingsManager;
    private readonly IDependencyManager _dependencyManager;
    private readonly IDialogService _dialogService;
    private readonly IScriptRegistry _scriptRegistry;
    private bool _isForcedClose;
    private SizeInt32 _lastWindowSize;
    private PointInt32 _lastWindowPosition = new(WindowPlacementHelper.UnspecifiedPosition, WindowPlacementHelper.UnspecifiedPosition);
    private bool _isMaximized;
    private float _lastScaleFactor = 1f;
    private bool _hasWindowSize;

    public MainWindow(
        ILogService logService,
        ISettingsManager settingsManager,
        IDependencyManager dependencyManager,
        IDialogService dialogService,
        IScriptRegistry scriptRegistry)
    {
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
        _dependencyManager = dependencyManager ?? throw new ArgumentNullException(nameof(dependencyManager));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _scriptRegistry = scriptRegistry ?? throw new ArgumentNullException(nameof(scriptRegistry));

        try
        {
            InitializeComponent();

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            // Установка иконки приложения в панель задач и настройка заголовка.
            try
            {
                string baseDir = AppContext.BaseDirectory;
                string? iconIcoPath = ResolveIconPath(baseDir, "AppIcon.ico");

                // Иконка окна для AppWindow (системная иконка Alt+Tab)
                if (iconIcoPath != null)
                {
                    AppWindow.SetIcon(iconIcoPath);

                    // Иконка в кастомном TitleBar через абсолютный путь на диске,
                    // поскольку ресурсный URI /Assets/AppIcon.ico не резолвится в unpackaged-сборке.
                    try
                    {
                        AppTitleBar.IconSource = new Microsoft.UI.Xaml.Controls.ImageIconSource
                        {
                            ImageSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(
                                new Uri(iconIcoPath))
                        };
                    }
                    catch (Exception iconEx)
                    {
                        _logService.Write(
                            "window.titlebar_icon_failed",
                            LogLevel.Warning,
                            LogStatus.PartiallySucceeded,
                            "Не удалось установить иконку TitleBar",
                            iconEx,
                            "MainWindow",
                            properties: new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["Stage"] = "titlebar_icon",
                                ["ErrorCode"] = "icon-apply-failed"
                            });
                    }
                }

                // Иконка окна для панели задач и превью через Win32 API из ресурсов EXE-файла
                IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                if (hwnd != IntPtr.Zero)
                {
                    string? exePath = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(exePath))
                    {
                        IntPtr[] hiconSmall = new IntPtr[1];
                        IntPtr[] hiconBig = new IntPtr[1];
                        uint[] iconIds = new uint[1];

                        uint numSmall = PrivateExtractIcons(exePath, 0, 16, 16, hiconSmall, iconIds, 1, 0);
                        uint numBig = PrivateExtractIcons(exePath, 0, 32, 32, hiconBig, iconIds, 1, 0);

                        if (numSmall > 0 && hiconSmall[0] != IntPtr.Zero)
                        {
                            SendMessage(hwnd, WM_SETICON, new IntPtr(ICON_SMALL), hiconSmall[0]);
                        }
                        if (numBig > 0 && hiconBig[0] != IntPtr.Zero)
                        {
                            SendMessage(hwnd, WM_SETICON, new IntPtr(ICON_BIG), hiconBig[0]);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logService.Write(
                    "window.icon_failed",
                    LogLevel.Warning,
                    LogStatus.PartiallySucceeded,
                    "Не удалось установить иконку приложения",
                    ex,
                    "MainWindow",
                    properties: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["Stage"] = "window_icon",
                        ["ErrorCode"] = "icon-apply-failed"
                    });
            }

            // Определение DPI и масштабирование размеров окна
            IntPtr windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            uint dpi = GetDpiForWindow(windowHandle);
            float scaleFactor = dpi / 96.0f;

            double savedWidth = _settingsManager.GetSetting("Window", "Width", WindowPlacementHelper.DefaultWidth);
            double savedHeight = _settingsManager.GetSetting("Window", "Height", WindowPlacementHelper.DefaultHeight);
            int savedX = _settingsManager.GetSetting("Window", "X", WindowPlacementHelper.UnspecifiedPosition);
            int savedY = _settingsManager.GetSetting("Window", "Y", WindowPlacementHelper.UnspecifiedPosition);
            bool savedIsMaximized = _settingsManager.GetSetting("Window", "IsMaximized", false);

            int scaledWidth = (int)Math.Round(savedWidth * scaleFactor);
            int scaledHeight = (int)Math.Round(savedHeight * scaleFactor);

            bool positionApplied = false;
            if (WindowPlacementHelper.HasValidPosition(savedX, savedY))
            {
                try
                {
                    RectInt32 targetRect = new(savedX, savedY, scaledWidth, scaledHeight);
                    DisplayArea displayArea = DisplayArea.GetFromRect(targetRect, DisplayAreaFallback.Nearest);
                    if (displayArea != null)
                    {
                        var clamped = WindowPlacementHelper.ClampToWorkArea(
                            savedX,
                            savedY,
                            scaledWidth,
                            scaledHeight,
                            displayArea.WorkArea.X,
                            displayArea.WorkArea.Y,
                            displayArea.WorkArea.Width,
                            displayArea.WorkArea.Height);

                        AppWindow.MoveAndResize(new RectInt32(clamped.X, clamped.Y, clamped.Width, clamped.Height));
                        positionApplied = true;

                        _logService.Write(
                            "window.placement.restored",
                            LogLevel.Debug,
                            LogStatus.Succeeded,
                            $"Геометрия окна восстановлена: {clamped.Width}x{clamped.Height} по координатам ({clamped.X}, {clamped.Y})",
                            source: SourceName,
                            properties: LogProps
                                .Create("Resolution", $"{clamped.Width}x{clamped.Height} px")
                                .With("Percent", (int)dpi)
                                .With("Count", clamped.X));
                    }
                }
                catch (Exception ex)
                {
                    _logService.Write(
                        "window.placement.restore_failed",
                        LogLevel.Warning,
                        LogStatus.PartiallySucceeded,
                        "Не удалось применить сохранённые координаты окна, применяется базовый размер",
                        ex,
                        SourceName,
                        properties: LogProps
                            .Create("Stage", "window_placement")
                            .With("ErrorCode", "PLACEMENT_RESTORE_FAILED"));
                }
            }

            if (!positionApplied)
            {
                // Защита от вылезания окна за пределы рабочей области экрана при любом масштабе DPI
                try
                {
                    var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(windowHandle);
                    var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Nearest);
                    if (displayArea != null)
                    {
                        int maxWorkAreaHeight = displayArea.WorkArea.Height - 40;
                        if (scaledHeight > maxWorkAreaHeight)
                        {
                            scaledHeight = Math.Max(500, maxWorkAreaHeight);
                        }
                    }
                }
                catch { }

                _logService.Write(
                    "window.size.dpi_applied",
                    LogLevel.Debug,
                    LogStatus.Succeeded,
                    $"Размеры окна пересчитаны с учётом DPI: коэффициент {scaleFactor:F2}, итог {scaledWidth}x{scaledHeight} px",
                    source: SourceName,
                    properties: LogProps
                        .Create("Resolution", $"{scaledWidth}x{scaledHeight} px")
                        .With("Percent", (int)dpi)
                        .With("Count", savedWidth));

                AppWindow.Resize(new SizeInt32(scaledWidth, scaledHeight));
            }

            CaptureWindowPlacement();

            if (savedIsMaximized && AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.Maximize();
                _isMaximized = true;
                _logService.Write(
                    "window.state.maximized",
                    LogLevel.Debug,
                    LogStatus.Succeeded,
                    "Окно переведено в развёрнутое состояние согласно сохранённым настройкам",
                    source: SourceName,
                    properties: LogProps
                        .Create("Stage", "window_state")
                        .With("Mode", "Maximized"));
            }

            AppWindow.Changed += OnAppWindowChanged;

            // Навигация по умолчанию на главную страницу
            RootFrame.Navigate(typeof(MainPage));

            // Подключение subclass-процедуры для ограничения минимального размера
            // С защитой от ошибок при P/Invoke
            try
            {
                IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                if (hwnd != IntPtr.Zero)
                {
                    _subclassProcDelegate = new SubclassProc(WindowSubclassProc);
                    bool result = SetWindowSubclass(hwnd, _subclassProcDelegate, 1, IntPtr.Zero);
                    if (!result)
                    {
                        _logService.Write(
                            "window.subclass.failed",
                            LogLevel.Warning,
                            LogStatus.PartiallySucceeded,
                            "Обработчик изменения размеров окна не зарегистрирован, динамический пересчёт размеров недоступен",
                            source: SourceName,
                            properties: LogProps
                                .Create("ErrorCode", "WINDOW_SUBCLASS_FAILED")
                                .With("Control", "WindowSubclass"));
                    }
                }
            }
            catch (Exception ex)
            {
                // Если SetWindowSubclass не работает, логируем, но не падаем
                _logService.Write(
                    "window.subclass.failed",
                    LogLevel.Warning,
                    LogStatus.PartiallySucceeded,
                    "Не удалось установить обработчик минимального размера окна, будет использован размер по умолчанию",
                    ex,
                    SourceName,
                    properties: LogProps
                        .Create("Stage", "window_subclass")
                        .With("Control", "WindowSubclass")
                        .With("ErrorCode", "WINDOW_SUBCLASS_UNHANDLED"));
            }

            // Применение сохраненной темы при запуске
            ApplySavedTheme();

            // Применение сохраненного типа фона при запуске
            ApplySavedBackdrop();

            // Регистрация на получение сообщения об изменении темы для мгновенного применения
            WeakReferenceMessenger.Default.Register<ThemeChangedMessage>(this, (r, m) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (Content is FrameworkElement rootElement)
                    {
                        rootElement.RequestedTheme = m.NewTheme.ToLowerInvariant() switch
                        {
                            "light" => ElementTheme.Light,
                            "dark" => ElementTheme.Dark,
                            _ => ElementTheme.Default
                        };
                    }
                });
            });

            // Регистрация на получение сообщения об изменении типа фона
            WeakReferenceMessenger.Default.Register<BackdropChangedMessage>(this, (r, m) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    ApplyBackdrop(m.NewBackdrop);
                });
            });

            // Перехват закрытия окна: если идут активные операции скачивания или обработки файлов,
            // запрашиваем подтверждение пользователя через модальный диалог.
            AppWindow.Closing += async (sender, args) =>
            {
                CaptureWindowSize();
                if (_isForcedClose) return;

                bool isDownloading = _dependencyManager.HasActiveOperations;
                bool isProcessing = ActiveProcessTracker.HasActiveProcesses || _scriptRegistry.Scripts.Any(s => s.IsProcessing);

                if (isDownloading || isProcessing)
                {
                    args.Cancel = true;

                    _logService.Write(
                        "app.shutdown.blocked",
                        LogLevel.Warning,
                        LogStatus.Skipped,
                        "Закрытие приложения запрошено во время активных операций, запрошено подтверждение пользователя",
                        source: SourceName,
                        properties: LogProps
                            .Create("Succeeded", false)
                            .With("Cancelled", false)
                            .With("ErrorCode", "SHUTDOWN_BLOCKED_BY_ACTIVE_OPERATIONS")
                            .With("CleanupState", "NotStarted"));

                    string reason = isDownloading && isProcessing
                        ? "В данный момент выполняется загрузка/распаковка компонентов и активная обработка файлов."
                        : isDownloading
                            ? "В данный момент выполняется загрузка или распаковка компонентов зависимостей."
                            : "В данный момент выполняется активная обработка медиафайлов.";

                    bool shouldExit = await _dialogService.ShowConfirmationAsync(
                        "Подтверждение выхода",
                        $"{reason} Принудительное закрытие приложения прервёт текущие процессы и может привести к повреждению файлов.\n\nВы действительно хотите прервать работу и выйти?",
                        "Выйти",
                        "Остаться");

                    if (shouldExit)
                    {
                        _logService.Write(
                            "app.shutdown.forced_confirmed",
                            LogLevel.Warning,
                            LogStatus.Running,
                            "Пользователь подтвердил принудительное закрытие приложения во время активных операций",
                            source: SourceName,
                            properties: LogProps
                                .Create("Succeeded", true)
                                .With("ErrorCode", "FORCED_CLOSE_CONFIRMED")
                                .With("CleanupState", "Pending"));
                        _isForcedClose = true;
                        Close();
                    }
                    else
                    {
                        _logService.Write(
                            "app.shutdown.cancelled",
                            LogLevel.Info,
                            LogStatus.Cancelled,
                            "Пользователь отменил закрытие приложения, фоновые операции продолжаются",
                            source: SourceName,
                            properties: LogProps
                                .Create("Cancelled", true)
                                .With("Reason", "UserRequested")
                                .With("CleanupState", "NotStarted"));
                    }
                }
            };

            Closed += async (_, _) =>
            {
                try
                {
                    AppWindow.Changed -= OnAppWindowChanged;
                }
                catch { }

                await CloseAndShutdownAsync();
            };
        }
        catch (Exception ex)
        {
            _logService.Write(
                "window.init.failed",
                LogLevel.Fatal,
                LogStatus.Failed,
                "Главное окно не инициализировано, продолжение работы приложения невозможно",
                ex,
                SourceName,
                properties: LogProps
                    .Create("ErrorCode", "WINDOW_INIT_FAILED")
                    .With("CleanupState", "NotStarted"));
            throw;
        }

        App.RegisterShutdownSettingsStage(PersistWindowSettings);
    }

    public static MainWindowShutdownResult EvaluateShutdown(
        SettingsPersistenceResult settings,
        ProcessTerminationSummary processes,
        bool flushSucceeded,
        bool watcherStopped)
    {
        return MainWindowShutdownPolicy.Evaluate(settings, processes, flushSucceeded, watcherStopped);
    }

    public static bool CanReportShutdownSuccess(
        SettingsPersistenceResult settings,
        ProcessTerminationSummary processes,
        bool flushSucceeded,
        bool watcherStopped)
    {
        return EvaluateShutdown(settings, processes, flushSucceeded, watcherStopped).Succeeded;
    }

    private void CloseAndShutdown()
    {
        _ = CloseAndShutdownAsync();
    }

    private async Task CloseAndShutdownAsync()
    {
        bool completed = await App.TryBeginControlledShutdownAsync(
            App.CrashShutdownReasonWindowClosed,
            ShutdownWaitTimeout);
        if (completed)
        {
            return;
        }

        _logService.Write(
            "app.shutdown.not_completed",
            LogLevel.Error,
            LogStatus.Failed,
            "Контролируемое завершение не завершилось до закрытия окна",
            null,
            SourceName,
            properties: LogProps
                .Create("Stage", "controlled_shutdown")
                .With("Succeeded", false)
                .With("Failed", true)
                .With("ErrorCode", "shutdown-not-completed"));
    }

    private SettingsPersistenceResult PersistWindowSettings()
    {
        try
        {
            CaptureWindowPlacement();

            SizeInt32 size = ReadWindowSize();
            PointInt32 position = ReadWindowPosition();
            float scaleFactor = _hasWindowSize && _lastScaleFactor > 0f ? _lastScaleFactor : ReadWindowScaleFactor();

            _settingsManager.SetSetting("Window", "Width", size.Width / scaleFactor);
            _settingsManager.SetSetting("Window", "Height", size.Height / scaleFactor);

            if (WindowPlacementHelper.HasValidPosition(position.X, position.Y))
            {
                _settingsManager.SetSetting("Window", "X", position.X);
                _settingsManager.SetSetting("Window", "Y", position.Y);
            }

            _settingsManager.SetSetting("Window", "IsMaximized", _isMaximized);

            PersistenceResult saveResult = _settingsManager.SaveSettings();
            SettingsPersistenceResult persistence = SettingsPersistenceInvoker.ReadResult(saveResult);
            if (!persistence.Persisted)
            {
                _logService.Write(
                    "app.shutdown.settings_persistence_failed",
                    LogLevel.Error,
                    LogStatus.Failed,
                    "Настройки окна не сохранены",
                    null,
                    SourceName,
                    properties: LogProps
                        .Create("Persisted", false)
                        .With("Stage", "settings_persistence")
                        .With("ErrorCode", persistence.ErrorCode ?? "persistence-failed"));
            }

            return persistence;
        }
        catch (Exception ex)
        {
            _logService.Write(
                "app.shutdown.settings_persistence_failed",
                LogLevel.Error,
                LogStatus.Failed,
                "Сохранение настроек окна вызвало исключение",
                ex,
                SourceName,
                properties: LogProps
                    .Create("Persisted", false)
                    .With("Stage", "settings_persistence")
                    .With("ErrorCode", "save-exception"));
            return SettingsPersistenceResult.Failure("save-exception");
        }
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        CaptureWindowPlacement();
    }

    private void CaptureWindowPlacement()
    {
        try
        {
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                OverlappedPresenterState state = presenter.State;
                _isMaximized = WindowPlacementHelper.ResolveIsMaximized(state, _isMaximized);

                if (WindowPlacementHelper.ShouldUpdateNormalBounds(state))
                {
                    _lastWindowSize = AppWindow.Size;
                    _lastWindowPosition = AppWindow.Position;
                    _lastScaleFactor = ReadWindowScaleFactor();
                    _hasWindowSize = true;
                }
            }
            else
            {
                _lastWindowSize = AppWindow.Size;
                _lastWindowPosition = AppWindow.Position;
                _lastScaleFactor = ReadWindowScaleFactor();
                _hasWindowSize = true;
            }
        }
        catch (Exception)
        {
        }
    }

    private void CaptureWindowSize()
    {
        CaptureWindowPlacement();
    }

    private SizeInt32 ReadWindowSize()
    {
        if (_hasWindowSize)
        {
            return _lastWindowSize;
        }

        try
        {
            return AppWindow.Size;
        }
        catch
        {
            return _lastWindowSize;
        }
    }

    private PointInt32 ReadWindowPosition()
    {
        if (WindowPlacementHelper.HasValidPosition(_lastWindowPosition.X, _lastWindowPosition.Y))
        {
            return _lastWindowPosition;
        }

        try
        {
            return AppWindow.Position;
        }
        catch
        {
            return _lastWindowPosition;
        }
    }

    private float ReadWindowScaleFactor()
    {
        try
        {
            IntPtr windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            uint dpi = windowHandle == IntPtr.Zero ? 0u : GetDpiForWindow(windowHandle);
            return dpi == 0 ? 1f : dpi / 96.0f;
        }
        catch (Exception)
        {
            return 1f;
        }
    }

    private void ApplySavedTheme()
    {
        try
        {
            string theme = _settingsManager.Theme;
            if (Content is FrameworkElement rootElement)
            {
                rootElement.RequestedTheme = theme.ToLowerInvariant() switch
                {
                    "light" => ElementTheme.Light,
                    "dark" => ElementTheme.Dark,
                    _ => ElementTheme.Default
                };
                _logService.Write(
                    "window.theme_applied",
                    LogLevel.Debug,
                    LogStatus.Succeeded,
                    $"Сохранённая тема оформления применена: '{LogRedactor.CompactSafeToken(theme)}'",
                    source: SourceName,
                    properties: LogProps.Create("Theme", LogRedactor.CompactSafeToken(theme)));
            }
        }
        catch (Exception ex)
        {
            _logService.Write(
                "window.theme_failed",
                LogLevel.Warning,
                LogStatus.PartiallySucceeded,
                "Не удалось применить сохранённую тему оформления при старте, применена тема по умолчанию",
                ex,
                "MainWindow",
                properties: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Stage"] = "theme",
                    ["ErrorCode"] = "theme-apply-failed"
                });
        }
    }

    /// <summary>
    /// Считывает сохраненный тип фона из SettingsManager и применяет его к окну.
    /// </summary>
    private void ApplySavedBackdrop()
    {
        try
        {
            string backdrop = _settingsManager.BackdropType;
            ApplyBackdrop(backdrop);
        }
        catch (Exception ex)
        {
            _logService.Write(
                "window.backdrop_failed",
                LogLevel.Warning,
                LogStatus.PartiallySucceeded,
                "Не удалось применить сохранённый тип фона при старте",
                ex,
                "MainWindow",
                properties: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Stage"] = "backdrop",
                    ["ErrorCode"] = "backdrop-apply-failed"
                });
        }
    }

    /// <summary>
    /// Применяет выбранный тип фона (Mica или Acrylic) к окну приложения.
    /// </summary>
    private void ApplyBackdrop(string backdropType)
    {
        try
        {
            if (backdropType.Equals("Acrylic", StringComparison.OrdinalIgnoreCase))
            {
                SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
            }
            else
            {
                SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
            }
            _logService.Write(
                "window.backdrop_applied",
                LogLevel.Debug,
                LogStatus.Succeeded,
                $"Тип фона окна применён: '{LogRedactor.CompactSafeToken(backdropType)}'",
                source: SourceName,
                properties: LogProps.Create("Theme", LogRedactor.CompactSafeToken(backdropType)));
        }
        catch (Exception ex)
        {
            _logService.Write(
                "window.backdrop_failed",
                LogLevel.Warning,
                LogStatus.PartiallySucceeded,
                $"Не удалось применить тип фона окна '{backdropType}'",
                ex,
                "MainWindow",
                properties: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Stage"] = "backdrop",
                    ["ErrorCode"] = "backdrop-apply-failed"
                });
        }
    }

    /// <summary>
    /// Переопределенная процедура окна для перехвата сообщений Win32.
    /// Перехватывает WM_GETMINMAXINFO для ограничения минимальных размеров.
    /// </summary>
    private IntPtr WindowSubclassProc(
        IntPtr hWnd,
        uint uMsg,
        IntPtr wParam,
        IntPtr lParam,
        uint uIdSubclass,
        IntPtr dwRefData)
    {
        if (uMsg == WM_NCHITTEST)
        {
            IntPtr result = DefSubclassProc(hWnd, uMsg, wParam, lParam);
            if (result.ToInt32() == HTSYSMENU)
            {
                return new IntPtr(HTCAPTION);
            }
            return result;
        }

        if (uMsg == WM_GETMINMAXINFO)
        {
            try
            {
                MINMAXINFO minMax = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                // Ограничиваем минимальную ширину и высоту размером по умолчанию с учетом DPI монитора
                uint dpi = GetDpiForWindow(hWnd);
                float scaleFactor = dpi / 96.0f;

                // Сохраняем ширину 800 DIP для идеальной сетки 2 колонок на Home странице
                minMax.ptMinTrackSize.x = (int)Math.Round(800 * scaleFactor);

                // Минимальная высота: 620 DIP с защитой от вылезания за пределы экрана
                int minHeight = (int)Math.Round(620 * scaleFactor);
                try
                {
                    var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
                    var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(windowId, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
                    if (displayArea != null)
                    {
                        int maxWorkHeight = displayArea.WorkArea.Height - 40;
                        if (minHeight > maxWorkHeight)
                        {
                            minHeight = Math.Max(480, maxWorkHeight);
                        }
                    }
                }
                catch { }

                minMax.ptMinTrackSize.y = minHeight;

                Marshal.StructureToPtr(minMax, lParam, false);
                return IntPtr.Zero; // Сообщение обработано
            }
            catch (Exception ex)
            {
                _logService.Write(
                    "window.min_max_info_failed",
                    LogLevel.Warning,
                    LogStatus.Failed,
                    "Ограничения размера окна с учётом DPI не рассчитаны, применены значения по умолчанию",
                    ex,
                    SourceName,
                    properties: LogProps
                        .Create("ErrorCode", "WINDOW_MINMAXINFO_FAILED")
                        .With("Control", "WM_GETMINMAXINFO"));
            }
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    /// <summary>
    /// Ищет файл иконки сначала в корне каталога приложения (установленная версия),
    /// затем в подпапке Assets (dev-сборка). Возвращает абсолютный путь или null.
    /// </summary>
    private static string? ResolveIconPath(string baseDirectory, string fileName)
    {
        // Установленная версия: иконка в корне (H:\K-Tools\AppIcon.ico)
        string rootPath = System.IO.Path.Combine(baseDirectory, fileName);
        if (System.IO.File.Exists(rootPath))
        {
            return rootPath;
        }

        // Dev-сборка: иконка в подпапке Assets (bin\...\Assets\AppIcon.ico)
        string assetsPath = System.IO.Path.Combine(baseDirectory, "Assets", fileName);
        if (System.IO.File.Exists(assetsPath))
        {
            return assetsPath;
        }

        return null;
    }
}
