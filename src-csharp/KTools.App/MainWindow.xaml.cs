using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using KTools_App.Core;

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

    public MainWindow()
    {
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
                LogService.Instance.Warn(
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
                        LogService.Instance.Warn(
                            "SetWindowSubclass вернул false - обработчик размера не установлен.",
                            "MainWindow");
                    }
                }
            }
            catch (Exception ex)
            {
                // Если SetWindowSubclass не работает, логируем, но не падаем
                LogService.Instance.Warn(
                    $"Не удалось установить обработчик минимального размера окна: {ex.Message}. " +
                    "Будет использован размер по умолчанию.",
                    "MainWindow");
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Exception(
                ex,
                "Критическая ошибка при инициализации главного окна.",
                "MainWindow");
            throw;
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
                Core.LogService.Instance.Exception(
                    ex,
                    "Ошибка при обработке сообщения WM_GETMINMAXINFO",
                    "MainWindow");
            }
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }
}
