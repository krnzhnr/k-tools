// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using KTools_App.Core;
using KTools_App.Services.Contracts;
using KTools_App.UI.Controls;

namespace KTools_App.ViewModels;

/// <summary>
/// Модель представления для управления выбором дорожек и вложений.
/// Все комментарии и логирование выполнены исключительно на русском языке.
/// </summary>
public sealed partial class TrackSelectionViewModel : ThreadSafeViewModel
{
    private readonly ILogService _logService;

    [ObservableProperty]
    private AbstractScript? _activeScript;

    [ObservableProperty]
    private ObservableCollection<FileQueueItem>? _files;

    [ObservableProperty]
    private int _videoCount;

    [ObservableProperty]
    private int _audioCount;

    [ObservableProperty]
    private int _subtitleCount;

    [ObservableProperty]
    private int _attachmentCount;

    // Структуры для хранения уникальных технических параметров (опций) для фильтрации
    public Dictionary<string, Dictionary<string, HashSet<string>>> DynamicOptions { get; } = new()
    {
        { "video", new() { { "language", new() }, { "codec", new() }, { "resolution", new() }, { "name", new() } } },
        { "audio", new() { { "language", new() }, { "codec", new() }, { "channels", new() }, { "name", new() } } },
        { "subtitles", new() { { "language", new() }, { "codec", new() }, { "name", new() } } },
        { "attachments", new() { { "extension", new() }, { "name", new() } } }
    };

    // Выбранные в данный момент правила фильтрации по категориям и свойствам
    public Dictionary<string, Dictionary<string, HashSet<string>>> ActiveRules { get; } = new()
    {
        { "video", new() { { "language", new() }, { "codec", new() }, { "resolution", new() }, { "name", new() } } },
        { "audio", new() { { "language", new() }, { "codec", new() }, { "channels", new() }, { "name", new() } } },
        { "subtitles", new() { { "language", new() }, { "codec", new() }, { "name", new() } } },
        { "attachments", new() { { "extension", new() }, { "name", new() } } }
    };

    public ISettingsManager SettingsManager { get; }

    public TrackSelectionViewModel(ILogService logService, ISettingsManager settingsManager)
    {
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        SettingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
    }

    /// <summary>
    /// Собирает уникальные свойства для фильтрации на основе текущей очереди файлов.
    /// </summary>
    public void CollectDynamicOptions()
    {
        try
        {
            foreach (var cat in DynamicOptions.Values)
            {
                foreach (var prop in cat.Values)
                {
                    prop.Clear();
                }
            }

            if (Files == null) return;

            foreach (var fileItem in Files)
            {
                if (fileItem.MediaInfo == null) continue;

                var structure = fileItem.MediaInfo;

                // Видео
                foreach (var track in structure.GetVideoTracks())
                {
                    string lang = !string.IsNullOrEmpty(track.Language) && track.Language != "und" ? track.Language.ToUpperInvariant() : "Неизвестный";
                    string codec = !string.IsNullOrEmpty(track.Codec) ? track.Codec.ToUpperInvariant() : "Неизвестный";
                    
                    DynamicOptions["video"]["language"].Add(lang);
                    DynamicOptions["video"]["codec"].Add(codec);
                    if (!string.IsNullOrEmpty(track.Resolution))
                    {
                        DynamicOptions["video"]["resolution"].Add(track.Resolution);
                    }
                    if (!string.IsNullOrEmpty(track.Name))
                    {
                        DynamicOptions["video"]["name"].Add(track.Name);
                    }
                }

                // Аудио
                foreach (var track in structure.GetAudioTracks())
                {
                    string lang = !string.IsNullOrEmpty(track.Language) && track.Language != "und" ? track.Language.ToUpperInvariant() : "Неизвестный";
                    string codec = !string.IsNullOrEmpty(track.Codec) ? track.Codec.ToUpperInvariant() : "Неизвестный";
                    string ch = track.Channels > 0 ? $"{track.Channels} ch" : "Неизвестно";

                    DynamicOptions["audio"]["language"].Add(lang);
                    DynamicOptions["audio"]["codec"].Add(codec);
                    DynamicOptions["audio"]["channels"].Add(ch);
                    if (!string.IsNullOrEmpty(track.Name))
                    {
                        DynamicOptions["audio"]["name"].Add(track.Name);
                    }
                }

                // Субтитры
                foreach (var track in structure.GetSubtitleTracks())
                {
                    string lang = !string.IsNullOrEmpty(track.Language) && track.Language != "und" ? track.Language.ToUpperInvariant() : "Неизвестный";
                    string codec = !string.IsNullOrEmpty(track.Codec) ? track.Codec.ToUpperInvariant() : "Неизвестный";

                    DynamicOptions["subtitles"]["language"].Add(lang);
                    DynamicOptions["subtitles"]["codec"].Add(codec);
                    if (!string.IsNullOrEmpty(track.Name))
                    {
                        DynamicOptions["subtitles"]["name"].Add(track.Name);
                    }
                }

                // Вложения
                foreach (var font in structure.GetFontAttachments())
                {
                    string ext = Path.GetExtension(font.FileName).ToLowerInvariant();
                    string name = Path.GetFileNameWithoutExtension(font.FileName);

                    DynamicOptions["attachments"]["extension"].Add(ext);
                    if (!string.IsNullOrEmpty(name))
                    {
                        DynamicOptions["attachments"]["name"].Add(name);
                    }
                }
            }

            // Автоматически очищаем устаревшие правила фильтров, которых нет в новом наборе файлов
            PruneObsoleteRules();

            _logService.Info("Сбор уникальных свойств медиадорожек для фильтрации успешно завершен во ViewModel", "TrackSelectionViewModel");
        }
        catch (Exception ex)
        {
            _logService.Exception(ex, "Ошибка при сборе уникальных свойств дорожек для фильтрации во ViewModel", "TrackSelectionViewModel");
        }
    }

    /// <summary>
    /// Очищает все правила фильтрации для указанной категории.
    /// </summary>
    public void ClearRules(string category)
    {
        if (ActiveRules.TryGetValue(category, out var rules))
        {
            foreach (var prop in rules.Values)
            {
                prop.Clear();
            }
        }
    }

    /// <summary>
    /// Полностью очищает все правила фильтрации по всем категориям.
    /// </summary>
    public void ClearAllRules()
    {
        foreach (var cat in ActiveRules.Values)
        {
            foreach (var prop in cat.Values)
            {
                prop.Clear();
            }
        }
    }

    /// <summary>
    /// Автоматически удаляет устаревшие правила фильтрации, значения которых отсутствуют в текущем наборе DynamicOptions.
    /// Предотвращает появление фантомных бейджей ("единичек") и зависание неактуальных фильтров при смене или партионной замене файлов.
    /// </summary>
    public void PruneObsoleteRules()
    {
        foreach (var catKvp in ActiveRules)
        {
            string category = catKvp.Key;
            var catRules = catKvp.Value;

            if (!DynamicOptions.TryGetValue(category, out var catOptions))
            {
                foreach (var propSet in catRules.Values)
                {
                    propSet.Clear();
                }
                continue;
            }

            foreach (var propKvp in catRules)
            {
                string propKey = propKvp.Key;
                var selectedValues = propKvp.Value;

                if (!catOptions.TryGetValue(propKey, out var availableValues) || availableValues.Count == 0)
                {
                    selectedValues.Clear();
                    continue;
                }

                // Удаляем значения правил, которых больше нет среди текущих медиафайлов
                selectedValues.RemoveWhere(val => !availableValues.Contains(val));
            }
        }
    }

    /// <summary>
    /// Проверяет, соответствует ли узел хотя бы одному активному правилу фильтрации.
    /// </summary>
    public bool MatchesFilterRules(TrackNodeItem item)
    {
        string category = string.Empty;
        if (item.IsFont)
        {
            category = "attachments";
        }
        else if (item.Track != null)
        {
            category = item.Track.TrackType.ToLowerInvariant();
        }

        if (string.IsNullOrEmpty(category) || !ActiveRules.TryGetValue(category, out var rules))
        {
            return false;
        }

        bool hasAnyActiveRule = rules.Values.Any(r => r.Count > 0);
        if (!hasAnyActiveRule)
        {
            return false;
        }

        foreach (var ruleKvp in rules)
        {
            string propKey = ruleKvp.Key;
            var selectedVals = ruleKvp.Value;

            if (selectedVals.Count == 0) continue;

            string trackVal = string.Empty;
            if (item.IsFont && item.Attachment != null)
            {
                if (propKey == "extension")
                {
                    trackVal = Path.GetExtension(item.Attachment.FileName).ToLowerInvariant();
                }
                else if (propKey == "name")
                {
                    trackVal = Path.GetFileNameWithoutExtension(item.Attachment.FileName);
                }
            }
            else if (item.Track != null)
            {
                var t = item.Track;
                if (propKey == "language")
                {
                    trackVal = !string.IsNullOrEmpty(t.Language) && t.Language != "und" ? t.Language.ToUpperInvariant() : "Неизвестный";
                }
                else if (propKey == "codec")
                {
                    trackVal = !string.IsNullOrEmpty(t.Codec) ? t.Codec.ToUpperInvariant() : "Неизвестный";
                }
                else if (propKey == "resolution")
                {
                    trackVal = t.Resolution;
                }
                else if (propKey == "channels")
                {
                    trackVal = t.Channels > 0 ? $"{t.Channels} ch" : string.Empty;
                }
                else if (propKey == "name")
                {
                    trackVal = t.Name;
                }
            }

            if (selectedVals.Contains(trackVal))
            {
                return true;
            }
        }

        return false;
    }
}
