// -*- coding: utf-8 -*-
using System.Threading.Tasks;

namespace KTools_App.Services.Contracts;

/// <summary>
/// Абстракция службы диалоговых окон для отображения информационных сообщений и подтверждений.
/// Изолирует ViewModels от прямых зависимостей на ContentDialog и XamlRoot.
/// </summary>
public interface IDialogService
{
    /// <summary>
    /// Отображает информационное сообщение с единственной кнопкой закрытия.
    /// </summary>
    Task ShowMessageAsync(string title, string content);

    /// <summary>
    /// Отображает диалог подтверждения с кнопками подтверждения и отмены.
    /// Возвращает true, если пользователь подтвердил действие.
    /// </summary>
    Task<bool> ShowConfirmationAsync(
        string title,
        string content,
        string confirmText = "ОК",
        string cancelText = "Отмена");
}
