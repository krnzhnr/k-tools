// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
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
    /// Имя синтетической карточки без реального скрипта: дроп на неё не принимается.
    /// </summary>
    private const string CalculatorScriptName = "Калькулятор сдвига";

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
            if (script.Name == CalculatorScriptName)
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
            if (scriptName == CalculatorScriptName)
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

    /// <summary>
    /// Разрешает целевой скрипт для приёма дропа на карточке домашнего экрана.
    /// Возвращает null для недоступных карточек, синтетической карточки калькулятора
    /// и неизвестных имён: такие карточки файлы не принимают.
    /// </summary>
    /// <param name="info">Карточка, на которую перетаскивают файлы.</param>
    /// <returns>Скрипт реестра или null, если дроп принимать нельзя.</returns>
    public AbstractScript? ResolveDropScript(ScriptInfo? info)
    {
        if (info is null || !info.IsAvailable)
        {
            return null;
        }

        if (string.Equals(info.Name, CalculatorScriptName, StringComparison.Ordinal))
        {
            return null;
        }

        return _scriptRegistry.GetScriptByName(info.Name);
    }

    /// <summary>
    /// Отфильтровывает пути файлов по допустимым расширениям скрипта (без учёта регистра).
    /// Скрипт без списка расширений принимает все файлы, как диалог выбора файлов.
    /// </summary>
    /// <param name="script">Целевой скрипт.</param>
    /// <param name="paths">Пути-кандидаты для добавления в очередь.</param>
    /// <returns>Только совместимые непустые пути.</returns>
    public IReadOnlyList<string> FilterDropPaths(AbstractScript script, IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(paths);

        var cleaned = paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (script.FileExtensions is not { Length: > 0 })
        {
            return cleaned;
        }

        var allowed = new HashSet<string>(script.FileExtensions, StringComparer.OrdinalIgnoreCase);
        return cleaned.Where(p => allowed.Contains(Path.GetExtension(p))).ToList();
    }

    /// <summary>
    /// Запоминает совместимые пути для скрипта карточки, чтобы WorkPanel забрал их при навигации.
    /// Возвращает false, если дроп принимать нельзя или ни один файл не подошёл по расширению.
    /// </summary>
    /// <param name="info">Карточка, на которую сбросили файлы.</param>
    /// <param name="paths">Пути сброшенных файлов.</param>
    /// <returns>True — пути запомнены, можно переходить к скрипту.</returns>
    public bool TryStageCardDrop(ScriptInfo? info, IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var script = ResolveDropScript(info);
        if (script is null)
        {
            return false;
        }

        var compatible = FilterDropPaths(script, paths);
        if (compatible.Count == 0)
        {
            return false;
        }

        CardDropHandoff.Stage(script.Name, compatible);
        return true;
    }
}
