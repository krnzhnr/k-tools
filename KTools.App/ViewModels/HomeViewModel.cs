// -*- coding: utf-8 -*-
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using KTools_App.Core;
using KTools_App.Services.Contracts;

namespace KTools_App.ViewModels;

/// <summary>
/// Модель представления домашней страницы со списком скриптов обработки медиа.
/// Группирует скрипты по категориям для отображения в виде карточек.
/// </summary>
public partial class HomeViewModel : ObservableObject
{
    private readonly INavigationService _navigationService;

    /// <summary>
    /// Список скриптов категории «Видео».
    /// </summary>
    public List<ScriptInfo> VideoScripts { get; }

    /// <summary>
    /// Список скриптов категории «Аудио».
    /// </summary>
    public List<ScriptInfo> AudioScripts { get; }

    /// <summary>
    /// Список скриптов категории «Контейнеры».
    /// </summary>
    public List<ScriptInfo> ContainerScripts { get; }

    /// <summary>
    /// Список скриптов категории «Субтитры».
    /// </summary>
    public List<ScriptInfo> SubtitleScripts { get; }

    /// <summary>
    /// Инициализирует ViewModel домашней страницы.
    /// </summary>
    public HomeViewModel(INavigationService navigationService)
    {
        _navigationService = navigationService;

        var scripts = new List<ScriptInfo>
        {
            new ScriptInfo
            {
                Name = "Кодирование видео",
                Category = "Видео",
                IconName = "video",
                Description = "Кодирование видео: изменение формата, " +
                              "вшивание субтитров, фильтрация тегов " +
                              "и настройка звука",
                IsAvailable = false
            },
            new ScriptInfo
            {
                Name = "Конвертация контейнера",
                Category = "Видео",
                IconName = "forward",
                Description = "Перемещение видео/аудио потоков в " +
                              "другой контейнер без перекодирования"
            },
            new ScriptInfo
            {
                Name = "Очистка метаданных",
                Category = "Видео",
                IconName = "delete",
                Description = "Удаление метаданных из видеофайлов с " +
                              "сохранением оригинального качества"
            },
            new ScriptInfo
            {
                Name = "Кодирование аудио",
                Category = "Аудио",
                IconName = "music",
                Description = "Перекодирование аудио в QAAC, AAC, " +
                              "FLAC, WAV, E-AC3, AC3 и др. с " +
                              "настройкой качества"
            },
            new ScriptInfo
            {
                Name = "Даунмикс в Stereo",
                Category = "Аудио",
                IconName = "volume2",
                Description = "Даунмикс 5.1/7.1 в Stereo 2.0 (DDP/DD) " +
                              "через Dolby Encoding Engine"
            },
            new ScriptInfo
            {
                Name = "Изменение скорости аудио",
                Category = "Аудио",
                IconName = "sync",
                Description = "Изменение скорости/тона аудио " +
                              "(PAL ↔ NTSC) с помощью eac3to."
            },
            new ScriptInfo
            {
                Name = "Разделение каналов",
                Category = "Аудио",
                IconName = "map",
                Description = "Разделение многоканального аудио на " +
                              "моно-WAV файлы с опциональной " +
                              "склейкой в стереопары"
            },
            new ScriptInfo
            {
                Name = "Сборка MKV",
                Category = "Контейнеры",
                IconName = "add",
                Description = "Сборка контейнера MKV из отдельных " +
                              "потоков видео, аудио и субтитров с " +
                              "сопоставлением по имени"
            },
            new ScriptInfo
            {
                Name = "Управление потоками",
                Category = "Контейнеры",
                IconName = "list",
                Description = "Удаление или сохранение выбранных " +
                              "дорожек (видео, аудио, субтитры) в " +
                              "MKV и MP4 файлах."
            },
            new ScriptInfo
            {
                Name = "Замена потоков",
                Category = "Контейнеры",
                IconName = "switch",
                Description = "Заменяет дорожки в MKV/MP4 на внешние " +
                              "файлы (видео, аудио, субтитры).",
                IsAvailable = false
            },
            new ScriptInfo
            {
                Name = "Разборка контейнера",
                Category = "Контейнеры",
                IconName = "download",
                Description = "Массовое извлечение потоков из " +
                              "контейнера с авто-именованием."
            },
            new ScriptInfo
            {
                Name = "ASS/SRT → VTT",
                Category = "Субтитры",
                IconName = "font",
                Description = "Конвертация субтитров ASS/SSA/SRT в " +
                              "WebVTT с фильтрацией по актёрам и " +
                              "очисткой тегов."
            }
        };

        VideoScripts = scripts
            .Where(s => s.Category == "Видео")
            .ToList();
        AudioScripts = scripts
            .Where(s => s.Category == "Аудио")
            .ToList();
        ContainerScripts = scripts
            .Where(s => s.Category == "Контейнеры")
            .ToList();
        SubtitleScripts = scripts
            .Where(s => s.Category == "Субтитры")
            .ToList();
    }
}
