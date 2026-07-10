// -*- coding: utf-8 -*-
using System;
using System.Runtime.InteropServices;
using KTools_App.Services.Contracts;
using Microsoft.UI.Xaml;
using KTools_App.Core;
using KTools_App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace KTools_App.UI.Pages;

/// <summary>
/// Окно-контейнер для отображения страницы предпросмотра субтитров.
/// </summary>
public sealed class SubtitlePreviewWindow : Window
{
    private const int WM_NCHITTEST = 0x0084;
    private const int HTSYSMENU = 3;
    private const int HTCAPTION = 2;

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
    /// <summary>
    /// Инициализирует новый экземпляр класса SubtitlePreviewWindow.
    /// </summary>
    public SubtitlePreviewWindow(SubtitlePreviewViewModel viewModel)
    {
        Title = "Предпросмотр субтитров и настройка фильтров";
        
        // В качестве содержимого устанавливаем страницу,
        // чтобы избежать ошибок приведения типов XAML-компилятора к FrameworkElement.
        var page = new SubtitlePreviewPage(viewModel);
        page.OwnerWindow = this;
        Content = page;

        // Расширяем контент в область заголовка окна
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(page.TitleBarElement);

        // Применение сохраненной темы оформления
        try
        {
            string theme = App.Services.GetRequiredService<ISettingsManager>().Theme;
            if (Content is FrameworkElement rootElement)
            {
                rootElement.RequestedTheme = theme.Equals("Light", StringComparison.OrdinalIgnoreCase)
                    ? ElementTheme.Light
                    : ElementTheme.Dark;
            }
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogService>().Error($"Не удалось применить тему к окну предпросмотра: {ex.Message}", "SubtitlePreviewWindow");
        }

        // Применение эффекта фона (Mica или Acrylic)
        try
        {
            string backdrop = App.Services.GetRequiredService<ISettingsManager>().BackdropType;
            if (backdrop.Equals("Acrylic", StringComparison.OrdinalIgnoreCase))
            {
                SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
            }
            else
            {
                SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
            }
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogService>().Error($"Не удалось применить эффект фона к окну предпросмотра: {ex.Message}", "SubtitlePreviewWindow");
        }

        // Настройка размеров и центрирования окна
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        if (appWindow != null)
        {
            if (Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported())
            {
                appWindow.TitleBar.PreferredTheme = Microsoft.UI.Windowing.TitleBarTheme.UseDefaultAppMode;
                appWindow.TitleBar.IconShowOptions = Microsoft.UI.Windowing.IconShowOptions.HideIconAndSystemMenu;
            }
            appWindow.Resize(new Windows.Graphics.SizeInt32(1600, 1000));
            
            var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                windowId, 
                Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
                
            if (displayArea != null)
            {
                var screenWidth = displayArea.WorkArea.Width;
                var screenHeight = displayArea.WorkArea.Height;
                var x = (screenWidth - 1600) / 2;
                var y = (screenHeight - 1000) / 2;
                appWindow.Move(new Windows.Graphics.PointInt32(x, y));
            }
            // Подключение subclass-процедуры для отключения системного меню на иконке
            try
            {
                if (hwnd != IntPtr.Zero)
                {
                    _subclassProcDelegate = new SubclassProc(WindowSubclassProc);
                    SetWindowSubclass(hwnd, _subclassProcDelegate, 1, IntPtr.Zero);
                }
            }
            catch (Exception ex)
            {
                App.Services.GetRequiredService<ILogService>().Warn($"Не удалось установить обработчик сообщений окна предпросмотра: {ex.Message}", "SubtitlePreviewWindow");
            }
        }
    }

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

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }
}
