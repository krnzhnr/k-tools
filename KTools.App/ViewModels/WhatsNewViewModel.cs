// -*- coding: utf-8 -*-
using System;
using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;

using KTools_App.Core;

namespace KTools_App.ViewModels;

/// <summary>
/// Модель представления окна «Что нового».
/// Хранит структурированные изменения версии и имя анимации шапки.
/// </summary>
public partial class WhatsNewViewModel : ThreadSafeViewModel
{
    /// <summary>Заголовок окна (например «Что нового в 2.1.9»).</summary>
    [ObservableProperty]
    public partial string VersionTitle { get; set; } = string.Empty;

    /// <summary>Подзаголовок релиза с GitHub.</summary>
    [ObservableProperty]
    public partial string ReleaseTitle { get; set; } = string.Empty;

    /// <summary>Имя ascii.rest-анимации для шапки.</summary>
    [ObservableProperty]
    public partial string ArtPieceName { get; set; } = WhatsNewArtCatalog.DefaultPiece;

    /// <summary>Краткая выжимка версии.</summary>
    public ObservableCollection<string> Highlights { get; } = new();

    /// <summary>Полные секции изменений.</summary>
    public ObservableCollection<ReleaseNotesSection> Sections { get; } = new();

    /// <summary>
    /// Инициализирует модель данными релиза.
    /// </summary>
    public WhatsNewViewModel()
    {
    }

    /// <summary>
    /// Инициализирует модель готовыми данными релиза.
    /// </summary>
    public WhatsNewViewModel(ReleaseNotes notes)
    {
        ArgumentNullException.ThrowIfNull(notes);

        Apply(notes);
    }

    /// <summary>
    /// Заполняет модель данными релиза.
    /// </summary>
    public void Apply(ReleaseNotes notes)
    {
        ArgumentNullException.ThrowIfNull(notes);

        VersionTitle = $"Что нового в {notes.Version}";
        ReleaseTitle = notes.Title;
        ArtPieceName = notes.ArtPieceName;

        Highlights.Clear();
        foreach (string item in notes.Highlights)
        {
            Highlights.Add(item);
        }

        Sections.Clear();
        foreach (ReleaseNotesSection section in notes.Sections)
        {
            Sections.Add(section);
        }
    }
}
