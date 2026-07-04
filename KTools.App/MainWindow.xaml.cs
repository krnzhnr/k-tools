using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using KTools_App.Services.Contracts;
using Windows.Graphics;
using CommunityToolkit.Mvvm.Messaging;
using KTools_App.Core;
using KTools_App.ViewModels;

namespace KTools_App;

/// <summary>
/// Главное окно приложения. Настраивает габариты 800x960 и управляет
/// отображением, предотвращая уменьшение окна меньше размеров по умолчанию.
/// Все комментарии и описание выполнены строго на русском языке.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const int WM_GETMINMAXINFO = 0x0024;

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

    private SubclassProc? _subclassProcDelegate;
    private readonly ILogService _logService;
    private readonly ISettingsManager _settingsManager;

    public MainWindow(ILogService logService, ISettingsManager settingsManager)
    {
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));

        try
        {
            InitializeComponent();

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            // Установка иконки приложения
            try
            {
                AppWindow.SetIcon("Assets/AppIcon.ico");
            }
            catch (Exception ex)
            {
                _logService.Warn(
                    $"Не удалось установить иконку приложения: {ex.Message}",
                    "MainWindow");
            }

            // Установка точного размера окна (800 x 960 пикселей)
            AppWindow.Resize(new SizeInt32(800, 960));

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
                        _logService.Warn(
                            "SetWindowSubclass вернул false - обработчик размера не установлен.",
                            "MainWindow");
                    }
                }
            }
            catch (Exception ex)
            {
                // Если SetWindowSubclass не работает, логируем, но не падаем
                _logService.Warn(
                    $"Не удалось установить обработчик минимального размера окна: {ex.Message}. " +
                    "Будет использован размер по умолчанию.",
                    "MainWindow");
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
                        rootElement.RequestedTheme = m.NewTheme.Equals("Light", StringComparison.OrdinalIgnoreCase)
                            ? ElementTheme.Light
                            : ElementTheme.Dark;
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

            // Подписка на событие закрытия окна для гарантированного завершения процесса приложения
            Closed += (sender, args) =>
            {
                _logService.Info(
                    "Главное окно закрыто пользователем. Запуск процедуры полного завершения процесса приложения.", 
                    "MainWindow");
                
                try
                {
                    Application.Current.Exit();
                    _logService.Info(
                        "Запрос на выход из приложения успешно отправлен через Application.Current.Exit().", 
                        "MainWindow");
                }
                catch (Exception ex)
                {
                    _logService.Exception(
                        ex, 
                        "Возникло исключение при попытке принудительного завершения работы приложения.", 
                        "MainWindow");
                }
            };
        }
        catch (Exception ex)
        {
            _logService.Exception(
                ex,
                "Критическая ошибка при инициализации главного окна.",
                "MainWindow");
            throw;
        }
    }

    /// <summary>
    /// Считывает сохраненную тему оформления из SettingsManager и применяет её к корневому контейнеру окна.
    /// </summary>
    private void ApplySavedTheme()
    {
        try
        {
            string theme = _settingsManager.Theme;
            if (Content is FrameworkElement rootElement)
            {
                rootElement.RequestedTheme = theme.Equals("Light", StringComparison.OrdinalIgnoreCase)
                    ? ElementTheme.Light
                    : ElementTheme.Dark;
                _logService.Info($"Успешно применена сохраненная тема оформления: '{theme}'", "MainWindow");
            }
        }
        catch (Exception ex)
        {
            _logService.Error($"Не удалось применить сохраненную тему оформления при старте: {ex.Message}", "MainWindow");
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
            _logService.Error($"Не удалось применить сохраненный тип фона при старте: {ex.Message}", "MainWindow");
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
            _logService.Info($"Успешно применен тип фона окна: '{backdropType}'", "MainWindow");
        }
        catch (Exception ex)
        {
            _logService.Error($"Не удалось применить тип фона окна '{backdropType}': {ex.Message}", "MainWindow");
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
        if (uMsg == WM_GETMINMAXINFO)
        {
            try
            {
                MINMAXINFO minMax = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                // Ограничиваем минимальную ширину и высоту размером по умолчанию
                minMax.ptMinTrackSize.x = 800;
                minMax.ptMinTrackSize.y = 960;
                Marshal.StructureToPtr(minMax, lParam, false);
                return IntPtr.Zero; // Сообщение обработано
            }
            catch (Exception ex)
            {
                _logService.Exception(
                    ex,
                    "Ошибка при обработке сообщения WM_GETMINMAXINFO",
                    "MainWindow");
            }
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }
}
