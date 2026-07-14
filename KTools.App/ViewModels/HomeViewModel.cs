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
                Name = AppConstants.ScriptMetadata.VideoProcessorName,
                Category = AppConstants.ScriptCategory.Video,
                IconName = AppConstants.ScriptIcons.VideoEncoding,
                Description = AppConstants.ScriptMetadata.VideoProcessorDesc
            },
            new ScriptInfo
            {
                Name = AppConstants.ScriptMetadata.ContainerConvName,
                Category = AppConstants.ScriptCategory.Video,
                IconName = AppConstants.ScriptIcons.ContainerConversion,
                Description = AppConstants.ScriptMetadata.ContainerConvDesc
            },
            new ScriptInfo
            {
                Name = AppConstants.ScriptMetadata.MetadataCleanName,
                Category = AppConstants.ScriptCategory.Video,
                IconName = AppConstants.ScriptIcons.MetadataCleanup,
                Description = AppConstants.ScriptMetadata.MetadataCleanDesc
            },
            new ScriptInfo
            {
                Name = AppConstants.ScriptMetadata.AudioConverterName,
                Category = AppConstants.ScriptCategory.Audio,
                IconName = AppConstants.ScriptIcons.AudioEncoding,
                Description = AppConstants.ScriptMetadata.AudioConverterDesc
            },
            new ScriptInfo
            {
                Name = AppConstants.ScriptMetadata.AudioDownmixName,
                Category = AppConstants.ScriptCategory.Audio,
                IconName = AppConstants.ScriptIcons.AudioDownmix,
                Description = AppConstants.ScriptMetadata.AudioDownmixDesc
            },
            new ScriptInfo
            {
                Name = AppConstants.ScriptMetadata.AudioSpeedName,
                Category = AppConstants.ScriptCategory.Audio,
                IconName = AppConstants.ScriptIcons.AudioSpeed,
                Description = AppConstants.ScriptMetadata.AudioSpeedDesc
            },
            new ScriptInfo
            {
                Name = AppConstants.ScriptMetadata.AudioSplitName,
                Category = AppConstants.ScriptCategory.Audio,
                IconName = AppConstants.ScriptIcons.AudioChannels,
                Description = AppConstants.ScriptMetadata.AudioSplitDesc
            },
            new ScriptInfo
            {
                Name = AppConstants.ScriptMetadata.AudioShiftName,
                Category = AppConstants.ScriptCategory.Audio,
                IconName = AppConstants.ScriptIcons.AudioShift,
                Description = AppConstants.ScriptMetadata.AudioShiftDesc
            },
            new ScriptInfo
            {
                Name = AppConstants.ScriptMetadata.MuxerName,
                Category = AppConstants.ScriptCategory.Containers,
                IconName = AppConstants.ScriptIcons.MkvAssembly,
                Description = AppConstants.ScriptMetadata.MuxerDesc
            },
            new ScriptInfo
            {
                Name = AppConstants.ScriptMetadata.StreamMgrName,
                Category = AppConstants.ScriptCategory.Containers,
                IconName = AppConstants.ScriptIcons.StreamManagement,
                Description = AppConstants.ScriptMetadata.StreamMgrDesc
            },
            new ScriptInfo
            {
                Name = AppConstants.ScriptMetadata.StreamReplName,
                Category = AppConstants.ScriptCategory.Containers,
                IconName = AppConstants.ScriptIcons.StreamReplacement,
                Description = AppConstants.ScriptMetadata.StreamReplDesc
            },
            new ScriptInfo
            {
                Name = AppConstants.ScriptMetadata.TrackExtrName,
                Category = AppConstants.ScriptCategory.Containers,
                IconName = AppConstants.ScriptIcons.TrackExtractor,
                Description = AppConstants.ScriptMetadata.TrackExtrDesc
            },
            new ScriptInfo
            {
                Name = AppConstants.ScriptMetadata.AssToVttName,
                Category = AppConstants.ScriptCategory.Subtitles,
                IconName = AppConstants.ScriptIcons.SubtitlesConvert,
                Description = AppConstants.ScriptMetadata.AssToVttDesc
            },
            new ScriptInfo
            {
                Name = AppConstants.ScriptMetadata.SubtitleShiftName,
                Category = AppConstants.ScriptCategory.Subtitles,
                IconName = AppConstants.ScriptIcons.SubtitlesShift,
                Description = AppConstants.ScriptMetadata.SubtitleShiftDesc
            },
            new ScriptInfo
            {
                Name = "Калькулятор сдвига",
                Category = "Инструменты",
                IconName = AppConstants.ScriptIcons.Calculator,
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
