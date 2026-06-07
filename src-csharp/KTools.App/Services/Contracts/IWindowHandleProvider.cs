// -*- coding: utf-8 -*-
using System;

namespace KTools_App.Services.Contracts;

/// <summary>
/// Абстракция провайдера дескриптора главного окна (HWND).
/// Используется для инициализации системных диалогов (FolderPicker, FilePicker)
/// в WinUI 3, где требуется привязка к родительскому окну через COM-интерфейс.
/// </summary>
public interface IWindowHandleProvider
{
    /// <summary>
    /// Возвращает дескриптор главного окна приложения (HWND).
    /// </summary>
    IntPtr GetMainWindowHandle();
}
