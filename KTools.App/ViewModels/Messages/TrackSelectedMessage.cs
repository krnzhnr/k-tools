// -*- coding: utf-8 -*-
using System.Collections.Generic;

namespace KTools_App.ViewModels.Messages;

/// <summary>
/// Сообщение, отправляемое при изменении выбора дорожек или вложений в UI.
/// Содержит актуальные списки выбранных элементов по каждому файлу.
/// </summary>
public sealed class TrackSelectedMessage
{
    /// <summary>
    /// Выбранные дорожки (путь к файлу -> список ID выбранных дорожек).
    /// </summary>
    public Dictionary<string, List<int>> SelectedTracks { get; }

    /// <summary>
    /// Выбранные вложения/шрифты (путь к файлу -> список ID выбранных вложений).
    /// </summary>
    public Dictionary<string, List<int>> SelectedAttachments { get; }

    /// <summary>
    /// Инициализирует новый экземпляр сообщения TrackSelectedMessage.
    /// </summary>
    public TrackSelectedMessage(
        Dictionary<string, List<int>> selectedTracks,
        Dictionary<string, List<int>> selectedAttachments)
    {
        SelectedTracks = selectedTracks;
        SelectedAttachments = selectedAttachments;
    }
}
