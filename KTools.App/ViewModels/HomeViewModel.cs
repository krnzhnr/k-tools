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
/// Динамически группирует ВСЕ скрипты из реестра ScriptRegistry по категориям для отображения в виде карточек.
/// Все комментарии и логи исключительно на русском языке в соответствии с регламентом.
/// </summary>
public partial class HomeViewModel : ThreadSafeViewModel
{
    private readonly INavigationService _navigationService;
    private readonly IScriptRegistry _scriptRegistry;

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
    /// Список скриптов категории «Сеть».
    /// </summary>
    public List<ScriptInfo> NetworkScripts { get; }

    /// <summary>
    /// Список интерактивных инструментов (например, калькулятор таймингов).
    /// </summary>
    public List<ScriptInfo> ToolScripts { get; }

    /// <summary>
    /// Инициализирует ViewModel домашней страницы и динамически формирует карточки скриптов.
    /// </summary>
    public HomeViewModel(INavigationService navigationService, IScriptRegistry scriptRegistry)
    {
        _navigationService = navigationService;
        _scriptRegistry = scriptRegistry;

        // Динамическое получение всех доступных скриптов из централизованного реестра
        var allScripts = _scriptRegistry.Scripts.Select(s => new ScriptInfo
        {
            Name = s.Name,
            Category = s.Category,
            IconName = s.IconName,
            Description = s.Description
        }).ToList();

        VideoScripts = allScripts.Where(s => s.Category == AppConstants.ScriptCategory.Video).ToList();
        AudioScripts = allScripts.Where(s => s.Category == AppConstants.ScriptCategory.Audio).ToList();
        ContainerScripts = allScripts.Where(s => s.Category == AppConstants.ScriptCategory.Containers).ToList();
        SubtitleScripts = allScripts.Where(s => s.Category == AppConstants.ScriptCategory.Subtitles).ToList();
        NetworkScripts = allScripts.Where(s => s.Category == AppConstants.ScriptCategory.Network).ToList();

        // Формируем список инструментов (включая калькулятор и любые пользовательские скрипты категории "Инструменты")
        ToolScripts = new List<ScriptInfo>
        {
            new ScriptInfo
            {
                Name = "Калькулятор сдвига",
                Category = AppConstants.ScriptCategory.Tools,
                IconName = AppConstants.ScriptIcons.Calculator,
                Description = "Расчёт разницы во времени между двумя таймингами для корректировки сдвига аудио и субтитров"
            }
        };
        ToolScripts.AddRange(allScripts.Where(s => s.Category == AppConstants.ScriptCategory.Tools));
    }

    /// <summary>
    /// Команда перехода на рабочий экран выбранного скрипта.
    /// Поддерживает передачу объекта ScriptInfo или названия скрипта string.
    /// </summary>
    [RelayCommand]
    private void NavigateToScript(object? arg)
    {
        if (arg is ScriptInfo script)
        {
            if (script.Name == "Калькулятор сдвига")
            {
                _navigationService.NavigateTo(typeof(TimingCalculatorPage));
                return;
            }

            var realScript = _scriptRegistry.GetScriptByName(script.Name);
            if (realScript != null)
            {
                _navigationService.NavigateTo(typeof(WorkPanel), realScript);
            }
        }
        else if (arg is string scriptName)
        {
            if (scriptName == "Калькулятор сдвига")
            {
                _navigationService.NavigateTo(typeof(TimingCalculatorPage));
                return;
            }

            var realScript = _scriptRegistry.GetScriptByName(scriptName);
            if (realScript != null)
            {
                _navigationService.NavigateTo(typeof(WorkPanel), realScript);
            }
        }
    }
}
