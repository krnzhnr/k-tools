// -*- coding: utf-8 -*-
using System;
using Microsoft.UI.Xaml;
using KTools_App.Services.Contracts;

namespace KTools_App.Services.Implementations;

/// <summary>
/// Провайдер дескриптора главного окна приложения.
/// Используется для инициализации FolderPicker/FilePicker через COM Interop в WinUI 3.
/// </summary>
public sealed class WindowHandleProvider : IWindowHandleProvider
{
    private Window? _mainWindow;

    /// <summary>
    /// Устанавливает ссылку на главное окно приложения.
    /// </summary>
    public void SetMainWindow(Window window)
    {
        _mainWindow = window;
    }

    /// <inheritdoc />
    public IntPtr GetMainWindowHandle()
    {
        if (_mainWindow == null)
        {
            throw new InvalidOperationException(
                "Главное окно приложения не инициализировано. "
                + "Вызовите SetMainWindow перед получением дескриптора.");
        }

        return WinRT.Interop.WindowNative.GetWindowHandle(
            _mainWindow);
    }
}
