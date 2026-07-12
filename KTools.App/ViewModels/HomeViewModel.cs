// -*- coding: utf-8 -*-
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KTools_App.Core;
using KTools_App.Services.Contracts;
using KTools_App.UI.Pages;

namespace KTools_App.ViewModels;

/// <summary>
/// Модель представления домашней страницы со списком скриптов обработки медиа.
/// Группирует скрипты по категориям для отображения в виде карточек.
/// </summary>
public partial class HomeViewModel : ThreadSafeViewModel
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
    /// Список интерактивных инструментов (например, калькулятор таймингов).
    /// </summary>
    public List<ScriptInfo> ToolScripts { get; }

    private readonly IScriptRegistry _scriptRegistry;

    /// <summary>
    /// Инициализирует ViewModel домашней страницы.
    /// </summary>
    public HomeViewModel(INavigationService navigationService, IScriptRegistry scriptRegistry)
    {
        _navigationService = navigationService;
        _scriptRegistry = scriptRegistry;

        var scripts = new List<ScriptInfo>
        {
            new ScriptInfo
            {
                Name = "Кодирование видео",
                Category = "Видео",
                IconName = "video",
                Description = "Кодирование видео: изменение формата, " +
                              "вшивание субтитров, фильтрация тегов " +
                              "и настройка звука"
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
                              "файлы (видео, аудио, субтитры)."
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
            },
            new ScriptInfo
            {
                Name = "Калькулятор сдвига",
                Category = "Инструменты",
                IconName = "calculator",
                Description = "Расчет разницы во времени между двумя таймингами " +
                              "для корректировки сдвига аудио и субтитров."
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
        ToolScripts = scripts
            .Where(s => s.Category == "Инструменты")
            .ToList();
    }

    /// <summary>
    /// Выполняет переход к экрану выполнения скрипта через службу навигации.
    /// </summary>
    [RelayCommand]
    private void NavigateToScript(string scriptName)
    {
        if (scriptName == "Калькулятор сдвига")
        {
            _navigationService.NavigateTo(typeof(TimingCalculatorPage));
            return;
        }

        var script = _scriptRegistry.GetScriptByName(scriptName);
        if (script != null)
        {
            _navigationService.NavigateTo(typeof(WorkPanel), script);
        }
    }
}
