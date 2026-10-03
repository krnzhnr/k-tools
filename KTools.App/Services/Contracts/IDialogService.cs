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

    /// <summary>
    /// Отображает диалоговое окно со списком доступных дорожек субтитров для выбора дорожки, вшиваемой в видеоряд.
    /// Возвращает выбранную дорожку субтитров или null, если выбор отменен пользователем или вшивание пропущено.
    /// </summary>
    /// <param name="videoFileName">Имя файла обрабатываемого видео для контекста пользователя.</param>
    /// <param name="tracks">Список доступных дорожек субтитров.</param>
    Task<KTools_App.Core.MediaTrack?> ChooseSubtitleTrackAsync(
        string videoFileName,
        System.Collections.Generic.IReadOnlyList<KTools_App.Core.MediaTrack> tracks);
}

