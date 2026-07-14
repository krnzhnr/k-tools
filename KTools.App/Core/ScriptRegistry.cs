// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using KTools_App.Services.Contracts;

namespace KTools_App.Core;

/// <summary>
/// Реестр всех доступных скриптов обработки файлов в приложении K-Tools.
/// Хранит экземпляры скриптов и предоставляет методы доступа к ним.
/// </summary>
public sealed class ScriptRegistry : IScriptRegistry
{
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

        _scripts.Add(_serviceProvider.GetRequiredService<Scripts.MkvAssemblyScript>());
        _scripts.Add(_serviceProvider.GetRequiredService<Scripts.StreamManagementScript>());
        _scripts.Add(_serviceProvider.GetRequiredService<Scripts.StreamReplacementScript>());
        _scripts.Add(_serviceProvider.GetRequiredService<Scripts.TrackExtractorScript>());

        _scripts.Add(_serviceProvider.GetRequiredService<Scripts.SubtitlesConvertScript>());
        _scripts.Add(_serviceProvider.GetRequiredService<Scripts.SubtitleShiftScript>());
        _scripts.Add(_serviceProvider.GetRequiredService<Scripts.MediaDownloaderScript>());

        // Инициализируем настройки скриптов по умолчанию при регистрации
        _settingsManager.InitializeDefaults(_scripts);

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
                _logService.Exception(
                    ex, 
                    $"Не удалось очистить состояние скрипта '{script.Name}' при регистрации в реестре.", 
                    "ScriptRegistry");
            }
        }
    }
}
