// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using KTools_App.Diagnostics;
using KTools_App.Services.Contracts;

using Microsoft.Extensions.DependencyInjection;

namespace KTools_App.Core;

/// <summary>
/// Реестр всех доступных скриптов обработки файлов в приложении K-Tools.
/// Хранит экземпляры скриптов и предоставляет методы доступа к ним.
/// </summary>
public sealed class ScriptRegistry : IScriptRegistry
{
    private const string SourceName = nameof(ScriptRegistry);

    private readonly IServiceProvider _serviceProvider;
    private readonly ISettingsManager _settingsManager;
    private readonly ILogService _logService;

    // Ленивое хранилище скриптов: построение выполняется один раз при первом
    // доступе к Scripts / GetScriptByName, потокобезопасно. Создание реестра
    // в DI не создаёт ни одного скрипта и не обращается к диску.
    private readonly Lazy<List<AbstractScript>> _scripts;

    /// <summary>
    /// Инициализирует новый экземпляр класса ScriptRegistry с внедрением зависимостей.
    /// Скрипты не создаются в конструкторе: построение отложено до первого доступа.
    /// </summary>
    public ScriptRegistry(IServiceProvider serviceProvider, ISettingsManager settingsManager, ILogService logService)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _scripts = new Lazy<List<AbstractScript>>(BuildScripts, LazyThreadSafetyMode.ExecutionAndPublication);
    }



    /// <summary>
    /// Возвращает полный список зарегистрированных скриптов.
    /// При первом доступе список строится один раз, повторные доступы возвращают построенное.
    /// </summary>
    public List<AbstractScript> Scripts => _scripts.Value;

    /// <summary>
    /// Получить скрипт по его уникальному названию.
    /// При первом доступе инициирует построение всего списка скриптов.
    /// </summary>
    public AbstractScript? GetScriptByName(string name)
    {
        return _scripts.Value.FirstOrDefault(s => s.Name.Equals(
            name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Потребляет типизированный результат инициализации настроек по умолчанию при старте.
    /// Результат не отбрасывается: успех и любая типизированная ошибка попадают в журнал,
    /// поэтому молчаливая потеря настроек исключена. Блокирующий диалог не показывается.
    /// Событие «по умолчанию записаны» используется только при реальном Persisted=true;
    /// отложенные и неуспешные исходы получают отдельный идентификатор события.
    /// </summary>
    /// <param name="result">Результат вызова ISettingsManager.InitializeDefaults (может быть null).</param>
    /// <param name="logService">Журнал приложения.</param>
    /// <returns>Нормализованный результат без пустого (ложно-успешного) значения.</returns>
    public static PersistenceResult RecordDefaultsOutcome(
        PersistenceResult? result,
        ILogService logService)
    {
        ArgumentNullException.ThrowIfNull(logService);

        PersistenceResult normalized = PersistenceResult.Normalize(result);
        var properties = new Dictionary<string, object?>(6, StringComparer.Ordinal)
        {
            ["Count"] = normalized.ChangedCount,
            ["Changed"] = normalized.HasChanges,
            ["ErrorCode"] = normalized.ErrorCode,
            ["Failed"] = normalized.IsFailure,
            ["Persisted"] = normalized.Persisted,
            ["Succeeded"] = normalized.IsSuccess
        };

        if (normalized.IsFailure)
        {
            logService.Write(
                SettingsEventIds.DefaultsDeferred,
                LogLevel.Error,
                LogStatus.Failed,
                normalized.UserSummary,
                null,
                "ScriptRegistry",
                null,
                properties.With("ErrorCode", normalized.ErrorCode));
            return normalized;
        }

        if (normalized.IsPending)
        {
            logService.Write(
                SettingsEventIds.DefaultsDeferred,
                LogLevel.Warning,
                LogStatus.Changed,
                normalized.UserSummary,
                null,
                "ScriptRegistry",
                null,
                properties);
            return normalized;
        }

        if (!normalized.Persisted)
        {
            logService.Write(
                SettingsEventIds.DefaultsDeferred,
                LogLevel.Warning,
                LogStatus.Changed,
                normalized.UserSummary,
                null,
                "ScriptRegistry",
                null,
                properties);
            return normalized;
        }

        logService.Write(
            SettingsEventIds.DefaultsPersisted,
            LogLevel.Info,
            LogStatus.Succeeded,
            "Настройки скриптов по умолчанию подтверждены записью на диск",
            null,
            "ScriptRegistry",
            null,
            properties);
        return normalized;
    }

    /// <summary>
    /// Лениво строит полный список скриптов при первом доступе.
    /// Точка регистрации новых скриптов: каждый новый скрипт добавляется здесь
    /// через _serviceProvider.GetRequiredService. Вызывается один раз фабрикой
    /// ленивого хранилища, поэтому повторные доступы работу не повторяют.
    /// Инициализация настроек по умолчанию отложена до этого же прохода и не
    /// требует Persisted=true: отложенный исход принимается через
    /// RecordDefaultsOutcome. Защита деградированной загрузки сохраняется:
    /// запись поверх повреждённого файла выполняет только явный ResetToDefaults
    /// внутри SettingsManager, этот метод напрямую на диск не пишет.
    /// </summary>
    private List<AbstractScript> BuildScripts()
    {
        // Создаём локальный список: поле публикуется только после полной сборки,
        // поэтому наблюдатели никогда не видят частично построенный реестр.
        List<AbstractScript> scripts = new(18);

        // Получаем зарегистрированные скрипты через IServiceProvider
        scripts.Add(_serviceProvider.GetRequiredService<Scripts.MetadataCleanupScript>());
        scripts.Add(_serviceProvider.GetRequiredService<Scripts.VideoEncodingScript>());
        scripts.Add(_serviceProvider.GetRequiredService<Scripts.ContainerConversionScript>());

        scripts.Add(_serviceProvider.GetRequiredService<Scripts.AudioEncodingScript>());
        scripts.Add(_serviceProvider.GetRequiredService<Scripts.AudioDownmixScript>());
        scripts.Add(_serviceProvider.GetRequiredService<Scripts.AudioSpeedScript>());
        scripts.Add(_serviceProvider.GetRequiredService<Scripts.AudioChannelsScript>());
        scripts.Add(_serviceProvider.GetRequiredService<Scripts.AudioShiftScript>());
        scripts.Add(_serviceProvider.GetRequiredService<Scripts.AudioTransplantScript>());

        scripts.Add(_serviceProvider.GetRequiredService<Scripts.MkvAssemblyScript>());
        scripts.Add(_serviceProvider.GetRequiredService<Scripts.StreamManagementScript>());
        scripts.Add(_serviceProvider.GetRequiredService<Scripts.StreamReplacementScript>());
        scripts.Add(_serviceProvider.GetRequiredService<Scripts.TrackExtractorScript>());

        scripts.Add(_serviceProvider.GetRequiredService<Scripts.SubtitlesConvertScript>());
        scripts.Add(_serviceProvider.GetRequiredService<Scripts.SubtitleShiftScript>());
        scripts.Add(_serviceProvider.GetRequiredService<Scripts.SpeechRecognitionScript>());
        scripts.Add(_serviceProvider.GetRequiredService<Scripts.MediaDownloaderScript>());
        scripts.Add(_serviceProvider.GetRequiredService<Scripts.BitrateViewerScript>());

        // Инициализируем настройки скриптов по умолчанию в памяти при первом доступе.
        // Результат обязательно потребляется: ошибка не остаётся молчаливой.
        // Отложенный исход не считается сбоем: Persisted=true не требуется.
        RecordDefaultsOutcome(_settingsManager.InitializeDefaults(scripts), _logService);

        ResetScriptStates(scripts);

        return scripts;
    }

    /// <summary>
    /// Сбрасывает состояние фактически созданных скриптов в том же ленивом проходе,
    /// для обеспечения запуска с абсолютно чистого листа.
    /// </summary>
    private void ResetScriptStates(List<AbstractScript> scripts)
    {
        // Явно гарантируем сброс всех очередей файлов и выбранных дорожек
        // только для фактически созданных скриптов
        foreach (var script in scripts)
        {
            try
            {
                script.FilesQueue.Clear();
                script.SelectedTrackIds.Clear();
                script.SelectedAttachmentIds.Clear();
                script.SavedLogText = string.Empty;
                script.SavedStatusText = "Ожидание запуска...";
                script.SavedGlobalProgress = 0.0;
                script.IsProcessing = false;
            }
            catch (Exception ex)
            {
                _logService.Write(
                    "script.registry.state_reset_failed",
                    LogLevel.Warning,
                    LogStatus.Failed,
                    $"Состояние скрипта '{script.Name}' не очищено при регистрации в реестре",
                    ex,
                    SourceName,
                    properties: LogProps
                        .Create("ScriptId", LogRedactor.CompactSafeToken(script.Name))
                        .With("ErrorCode", "SCRIPT_STATE_RESET_FAILED"));
            }
        }
    }
}
