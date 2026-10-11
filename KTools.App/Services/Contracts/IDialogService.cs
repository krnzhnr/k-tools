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

    /// <summary>
    /// <summary>
    /// Отображает диалог ввода общего заголовка для добавляемых аудиодорожек
    /// со списком заготовленных заголовков для мгновенного применения.
    /// Список заготовок редактируется прямо в диалоге (удаление крестиком, добавление).
    /// Возвращает введённый заголовок (пустая строка — без заголовка)
    /// или null, если ввод отменён пользователем.
    /// </summary>
    /// <param name="audioFileNames">Имена добавляемых аудиофайлов для контекста пользователя.</param>
    /// <param name="defaultTitle">Начальное значение поля заголовка.</param>
    /// <param name="presets">Живой список заготовок (изменения применяются сразу).</param>
    Task<string?> AskAudioTitleAsync(
        System.Collections.Generic.IReadOnlyList<string> audioFileNames,
        string defaultTitle,
        System.Collections.Generic.IList<string> presets);

    /// <summary>
    /// Отображает диалог переименования заголовка аудиодорожек со списком заготовок.
    /// Возвращает новый заголовок (пустая строка — очистить) или null при отмене.
    /// </summary>
    /// <param name="oldTitle">Текущий заголовок для контекста пользователя.</param>
    /// <param name="affectedCount">Число дорожек с таким заголовком.</param>
    /// <param name="presets">Живой список заготовок (изменения применяются сразу).</param>
    Task<string?> RenameAudioTitleAsync(
        string oldTitle,
        int affectedCount,
        System.Collections.Generic.IList<string> presets);
}

