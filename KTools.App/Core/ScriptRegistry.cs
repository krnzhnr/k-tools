// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;

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
    private readonly List<AbstractScript> _scripts;

    /// <summary>
    /// Инициализирует новый экземпляр класса ScriptRegistry с внедрением зависимостей.
    /// </summary>
    public ScriptRegistry(IServiceProvider serviceProvider, ISettingsManager settingsManager, ILogService logService)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _scripts = new List<AbstractScript>();
        RegisterScripts();
    }



    /// <summary>
    /// Возвращает полный список зарегистрированных скриптов.
    /// </summary>
    public List<AbstractScript> Scripts => _scripts;

    /// <summary>
    /// Получить скрипт по его уникальному названию.
    /// </summary>
    public AbstractScript? GetScriptByName(string name)
    {
        return _scripts.FirstOrDefault(s => s.Name.Equals(
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
    /// Регистрация всех 12 оригинальных скриптов обработки медиа.
    /// </summary>
    private void RegisterScripts()
    {
        // Получаем зарегистрированные скрипты через IServiceProvider
        _scripts.Add(_serviceProvider.GetRequiredService<Scripts.MetadataCleanupScript>());
        _scripts.Add(_serviceProvider.GetRequiredService<Scripts.VideoEncodingScript>());
        _scripts.Add(_serviceProvider.GetRequiredService<Scripts.ContainerConversionScript>());

        _scripts.Add(_serviceProvider.GetRequiredService<Scripts.AudioEncodingScript>());
        _scripts.Add(_serviceProvider.GetRequiredService<Scripts.AudioDownmixScript>());
        _scripts.Add(_serviceProvider.GetRequiredService<Scripts.AudioSpeedScript>());
        _scripts.Add(_serviceProvider.GetRequiredService<Scripts.AudioChannelsScript>());
        _scripts.Add(_serviceProvider.GetRequiredService<Scripts.AudioShiftScript>());
        _scripts.Add(_serviceProvider.GetRequiredService<Scripts.AudioTransplantScript>());

        _scripts.Add(_serviceProvider.GetRequiredService<Scripts.MkvAssemblyScript>());
        _scripts.Add(_serviceProvider.GetRequiredService<Scripts.StreamManagementScript>());
        _scripts.Add(_serviceProvider.GetRequiredService<Scripts.StreamReplacementScript>());
        _scripts.Add(_serviceProvider.GetRequiredService<Scripts.TrackExtractorScript>());

        _scripts.Add(_serviceProvider.GetRequiredService<Scripts.SubtitlesConvertScript>());
        _scripts.Add(_serviceProvider.GetRequiredService<Scripts.SubtitleShiftScript>());
        _scripts.Add(_serviceProvider.GetRequiredService<Scripts.SpeechRecognitionScript>());
        _scripts.Add(_serviceProvider.GetRequiredService<Scripts.MediaDownloaderScript>());
        _scripts.Add(_serviceProvider.GetRequiredService<Scripts.BitrateViewerScript>());

        // Инициализируем настройки скриптов по умолчанию при регистрации.
        // Результат обязательно потребляется: ошибка не остаётся молчаливой.
        RecordDefaultsOutcome(_settingsManager.InitializeDefaults(_scripts), _logService);

        // Явно гарантируем сброс всех очередей файлов и выбранных дорожек
        // для обеспечения запуска приложения с абсолютно чистого листа
        foreach (var script in _scripts)
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
