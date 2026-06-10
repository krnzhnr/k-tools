// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;

namespace KTools_App.Core;

/// <summary>
/// Реестр всех доступных скриптов обработки файлов в приложении K-Tools.
/// Хранит экземпляры скриптов и предоставляет методы доступа к ним.
/// </summary>
public sealed class ScriptRegistry
{
    private static readonly Lazy<ScriptRegistry> LazyInstance =
        new(() => new ScriptRegistry());

    private readonly List<AbstractScript> _scripts;

    private ScriptRegistry()
    {
        _scripts = new List<AbstractScript>();
        RegisterScripts();
    }

    /// <summary>
    /// Возвращает единственный экземпляр класса ScriptRegistry.
    /// </summary>
    public static ScriptRegistry Instance => LazyInstance.Value;

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
        // В будущем здесь будут регистрироваться все реальные классы скриптов.
        // Сейчас регистрируем первый рабочий скрипт "Очистка метаданных" и заглушки для остальных.
        _scripts.Add(new Scripts.MetadataCleanupScript());
        _scripts.Add(new Scripts.VideoEncodingStub());
        _scripts.Add(new Scripts.ContainerConversionScript());
        
        _scripts.Add(new Scripts.AudioEncodingScript());
        _scripts.Add(new Scripts.AudioDownmixScript());
        _scripts.Add(new Scripts.AudioSpeedScript());
        _scripts.Add(new Scripts.AudioChannelsScript());

        _scripts.Add(new Scripts.MkvAssemblyStub());
        _scripts.Add(new Scripts.StreamManagementStub());
        _scripts.Add(new Scripts.StreamReplacementStub());
        _scripts.Add(new Scripts.TrackExtractorScript());

        _scripts.Add(new Scripts.SubtitlesConvertScript());

        // Инициализируем настройки скриптов по умолчанию при регистрации
        SettingsManager.Instance.InitializeDefaults(_scripts);

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
                LogService.Instance.Exception(
                    ex, 
                    $"Не удалось очистить состояние скрипта '{script.Name}' при регистрации в реестре.", 
                    "ScriptRegistry");
            }
        }
    }
}
